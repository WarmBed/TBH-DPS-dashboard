using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace TbhDpsMeter
{
    /// <summary>Persists saved fittings (a hero's sandbox loadout: gear by slot + socket assignments) as a
    /// single JSON file under BepInEx/config. Non-injected — keeps int[]/Dictionary out of the overlay type.</summary>
    public static class FitStore
    {
        private static string FilePath => Path.Combine(BepInEx.Paths.ConfigPath, "dpsmeter_fits.json");

        public sealed class Fit
        {
            public string Name = "";
            public int Hero;
            public int[] Gear = new int[10];
            public readonly Dictionary<int, int[]> Sockets = new Dictionary<int, int[]>();
        }

        public static List<Fit> LoadAll()
        {
            var list = new List<Fit>();
            try
            {
                if (!File.Exists(FilePath)) return list;
                var arr = Json.Arr(Json.Parse(File.ReadAllText(FilePath)));
                if (arr == null) return list;
                foreach (var it in arr)
                {
                    var f = new Fit { Name = Json.Str(Json.Get(it, "name")) ?? "", Hero = (int)Json.Num(Json.Get(it, "hero")) };
                    var g = Json.Arr(Json.Get(it, "gear"));
                    if (g != null) for (int i = 0; i < g.Count && i < f.Gear.Length; i++) f.Gear[i] = (int)Json.Num(g[i]);
                    var so = Json.Obj(Json.Get(it, "sockets"));
                    if (so != null)
                        foreach (var kv in so)
                        {
                            if (!int.TryParse(kv.Key, out var slot)) continue;
                            var sa = Json.Arr(kv.Value);
                            if (sa == null) continue;
                            var a = new int[sa.Count];
                            for (int j = 0; j < sa.Count; j++) a[j] = (int)Json.Num(sa[j]);
                            f.Sockets[slot] = a;
                        }
                    list.Add(f);
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("FitStore load: " + e.Message); }
            return list;
        }

        public static void SaveAll(List<Fit> list)
        {
            try
            {
                var sb = new StringBuilder("[");
                for (int i = 0; i < list.Count; i++)
                {
                    var f = list[i];
                    if (i > 0) sb.Append(',');
                    sb.Append("{\"name\":\"").Append(Esc(f.Name)).Append("\",\"hero\":").Append(f.Hero).Append(",\"gear\":[");
                    for (int j = 0; j < f.Gear.Length; j++) { if (j > 0) sb.Append(','); sb.Append(f.Gear[j]); }
                    sb.Append("],\"sockets\":{");
                    bool first = true;
                    foreach (var kv in f.Sockets)
                    {
                        if (!first) sb.Append(','); first = false;
                        sb.Append('"').Append(kv.Key).Append("\":[");
                        for (int j = 0; j < kv.Value.Length; j++) { if (j > 0) sb.Append(','); sb.Append(kv.Value[j]); }
                        sb.Append(']');
                    }
                    sb.Append("}}");
                }
                sb.Append(']');
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                File.WriteAllText(FilePath, sb.ToString());
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("FitStore save: " + e.Message); }
        }

        private static string Esc(string s) => (s ?? "").Replace("\\", "\\\\").Replace("\"", "\\\"");
        public static void Add(Fit f) { var l = LoadAll(); l.Add(f); SaveAll(l); }
        public static void RemoveAt(int idx) { var l = LoadAll(); if (idx >= 0 && idx < l.Count) { l.RemoveAt(idx); SaveAll(l); } }
    }
}
