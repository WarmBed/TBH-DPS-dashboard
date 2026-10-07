using System;
using System.Collections.Generic;
using System.Reflection;

namespace TbhDpsMeter
{
    /// <summary>The game's OWN gear stat table, read live — the authoritative replacement for the bundled
    /// wiki extract, which goes stale on every content patch (the Lv90 "193" tier shipped under different
    /// ItemKeys than the wiki's pre-release datamine guessed, so fit_gear.json has no row for any of it).
    ///
    /// Chain: ItemKey -> ItemInfoData (carries GearKey) -> GearInfoData (BaseStat1/2_Value plus three
    /// inherent {StatType, MODTYPE, Value} lines) — i.e. exactly the lines a tooltip shows. The holder is a
    /// singleton MonoBehaviour whose own type name is obfuscated, so it's located by SHAPE: the type with an
    /// instance (Int32) -> GearInfoData method. The data types themselves (GearInfoData / ItemInfoData /
    /// StatType / MODTYPE) have readable names and are stable across updates.</summary>
    internal static class GameGearDb
    {
        public struct Line
        {
            public string Stat;   // StatType name, e.g. "Armor"
            public string Mod;    // "FLAT" / "ADDITIVE" / "MULTIPLICATIVE"
            public double Value;  // raw table value (percent stats are stored x10)
            public Line(string s, string m, double v) { Stat = s; Mod = m; Value = v; }
        }

        private static bool _resolved;
        private static readonly Dictionary<int, int> _itemToGear = new Dictionary<int, int>();   // ItemKey -> GearKey
        private static readonly Dictionary<int, object> _itemRows = new Dictionary<int, object>(); // ItemKey -> ItemInfoData
        private static readonly Dictionary<int, object> _gearRows = new Dictionary<int, object>(); // GearKey -> GearInfoData

        /// <summary>Snapshot the game's two master tables once: Dictionary&lt;int, ItemInfoData&gt; (keyed by
        /// ItemKey, carries the GearKey) and Dictionary&lt;int, GearInfoData&gt; (keyed by GearKey, carries the
        /// stat lines). Found by SHAPE on the readable data type names, so the holder's own obfuscated name
        /// doesn't matter. Read straight off the dictionaries rather than the several same-shaped
        /// (int)->InfoData lookup methods, which key on different ids and can't be told apart by signature.</summary>
        private static void Resolve()
        {
            if (_resolved) return;
            var gearT = Refl.FindType("TaskbarHero.Data.GearInfoData");
            var itemT = Refl.FindType("TaskbarHero.Data.ItemInfoData");
            if (gearT == null || itemT == null) { _resolved = true; Plugin.Logger?.LogWarning("[geardb] info types not found"); return; }
            try
            {
                Type holder = null; PropertyInfo gearProp = null, itemProp = null, itemListProp = null;
                foreach (var t in gearT.Assembly.GetTypes())
                {
                    PropertyInfo gp = null, ip = null;
                    try
                    {
                        foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                        {
                            if (!IsIntDict(pr.PropertyType, gearT) && !IsIntDict(pr.PropertyType, itemT)) continue;
                            if (IsIntDict(pr.PropertyType, gearT)) { if (gp == null) gp = pr; }
                            else if (ip == null) ip = pr;
                        }
                    }
                    catch { continue; }
                    if (gp == null || ip == null) continue;
                    // the keyed item dictionary is only partially populated (229 rows vs the full table);
                    // prefer the complete List<ItemInfoData> when the holder exposes one.
                    PropertyInfo lp = null;
                    try
                    {
                        foreach (var pr in t.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly))
                            if (pr.PropertyType.IsGenericType && pr.PropertyType.Name.StartsWith("List")
                                && pr.PropertyType.GetGenericArguments().Length == 1
                                && pr.PropertyType.GetGenericArguments()[0] == itemT) { lp = pr; break; }
                    }
                    catch { }
                    holder = t; gearProp = gp; itemProp = ip; itemListProp = lp; break;
                }
                if (holder == null) { _resolved = true; Plugin.Logger?.LogWarning("[geardb] no holder with both keyed tables"); return; }

                object found = UnityEngine.Object.FindObjectOfType(Il2CppInterop.Runtime.Il2CppType.From(holder));
                if (found == null) { Plugin.Logger?.LogInfo("[geardb] holder " + holder.Name + " not in scene yet"); return; }   // retry later
                // FindObjectOfType hands back a UnityEngine.Object-typed wrapper; reading a property declared
                // on the concrete interop class off it throws "Object does not match target type". Re-wrap the
                // same il2cpp pointer as the concrete type (every interop class has an IntPtr ctor).
                object inst = found;
                try
                {
                    var ptr = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)found).Pointer;
                    inst = Activator.CreateInstance(holder, ptr);
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("[geardb] re-wrap failed: " + e.Message); }

                int items = 0, gears = 0;
                if (itemListProp != null)
                    foreach (var row in BigList(itemListProp.GetValue(inst)))
                    {
                        object ik = Refl.Get(row, "ItemKey"), gk = Refl.Get(row, "GearKey");
                        if (ik == null) continue;
                        int i = Convert.ToInt32(ik), g = gk == null ? 0 : Convert.ToInt32(gk);
                        if (i <= 0) continue;
                        _itemRows[i] = row;
                        if (g > 0) _itemToGear[i] = g;
                        items++;
                    }
                if (items == 0)
                    foreach (var kv in BigDict(itemProp.GetValue(inst)))
                    {
                        object row = Refl.Get(kv, "Value"); if (row == null) continue;
                        object ik = Refl.Get(row, "ItemKey"), gk = Refl.Get(row, "GearKey");
                        if (ik == null) continue;
                        int i = Convert.ToInt32(ik), g = gk == null ? 0 : Convert.ToInt32(gk);
                        if (i > 0 && g > 0) { _itemToGear[i] = g; items++; }
                    }
                foreach (var kv in BigDict(gearProp.GetValue(inst)))
                {
                    object key = Refl.Get(kv, "Key"), row = Refl.Get(kv, "Value");
                    if (key == null || row == null) continue;
                    _gearRows[Convert.ToInt32(key)] = row;
                    gears++;
                }
                _resolved = items > 0 && gears > 0;   // leave unresolved (retry) if the tables weren't loaded yet
                Plugin.Logger?.LogInfo($"[geardb] holder={holder.Name} items={items} gearRows={gears} resolved={_resolved}");
            }
            catch (Exception e) { _resolved = true; Plugin.Logger?.LogWarning("GameGearDb.Resolve: " + e.Message); }
        }


        // The shared Refl enumerators cap at 512 entries (a safety valve for small live collections) — these
        // master tables run to thousands of rows, so a capped read silently truncates the catalogue. Local
        // uncapped enumerators, used only for the one-time table snapshot.
        private static IEnumerable<object> BigList(object list)
        {
            if (list == null) yield break;
            int count;
            try { count = Convert.ToInt32(Refl.Get(list, "Count") ?? Refl.Get(list, "Length") ?? 0); }
            catch { yield break; }
            for (int i = 0; i < count; i++)
            {
                object item = null;
                try { item = Refl.Call(list, "get_Item", i); } catch { }
                if (item != null) yield return item;
            }
        }

        private static IEnumerable<object> BigDict(object dict)
        {
            if (dict == null) yield break;
            // two paths, same as Refl.EnumerateE: the il2cpp IEnumerable cast works for some wrappers, and a
            // reflected GetEnumerator covers the rest (the keyed master tables only answer to the latter).
            object en = null;
            var ie = dict as Il2CppSystem.Collections.IEnumerable;
            if (ie != null) en = ie.GetEnumerator();
            if (en == null) en = Refl.Call(dict, "GetEnumerator");
            if (en == null) yield break;
            int guard = 0;
            while (guard++ < 200000)
            {
                object mv = Refl.Call(en, "MoveNext");
                bool moved;
                try { moved = Convert.ToBoolean(mv); } catch { yield break; }
                if (!moved) yield break;
                object cur = Refl.Get(en, "Current") ?? Refl.Call(en, "get_Current");
                if (cur != null) yield return cur;
            }
        }

        private static bool IsIntDict(Type t, Type valueType)
            => t != null && t.IsGenericType && t.Name.StartsWith("Dictionary")
               && t.GetGenericArguments().Length == 2
               && t.GetGenericArguments()[0] == typeof(int)
               && t.GetGenericArguments()[1] == valueType;

        /// <summary>The game's own stat lines for an ItemKey; null when the table isn't reachable or the
        /// item has no gear row (materials, boxes).</summary>
        public static List<Line> Stats(int itemKey)
        {
            Resolve();
            if (itemKey <= 0 || _gearRows.Count == 0) return null;
            int gearKey;
            if (!_itemToGear.TryGetValue(itemKey, out gearKey)) return null;
            object gi;
            if (!_gearRows.TryGetValue(gearKey, out gi)) return null;
            try
            {
                var outp = new List<Line>();
                AddNum(outp, gi, "BaseStat1_Value");
                AddNum(outp, gi, "BaseStat2_Value");
                for (int n = 1; n <= 3; n++)
                {
                    string st = Refl.Get(gi, "inherentStat" + n + "StatType")?.ToString();
                    string md = Refl.Get(gi, "InherentStat" + n + "_MODTYPE")?.ToString();
                    object vo = Refl.Get(gi, "InherentStat" + n + "_Value");
                    if (string.IsNullOrEmpty(st) || st == "NONE" || vo == null) continue;
                    double v = Convert.ToDouble(vo);
                    if (v == 0) continue;
                    outp.Add(new Line(st, string.IsNullOrEmpty(md) ? "FLAT" : md, v));
                }
                return outp;
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("GameGearDb.Stats: " + e.Message); return null; }
        }

        private static void AddNum(List<Line> outp, object gi, string prop)
        {
            object v = Refl.Get(gi, prop);
            if (v == null) return;
            double d;
            try { d = Convert.ToDouble(v); } catch { return; }
            if (d != 0) outp.Add(new Line(prop, "FLAT", d));
        }

        private static bool _dumpedAll;

        /// <summary>Write the game's ENTIRE item + gear catalogue to disk as JSON: every ItemKey with its
        /// grade / level / type / icon / current-language name, plus the gear stat lines for equipment.
        /// This is the authoritative replacement for the bundled wiki extract, which is frozen at whatever
        /// the wiki had scraped and has no rows at all for tiers that shipped later. Runs once per session;
        /// the file is meant to be fed back into the bundled JSONs, not read at runtime.</summary>
        public static void DumpAll()
        {
            if (_dumpedAll) return;
            Resolve();
            if (_itemRows.Count == 0 || _gearRows.Count == 0) return;   // tables not ready yet -> retry later
            _dumpedAll = true;
            try
            {
                string dir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "tbh_dump");
                System.IO.Directory.CreateDirectory(dir);
                string path = System.IO.Path.Combine(dir, "game_items.json");
                var sb = new System.Text.StringBuilder(1 << 20);
                sb.Append("{\n  \"lang\": \"").Append(Loc.WikiLangCode()).Append("\",\n  \"items\": {\n");
                bool first = true;
                foreach (var kv in _itemRows)
                {
                    int key = kv.Key;
                    object row = kv.Value;
                    if (!first) sb.Append(",\n");
                    first = false;
                    sb.Append("    \"").Append(key).Append("\": {");
                    Field(sb, "gearKey", Num(row, "GearKey"), true);
                    Str(sb, "type", Enum(row, "ITEMTYPE"));
                    Str(sb, "grade", Enum(row, "GRADE"));
                    Str(sb, "parts", Enum(row, "PARTS"));
                    Str(sb, "gear", Enum(row, "GEARTYPE"));
                    Str(sb, "group", Enum(row, "GearGroup"));
                    Field(sb, "level", Num(row, "Level"), false);
                    Str(sb, "icon", Refl.Get(row, "IconPath") as string);
                    string nameKey = Refl.Get(row, "NameKey") as string;
                    Str(sb, "nameKey", nameKey);
                    Str(sb, "name", LiveName(nameKey, key));
                    object gi;
                    int gk = Num(row, "GearKey");
                    if (gk > 0 && _gearRows.TryGetValue(gk, out gi))
                    {
                        sb.Append(", \"stats\": [");
                        bool f2 = true;
                        foreach (var l in GearLines(gi))
                        {
                            if (!f2) sb.Append(", ");
                            f2 = false;
                            sb.Append("[\"").Append(l.Stat).Append("\",\"").Append(l.Mod).Append("\",").Append(l.Value.ToString("0.###")).Append(']');
                        }
                        sb.Append(']');
                    }
                    sb.Append('}');
                }
                sb.Append("\n  }\n}\n");
                System.IO.File.WriteAllText(path, sb.ToString(), new System.Text.UTF8Encoding(false));
                Plugin.Logger?.LogInfo($"[geardb] dumped {_itemRows.Count} items ({_gearRows.Count} gear rows) -> {path}");
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("GameGearDb.DumpAll: " + e.Message); }
        }

        private static string LiveName(string nameKey, int itemKey)
        {
            string n = null;
            if (!string.IsNullOrEmpty(nameKey)) n = HeroProbe.GameLocItem(nameKey);
            if (string.IsNullOrEmpty(n)) n = HeroProbe.GameLocItem(itemKey.ToString());
            return n;
        }

        private static int Num(object row, string prop)
        {
            object v = Refl.Get(row, prop);
            try { return v == null ? 0 : Convert.ToInt32(v); } catch { return 0; }
        }

        private static string Enum(object row, string prop) => Refl.Get(row, prop)?.ToString();

        private static void Field(System.Text.StringBuilder sb, string name, int value, bool firstField)
        {
            if (!firstField) sb.Append(", ");
            sb.Append('"').Append(name).Append("\": ").Append(value);
        }

        private static void Str(System.Text.StringBuilder sb, string name, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            sb.Append(", \"").Append(name).Append("\": \"").Append(Esc(value)).Append('"');
        }

        private static string Esc(string s)
            => s.Replace("\\", "\\\\").Replace("\"", "\\\"").Replace("\n", " ").Replace("\r", " ").Replace("\t", " ");

        /// <summary>The stat lines on a GearInfoData row.</summary>
        private static List<Line> GearLines(object gi)
        {
            var outp = new List<Line>();
            AddNum(outp, gi, "BaseStat1_Value");
            AddNum(outp, gi, "BaseStat2_Value");
            for (int n = 1; n <= 3; n++)
            {
                string st = Refl.Get(gi, "inherentStat" + n + "StatType")?.ToString();
                string md = Refl.Get(gi, "InherentStat" + n + "_MODTYPE")?.ToString();
                object vo = Refl.Get(gi, "InherentStat" + n + "_Value");
                if (string.IsNullOrEmpty(st) || st == "NONE" || vo == null) continue;
                double v;
                try { v = Convert.ToDouble(vo); } catch { continue; }
                if (v == 0) continue;
                outp.Add(new Line(st, string.IsNullOrEmpty(md) ? "FLAT" : md, v));
            }
            return outp;
        }

        private static bool _dumped;

        /// <summary>One-shot: read a few items whose real numbers we already know from two independent
        /// sources (the bundled wiki extract, and the live modifier list of an equipped item) and log them
        /// side by side. Tells us whether the table's values are final or still need the level/type scale
        /// tables applied, before anything is built on top of it.</summary>
        public static void DumpKnown(int[] itemKeys)
        {
            if (_dumped) return;
            Resolve();
            if (_gearRows.Count == 0) return;   // tables not loaded yet -> try again on the next snapshot
            _dumped = true;
            foreach (int k in itemKeys)
            {
                int gk; _itemToGear.TryGetValue(k, out gk);
                var lines = Stats(k);
                if (lines == null) { Plugin.Logger?.LogInfo($"[geardb] item {k}: gearKey={gk} (no row)"); continue; }
                var sb = new System.Text.StringBuilder();
                foreach (var l in lines) sb.Append(l.Stat).Append(' ').Append(l.Mod).Append(' ').Append(l.Value.ToString("0.###")).Append(" | ");
                Plugin.Logger?.LogInfo($"[geardb] item {k}: {sb}");
            }
        }
    }
}
