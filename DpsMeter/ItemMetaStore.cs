using System;
using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>Per-item template metadata (grade / item level / icon path) keyed by ItemKey, shipped
    /// from the wiki's items.json (GEAR only, trimmed). Grade and level are template properties — the
    /// save instance doesn't carry them and the in-memory item can't be fetched by save uid — so this
    /// stable ItemKey↔meta table is the source. Embedded in the DLL; offline, no extra files.</summary>
    internal static class ItemMetaStore
    {
        // itemKey(string) -> { "g": grade, "l": level, "i": iconPath }
        private static Dictionary<string, object> _map;

        private static Dictionary<string, object> Map()
        {
            if (_map != null) return _map;
            _map = new Dictionary<string, object>();
            try
            {
                var asm = typeof(ItemMetaStore).Assembly;
                string name = null;
                foreach (var n in asm.GetManifestResourceNames())
                    if (n.EndsWith("item_meta.json", StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                if (name != null)
                    using (var s = asm.GetManifestResourceStream(name))
                    using (var r = new System.IO.StreamReader(s))
                        _map = Json.Obj(Json.Parse(r.ReadToEnd())) ?? new Dictionary<string, object>();
                Plugin.Logger?.LogInfo($"[items] loaded {_map.Count} item-meta entries from embedded data");
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("ItemMetaStore: " + e.Message); }
            return _map;
        }

        private static object Entry(int itemKey)
            => itemKey <= 0 ? null : Json.Obj(Json.Get(Map(), itemKey.ToString()));

        /// <summary>EGradeType name (e.g. "IMMORTAL"); "" if unknown.</summary>
        public static string Grade(int itemKey) => Json.Str(Json.Get(Entry(itemKey), "g")) ?? "";

        /// <summary>Item (required) level; 0 if unknown.</summary>
        public static int Level(int itemKey) => (int)Json.Num(Json.Get(Entry(itemKey), "l"));

        /// <summary>Icon path relative to the wiki's /game/gear/ root (e.g. "bow/BOW_310017.png"); "" if unknown.</summary>
        public static string IconPath(int itemKey) => Json.Str(Json.Get(Entry(itemKey), "i")) ?? "";

        /// <summary>Gear slot/type (e.g. "BOW", "HELMET", "RING"); "" if unknown. Used to bucket the item's
        /// base value into the attack/defence split.</summary>
        public static string GearType(int itemKey) => Json.Str(Json.Get(Entry(itemKey), "t")) ?? "";
    }
}
