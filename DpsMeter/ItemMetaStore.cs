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

        // Fallback for ItemKeys the bundled wiki table has never seen (a new level tier the wiki hasn't
        // catalogued yet — e.g. the game ships a "193" tier suffix where the wiki guessed "191"/"192"
        // before the tier shipped — the wiki's own pre-release datamine already has the RIGHT content
        // under the WRONG id). Verified invariant over the whole bundled table: every gear "family"
        // (ItemKey / 1000 — same slot + grade, every level tier of it) shares one grade and one gear type
        // across ALL its known tiers (0 exceptions across 196 families / 5760 gear entries; confirmed e.g.
        // family 533 = LEGENDARY BOOTS runs level 1..90 in 18 known steps, all sharing grade+type). So an
        // unknown ItemKey's grade/type/icon/name can all be borrowed from a family sibling — grade/type from
        // ANY sibling (identical across the family), name/icon specifically from the HIGHEST-level sibling
        // (the "wiki's guess for a not-yet-shipped tier" is always the frontier one — level differs per
        // tier, so it's the best content match for an unknown tier beyond it).
        private static Dictionary<int, KeyValuePair<int, object>> _familyBest; // family -> (bestKey, bestEntry)

        private static Dictionary<int, KeyValuePair<int, object>> FamilyReps()
        {
            if (_familyBest != null) return _familyBest;
            _familyBest = new Dictionary<int, KeyValuePair<int, object>>();
            foreach (var kv in Map())
            {
                if (!int.TryParse(kv.Key, out int key)) continue;
                var e = Json.Obj(kv.Value);
                if (e == null || !string.IsNullOrEmpty(Json.Str(Json.Get(e, "c")))) continue; // gear only (no "c")
                int fam = key / 1000;
                int lvl = (int)Json.Num(Json.Get(e, "l"));
                if (!_familyBest.TryGetValue(fam, out var cur) || lvl >= (int)Json.Num(Json.Get(cur.Value, "l")))
                    _familyBest[fam] = new KeyValuePair<int, object>(key, e);
            }
            return _familyBest;
        }

        private static object FamilyEntry(int itemKey)
        {
            if (itemKey <= 0) return null;
            FamilyReps().TryGetValue(itemKey / 1000, out var kv);
            return kv.Value;
        }

        /// <summary>The bundled table's highest-level known sibling for itemKey's family (same ItemKey/1000
        /// slot+grade group) — used by <see cref="ItemNameStore"/> to borrow a name for an unknown tier.
        /// 0 if itemKey isn't gear or its whole family is unknown.</summary>
        public static int FamilyBestItemKey(int itemKey)
        {
            if (itemKey <= 0) return 0;
            FamilyReps().TryGetValue(itemKey / 1000, out var kv);
            return kv.Value != null ? kv.Key : 0;
        }

        /// <summary>EGradeType name (e.g. "IMMORTAL"); falls back to the item's family (see
        /// <see cref="FamilyEntry"/>) if this exact ItemKey is unknown; "" if the whole family is unknown.</summary>
        public static string Grade(int itemKey)
        {
            string g = Json.Str(Json.Get(Entry(itemKey), "g"));
            if (!string.IsNullOrEmpty(g)) return g;
            return Json.Str(Json.Get(FamilyEntry(itemKey), "g")) ?? "";
        }

        /// <summary>Item (required) level; 0 if unknown.</summary>
        /// <summary>Item (required) level; falls back to the item's family (see <see cref="FamilyEntry"/>,
        /// the highest-level known sibling — the same real-world tier under a different guessed ItemKey)
        /// if this exact ItemKey is unknown; 0 if the whole family is unknown.</summary>
        public static int Level(int itemKey)
        {
            int l = (int)Json.Num(Json.Get(Entry(itemKey), "l"));
            if (l > 0) return l;
            return (int)Json.Num(Json.Get(FamilyEntry(itemKey), "l"));
        }

        /// <summary>Icon path relative to the wiki's /game/gear/ root (e.g. "bow/BOW_310017.png"); falls back
        /// to a family sibling's (real, fetchable) path when this exact ItemKey is unknown — the wrong tier's
        /// art is a closer placeholder than no icon at all; "" if the whole family is unknown.</summary>
        public static string IconPath(int itemKey)
        {
            string i = Json.Str(Json.Get(Entry(itemKey), "i"));
            if (!string.IsNullOrEmpty(i)) return i;
            return Json.Str(Json.Get(FamilyEntry(itemKey), "i")) ?? "";
        }

        /// <summary>Gear slot/type (e.g. "BOW", "HELMET", "RING"); falls back to the item's family (see
        /// <see cref="FamilyEntry"/>) if this exact ItemKey is unknown; "" if the whole family is unknown.</summary>
        public static string GearType(int itemKey)
        {
            string t = Json.Str(Json.Get(Entry(itemKey), "t"));
            if (!string.IsNullOrEmpty(t)) return t;
            return Json.Str(Json.Get(FamilyEntry(itemKey), "t")) ?? "";
        }

        /// <summary>Broad category: "GEAR" for equipment, else the wiki `type` (e.g. "MATERIAL", "STAGEBOX").
        /// Non-gear entries carry an explicit "c" field; gear entries don't, so a known entry without "c" is
        /// gear. An ItemKey unknown to the table but belonging to a known GEAR family (see
        /// <see cref="FamilyEntry"/>) is also "GEAR" (a new level tier the wiki hasn't catalogued is still
        /// gear, not material). Empty string only if the item key AND its whole family are unknown.</summary>
        public static string Category(int itemKey)
        {
            var e = Entry(itemKey);
            if (e != null)
            {
                string c = Json.Str(Json.Get(e, "c"));
                return !string.IsNullOrEmpty(c) ? c : "GEAR";
            }
            return FamilyEntry(itemKey) != null ? "GEAR" : "";
        }
    }
}
