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

        /// <summary>Localized name for an item key in the current game language; "" if unknown.</summary>
        public static string Get(int itemKey)
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

        /// <summary>English (en-US) name for an item key; "" if unknown. Steam market hash_names are in
        /// English, so this is what we match against PriceStore.</summary>
        public static string GetEn(int itemKey)
        {
            if (itemKey <= 0) return "";
            var names = Json.Obj(Json.Get(Map(), itemKey.ToString()));
            if (names == null) return "";
            return Json.Str(Json.Get(names, "en-US")) ?? "";
        }
    }
}
