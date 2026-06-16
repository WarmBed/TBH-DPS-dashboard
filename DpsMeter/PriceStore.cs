using System;
using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;

namespace TbhDpsMeter
{
    /// <summary>Steam Community Market price snapshot for the game's items, fetched lazily from our
    /// cron-built prices.json on jsDelivr. Keyed by the Steam market hash_name (e.g. "Emerald" for
    /// materials, "Chain Boots (Legendary) A" for gear). All network is async/off the game thread and
    /// degrades silently on failure. Loaded once per session; call EnsureLoaded() before lookups.</summary>
    public static class PriceStore
    {
        public enum St { Idle, Loading, Ready, Error }
        public static volatile St State = St.Idle;
        public static string Currency = "$";
        public static long CachedAtMs;   // ms since unix epoch, from prices.json

        /// <summary>Per-item market data from prices.json.</summary>
        public sealed class Info
        {
            public int Cents;          // current lowest sell price (cents)
            public int Qty;            // live listing count
            public int PrevCents = -1; // price ~24h ago (-1 = unknown, before 24h of cron history)
            public int[] HistC;        // 7-day price series (cents) for the sparkline, or null
            public int[] HistT;        // matching unix-second timestamps (or null if the feed had no times)
            public int Vol = -1;       // units sold in the last 24h (-1 = unknown; refreshed ~every 6h)
            public int MedianCents = -1; // median sale price in cents (-1 = unknown)
        }

        // hash_name -> Info. Case-insensitive. Swapped atomically on refresh (readers keep the old map
        // until the new one is fully built), so a hovered item never blinks to "no price" mid-reload.
        private static volatile Dictionary<string, Info> _items =
            new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);

        // The cron rebuilds prices.json every 30 min; re-fetch on that cadence so a long-running game
        // picks up newly-listed items (e.g. a freshly tradable Soulstone) without needing a restart.
        private static long _lastAttemptMs;
        private const long RefreshMs = 30 * 60 * 1000;

        // raw.githubusercontent refreshes within ~minutes (the cron relies on it too); jsDelivr's branch
        // cache lags our 30-min updates badly, so it's only the fallback if raw is unreachable.
        private const string Url = "https://raw.githubusercontent.com/WarmBed/TBH-DPS-dashboard/data/prices.json";
        private const string UrlFallback = "https://cdn.jsdelivr.net/gh/WarmBed/TBH-DPS-dashboard@data/prices.json";
        private const string Ua = "TBH-DpsMeter-Prices";

        /// <summary>Kick off the one-time async fetch. Safe to call every frame.</summary>
        public static void EnsureLoaded()
        {
            long now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (State == St.Loading) return;
            // first load when Idle; otherwise re-fetch only once the feed is ~30 min stale
            if (State != St.Idle && now - _lastAttemptMs < RefreshMs) return;
            _lastAttemptMs = now;
            State = St.Loading;
            Task.Run(async () =>
            {
                try
                {
                    using (var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) })
                    {
                        http.DefaultRequestHeaders.Add("User-Agent", Ua);
                        string json;
                        try { json = await http.GetStringAsync(Url); }
                        catch { json = await http.GetStringAsync(UrlFallback); }   // raw down -> jsDelivr
                        var o = Json.Parse(json);
                        Currency = Json.Str(Json.Get(o, "currency")) ?? "$";
                        CachedAtMs = Json.Long(Json.Get(o, "cachedAt"));
                        var items = Json.Obj(Json.Get(o, "items"));
                        // build into a fresh map and swap in at the end (don't mutate the live one)
                        var fresh = new Dictionary<string, Info>(StringComparer.OrdinalIgnoreCase);
                        int n = 0;
                        if (items != null)
                            foreach (var kv in items)
                            {
                                var v = Json.Obj(kv.Value);
                                if (v == null) continue;
                                var info = new Info();
                                info.Cents = (int)Json.Long(Json.Get(v, "lowestCents"));
                                info.Qty = (int)Json.Long(Json.Get(v, "qty"));
                                var pcv = Json.Get(v, "prevCents");
                                info.PrevCents = pcv != null ? (int)Json.Long(pcv) : -1;
                                var volv = Json.Get(v, "vol");
                                info.Vol = volv != null ? (int)Json.Long(volv) : -1;
                                var medv = Json.Get(v, "medianCents");
                                info.MedianCents = medv != null ? (int)Json.Long(medv) : -1;
                                var harr = Json.Arr(Json.Get(v, "hist"));
                                if (harr != null && harr.Count > 0)
                                {
                                    var cs = new System.Collections.Generic.List<int>(harr.Count);
                                    var ts = new System.Collections.Generic.List<int>(harr.Count);
                                    foreach (var e in harr)
                                    {
                                        var pair = Json.Arr(e);   // new format: [tSec, cents]
                                        if (pair != null && pair.Count >= 2) { ts.Add((int)Json.Long(pair[0])); cs.Add((int)Json.Long(pair[1])); }
                                        else cs.Add((int)Json.Long(e));   // old flat format: just cents
                                    }
                                    if (cs.Count > 0) { info.HistC = cs.ToArray(); if (ts.Count == cs.Count) info.HistT = ts.ToArray(); }
                                }
                                fresh[kv.Key] = info;
                                n++;
                            }
                        if (n > 0) _items = fresh;   // atomic swap; keep the old map if the feed came back empty
                        State = St.Ready;
                        Plugin.Logger?.LogInfo($"[prices] loaded {n} items, currency='{Currency}'");
                    }
                }
                // on failure keep serving whatever we already had; only flag Error if we never loaded once
                catch (Exception e) { State = _items.Count > 0 ? St.Ready : St.Error; Plugin.Logger?.LogWarning("[prices] load failed: " + e.Message); }
            });
        }

        /// <summary>Lowest sell price in cents + listings count + price ~24h ago (prevCents = -1 if unknown,
        /// e.g. before the cron has 24h of history) for a Steam hash_name. False if the item isn't listed.</summary>
        public static bool TryGet(string hashName, out int cents, out int qty, out int prevCents)
        {
            cents = 0; qty = 0; prevCents = -1;
            if (string.IsNullOrEmpty(hashName)) return false;
            if (_items.TryGetValue(hashName, out var v)) { cents = v.Cents; qty = v.Qty; prevCents = v.PrevCents; return true; }
            return false;
        }

        /// <summary>Full market record for a hash_name, or null if the item isn't listed.</summary>
        public static Info Get(string hashName)
            => (!string.IsNullOrEmpty(hashName) && _items.TryGetValue(hashName, out var v)) ? v : null;

        /// <summary>Format cents as a currency string, e.g. 1234 -> "$12.34".</summary>
        public static string Format(int cents)
        {
            return Currency + (cents / 100.0).ToString("0.00");
        }
    }
}
