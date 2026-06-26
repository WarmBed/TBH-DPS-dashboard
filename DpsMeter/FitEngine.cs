using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>One stat line on a gear template or material: StatType + MODTYPE + value.</summary>
    public struct GearStat
    {
        public string Stat;   // StatType name, e.g. "AttackDamage" / "AttackSpeed" / "CriticalChance"
        public string Mod;    // MODTYPE: "FLAT" / "ADDITIVE" / "MULTIPLICATIVE"
        public double Value;
        public GearStat(string stat, string mod, double value) { Stat = stat; Mod = mod; Value = value; }
    }

    /// <summary>A gear item template (from the game's GearInfoData+GearTypeInfoData+ItemInfoData CSVs):
    /// its slot/type/grade plus the FULL resolved stat list (base + inherent).</summary>
    public sealed class GearTemplate
    {
        public int Key;
        public string Type = "";    // GEARTYPE: BOW/STAFF/RING/...
        public string Grade = "";
        public int Level;
        public string Slot = "";    // PARTS: MAIN_WEAPON/SUB_WEAPON/HELMET/...
        public string NameKey = "";
        public readonly List<GearStat> Stats = new List<GearStat>();
    }

    /// <summary>One tier of a decoration/socket material (StatModInfoData): the stat it grants at that tier.</summary>
    public struct MatTier
    {
        public int Tier;
        public string Stat;
        public string Mod;
        public double Min, Max;
        public double Mid => (Min + Max) / 2.0;
    }

    /// <summary>The fitting item/material database, parsed from the bundled fit_gear.json / fit_mats.json
    /// (extracted from the game's CSV TextAssets). Pure C# — the parse methods take strings so they're
    /// unit-testable; a Unity loader feeds the embedded resources in.</summary>
    public static class GearDatabase
    {
        private static readonly List<GearTemplate> _all = new List<GearTemplate>();
        private static readonly Dictionary<int, GearTemplate> _byKey = new Dictionary<int, GearTemplate>();
        private static readonly Dictionary<string, List<GearTemplate>> _bySlot = new Dictionary<string, List<GearTemplate>>();
        private static readonly Dictionary<int, List<MatTier>> _mats = new Dictionary<int, List<MatTier>>();
        private static readonly List<int> _matKeyList = new List<int>();
        public static bool Loaded { get; private set; }

        public static void Reset()
        {
            _all.Clear(); _byKey.Clear(); _bySlot.Clear(); _mats.Clear(); _matKeyList.Clear(); Loaded = false;
        }

        /// <summary>Parse fit_gear.json (array of {k,t,g,l,p,n,s:[[stat,mod,val]...]}).</summary>
        public static void LoadGear(string json)
        {
            var arr = Json.Arr(Json.Parse(json));
            if (arr == null) return;
            foreach (var it in arr)
            {
                var g = new GearTemplate
                {
                    Key = (int)Json.Num(Json.Get(it, "k")),
                    Type = Json.Str(Json.Get(it, "t")) ?? "",
                    Grade = Json.Str(Json.Get(it, "g")) ?? "",
                    Level = (int)Json.Num(Json.Get(it, "l")),
                    Slot = Json.Str(Json.Get(it, "p")) ?? "",
                    NameKey = Json.Str(Json.Get(it, "n")) ?? "",
                };
                var sl = Json.Arr(Json.Get(it, "s"));
                if (sl != null)
                    foreach (var st in sl)
                    {
                        var row = Json.Arr(st);
                        if (row == null || row.Count < 3) continue;
                        g.Stats.Add(new GearStat(Json.Str(row[0]) ?? "", Json.Str(row[1]) ?? "FLAT", Json.Num(row[2])));
                    }
                _all.Add(g);
                _byKey[g.Key] = g;
                if (!string.IsNullOrEmpty(g.Slot))
                {
                    if (!_bySlot.TryGetValue(g.Slot, out var l)) { l = new List<GearTemplate>(); _bySlot[g.Slot] = l; }
                    l.Add(g);
                }
            }
            Loaded = true;
        }

        /// <summary>Parse fit_mats.json ({ "StatModKey": [[tier,stat,mod,min,max]...] }).</summary>
        public static void LoadMats(string json)
        {
            var obj = Json.Obj(Json.Parse(json));
            if (obj == null) return;
            foreach (var kv in obj)
            {
                int key; if (!int.TryParse(kv.Key, out key)) continue;
                var tiers = new List<MatTier>();
                var arr = Json.Arr(kv.Value);
                if (arr != null)
                    foreach (var t in arr)
                    {
                        var row = Json.Arr(t);
                        if (row == null || row.Count < 5) continue;
                        tiers.Add(new MatTier
                        {
                            Tier = (int)Json.Num(row[0]),
                            Stat = Json.Str(row[1]) ?? "",
                            Mod = Json.Str(row[2]) ?? "FLAT",
                            Min = Json.Num(row[3]),
                            Max = Json.Num(row[4]),
                        });
                    }
                _mats[key] = tiers;
                _matKeyList.Add(key);
            }
        }

        public static int Count => _all.Count;
        public static GearTemplate ByKey(int key) { _byKey.TryGetValue(key, out var g); return g; }
        public static List<GearTemplate> BySlot(string slot) { _bySlot.TryGetValue(slot ?? "", out var l); return l ?? new List<GearTemplate>(); }
        public static List<MatTier> Material(int key) { _mats.TryGetValue(key, out var l); return l ?? new List<MatTier>(); }
        public static IReadOnlyList<int> MaterialKeys => _matKeyList;
        public static IReadOnlyList<GearTemplate> All => _all;
    }

    /// <summary>High-level fitting helpers: turn a loadout (the equipped item keys) into aggregated stats,
    /// CombatStats, and a formula DPS. Lives here (not in the injected panel) so the custom structs stay
    /// out of any IL2CPP-scanned signature.</summary>
    public static class FitCalc
    {
        /// <summary>Aggregate the stats of all items in a loadout (their templates' resolved stat lists).</summary>
        public static Dictionary<string, double> LoadoutStats(IEnumerable<int> itemKeys)
        {
            var lines = new List<GearStat>();
            if (itemKeys != null)
                foreach (var k in itemKeys)
                {
                    var g = GearDatabase.ByKey(k);
                    if (g != null) lines.AddRange(g.Stats);
                }
            return StatAggregator.Aggregate(lines);
        }

        public static CombatStats ToCombat(Dictionary<string, double> agg)
        {
            var c = new CombatStats();
            if (agg != null)
            {
                agg.TryGetValue("AttackDamage", out c.AttackDamage);
                agg.TryGetValue("AttackSpeed", out c.AttackSpeed);
                agg.TryGetValue("CriticalChance", out c.CritChance);
                agg.TryGetValue("CriticalDamage", out c.CritDamage);
            }
            return c;
        }

        /// <summary>Formula DPS for a loadout (placeholder formula until native decomp; used as a RATIO,
        /// anchored to measured DPS by the caller).</summary>
        public static double LoadoutDps(IEnumerable<int> itemKeys)
        {
            return DamageFormula.ExpectedDps(ToCombat(LoadoutStats(itemKeys)));
        }

        /// <summary>The stat lines granted by a set of applied materials, each given as [StatModKey, Tier].</summary>
        public static List<GearStat> MaterialStats(IEnumerable<int[]> mats)
        {
            var lines = new List<GearStat>();
            if (mats != null)
                foreach (var mt in mats)
                {
                    if (mt == null || mt.Length < 2) continue;
                    foreach (var t in GearDatabase.Material(mt[0]))
                        if (t.Tier == mt[1]) { lines.Add(new GearStat(t.Stat, t.Mod, t.Mid)); break; }
                }
            return lines;
        }

        /// <summary>Aggregate gear loadout + applied materials together.</summary>
        public static Dictionary<string, double> LoadoutStats(IEnumerable<int> itemKeys, IEnumerable<int[]> mats)
        {
            var lines = new List<GearStat>();
            if (itemKeys != null)
                foreach (var k in itemKeys) { var g = GearDatabase.ByKey(k); if (g != null) lines.AddRange(g.Stats); }
            lines.AddRange(MaterialStats(mats));
            return StatAggregator.Aggregate(lines);
        }

        public static double LoadoutDps(IEnumerable<int> itemKeys, IEnumerable<int[]> mats)
        {
            return DamageFormula.ExpectedDps(ToCombat(LoadoutStats(itemKeys, mats)));
        }
    }

    /// <summary>Aggregates a set of gear/material stat lines into a per-StatType total, PoE-style:
    /// final = ΣFLAT × (1 + ΣADDITIVE/100) × Π(1 + MULTIPLICATIVE/100). The exact percent divisor is the
    /// game's (assumed 100 until the native damage-formula decomp confirms it); callers ANCHOR to the
    /// hero's measured stats (scale = measured / aggregated-current) so absolute values track reality and
    /// only the loadout RATIO drives predictions. Pure C#.</summary>
    public static class StatAggregator
    {
        public static Dictionary<string, double> Aggregate(IEnumerable<GearStat> stats)
        {
            var flat = new Dictionary<string, double>();
            var add = new Dictionary<string, double>();
            var mul = new Dictionary<string, double>();   // accumulated product factor
            if (stats != null)
                foreach (var s in stats)
                {
                    if (string.IsNullOrEmpty(s.Stat)) continue;
                    switch (s.Mod)
                    {
                        case "ADDITIVE": add[s.Stat] = (add.TryGetValue(s.Stat, out var a) ? a : 0) + s.Value; break;
                        case "MULTIPLICATIVE":
                            mul[s.Stat] = (mul.TryGetValue(s.Stat, out var m) ? m : 1.0) * (1.0 + s.Value / 100.0); break;
                        default: flat[s.Stat] = (flat.TryGetValue(s.Stat, out var f) ? f : 0) + s.Value; break;
                    }
                }
            var outp = new Dictionary<string, double>();
            var keys = new HashSet<string>(flat.Keys);
            foreach (var k in add.Keys) keys.Add(k);
            foreach (var k in mul.Keys) keys.Add(k);
            foreach (var k in keys)
            {
                double f = flat.TryGetValue(k, out var ff) ? ff : 0;
                double a = add.TryGetValue(k, out var aa) ? aa : 0;
                double m = mul.TryGetValue(k, out var mm) ? mm : 1.0;
                outp[k] = f * (1.0 + a / 100.0) * m;
            }
            return outp;
        }
    }
}
