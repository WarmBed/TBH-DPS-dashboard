using System;
using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>Localized item names, keyed by the in-game ItemKey, shipped from the wiki's items.json
    /// (trimmed to the supported languages). This is the STABLE source for gear names: the in-memory
    /// item-name lookup (tf.ipp) breaks on every game-update obfuscation pass, but ItemKey↔name is
    /// fixed data. Embedded in the DLL — offline, no extra files.</summary>
    internal static class ItemNameStore
    {
        // id (string) -> { langCode -> name }
        private static Dictionary<string, object> _map;

        private static Dictionary<string, object> Map()
        {
            if (_map != null) return _map;
            _map = new Dictionary<string, object>();
            try
            {
                var asm = typeof(ItemNameStore).Assembly;
                string name = null;
                foreach (var n in asm.GetManifestResourceNames())
                    if (n.EndsWith("item_names.json", StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                if (name != null)
                    using (var s = asm.GetManifestResourceStream(name))
                    using (var r = new System.IO.StreamReader(s))
                        _map = Json.Obj(Json.Parse(r.ReadToEnd())) ?? new Dictionary<string, object>();
                Plugin.Logger?.LogInfo($"[items] loaded {_map.Count} item names from embedded data");
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("ItemNameStore: " + e.Message); }
            return _map;
        }

        /// <summary>Localized name for an item key in the current game language. An ItemKey the bundled
        /// table has never seen (a level tier the game shipped after the wiki scrape — the wiki's own
        /// pre-release datamine already named the tier, just under a different guessed ItemKey) borrows the
        /// name from <see cref="ItemMetaStore.FamilyBestItemKey"/>, the highest-level known sibling in the
        /// same slot+grade family. Falls back to the live game's own item-table localizer (see
        /// <see cref="ResolveLabel"/>) only if that also comes up empty; "" if nothing knows it.</summary>
        public static string Get(int itemKey)
        {
            string direct = DirectGet(itemKey);
            if (!string.IsNullOrEmpty(direct)) return direct;
            int bestKey = ItemMetaStore.FamilyBestItemKey(itemKey);
            if (bestKey > 0 && bestKey != itemKey)
            {
                string fam = DirectGet(bestKey);
                if (!string.IsNullOrEmpty(fam)) return fam;
            }
            return HeroProbe.GameLocItem(itemKey.ToString());
        }

        private static string DirectGet(int itemKey)
        {
            if (itemKey <= 0) return "";
            var names = Json.Obj(Json.Get(Map(), itemKey.ToString()));
            if (names == null) return "";
            string lang = Loc.WikiLangCode();
            string s = Json.Str(Json.Get(names, lang));
            if (!string.IsNullOrEmpty(s)) return s;
            return Json.Str(Json.Get(names, "en-US")) ?? "";   // fall back to English
        }

        // resolved-label cache (loc-key string -> display name). Only POSITIVE results are cached, so a key
        // unresolved because the live localizer wasn't ready yet is retried on the next frame.
        private static readonly Dictionary<string, string> _labelCache = new Dictionary<string, string>();

        /// <summary>Resolve a stored box-open item label — a loc key ("ItemName_620014") or bare id — to a
        /// localized display name: the bundled table first, then the live game item-table localizer for items
        /// added/renamed after this build (e.g. named rings the wiki lacks). Falls back to the raw input.</summary>
        public static string ResolveLabel(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            if (_labelCache.TryGetValue(key, out var cached)) return cached;
            int us = key.LastIndexOf('_');
            string digits = us >= 0 ? key.Substring(us + 1) : key;
            if (!int.TryParse(digits, out int id)) return key;   // already a display name, not a key
            string nm = Get(id);
            if (!string.IsNullOrEmpty(nm)) { _labelCache[key] = nm; return nm; }   // bundled hit (stable)
            string live = HeroProbe.GameLocItem(key);
            if (!string.IsNullOrEmpty(live) && live != key) { _labelCache[key] = live; return live; }
            return key;   // not resolved yet — don't cache, retry next frame
        }

        /// <summary>Every ItemKey the bundled table knows, for callers that need to build a reverse
        /// (name -> something) lookup — e.g. classifying a box-open log entry, which only carries a
        /// display name, back to its item category.</summary>
        public static IEnumerable<int> AllKeys()
        {
            foreach (var k in Map().Keys)
                if (int.TryParse(k, out int id)) yield return id;
        }

        /// <summary>English (en-US) name for an item key; falls back to the family's highest-level known
        /// sibling (see <see cref="Get"/>/<see cref="ItemMetaStore.FamilyBestItemKey"/>) if this exact
        /// ItemKey is unknown — a Steam market hash_name still needs the English name even for a tier the
        /// wiki mis-numbered, or price lookups for it silently miss (e.g. a blank name flattens to hash
        /// " (Rare) A", which never matches). "" if the whole family is unknown.</summary>
        public static string GetEn(int itemKey)
        {
            string en = DirectGetEn(itemKey);
            if (!string.IsNullOrEmpty(en)) return en;
            int bestKey = ItemMetaStore.FamilyBestItemKey(itemKey);
            return bestKey > 0 && bestKey != itemKey ? DirectGetEn(bestKey) : "";
        }

        private static string DirectGetEn(int itemKey)
        {
            if (itemKey <= 0) return "";
            var names = Json.Obj(Json.Get(Map(), itemKey.ToString()));
            if (names == null) return "";
            return Json.Str(Json.Get(names, "en-US")) ?? "";
        }
    }
}
