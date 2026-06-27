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
        public string GearGroup = ""; // WEAPON / ARMOR / ACCESSORY — picks which socket-material effect applies
        public int Level;
        public string Slot = "";    // PARTS: MAIN_WEAPON/SUB_WEAPON/HELMET/...
        public string NameKey = "";
        public readonly List<GearStat> Stats = new List<GearStat>();
    }

    /// <summary>A socketable material (decoration/engraving/inscription). Its effect depends on the gear group
    /// (weapon/armor/accessory) it is socketed into, pre-resolved from StatModGroup → StatMod tier.</summary>
    public sealed class SockMat
    {
        public int Key;
        public char Type;        // 'D' decoration / 'E' engraving / 'I' inscription
        public string NameKey = "";
        public GearStat W, A, C; // effect on weapon / armor / accessory (default Stat=="" if none)
        public int WTier, ATier, CTier;
        public bool HasW, HasA, HasC;
        public bool HasFor(string gearGroup) => gearGroup == "WEAPON" ? HasW : (gearGroup == "ARMOR" ? HasA : HasC);
        public GearStat Effect(string gearGroup) => gearGroup == "WEAPON" ? W : (gearGroup == "ARMOR" ? A : C);
        public int TierFor(string gearGroup) => gearGroup == "WEAPON" ? WTier : (gearGroup == "ARMOR" ? ATier : CTier);
    }

    /// <summary>The fitting item/material database, parsed from the bundled fit_gear.json / fit_mats.json
    /// (extracted from the game's CSV TextAssets). Pure C# — the parse methods take strings so they're
    /// unit-testable; a Unity loader feeds the embedded resources in.</summary>
    public static class GearDatabase
    {
        private static readonly List<GearTemplate> _all = new List<GearTemplate>();
        private static readonly Dictionary<int, GearTemplate> _byKey = new Dictionary<int, GearTemplate>();
        private static readonly Dictionary<string, List<GearTemplate>> _bySlot = new Dictionary<string, List<GearTemplate>>();
        public static bool Loaded { get; private set; }

        public static void Reset()
        {
            _all.Clear(); _byKey.Clear(); _bySlot.Clear(); Loaded = false;
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
                    GearGroup = Json.Str(Json.Get(it, "gg")) ?? "",
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

        public static int Count => _all.Count;
        public static GearTemplate ByKey(int key) { _byKey.TryGetValue(key, out var g); return g; }
        public static List<GearTemplate> BySlot(string slot) { _bySlot.TryGetValue(slot ?? "", out var l); return l ?? new List<GearTemplate>(); }
        public static IReadOnlyList<GearTemplate> All => _all;
    }

    /// <summary>Per-grade socket counts (GradeInfoData): how many decoration / engraving / inscription sockets a
    /// gear item of each grade has. Parsed from fit_sockets.json ({ "GRADE": [deco, engrave, inscribe] }).</summary>
    public static class SocketDb
    {
        private static readonly Dictionary<string, int[]> _g = new Dictionary<string, int[]>();
        public static void Load(string json)
        {
            _g.Clear();
            var obj = Json.Obj(Json.Parse(json));
            if (obj == null) return;
            foreach (var kv in obj)
            {
                var a = Json.Arr(kv.Value);
                _g[kv.Key] = new int[] { a != null && a.Count > 0 ? (int)Json.Num(a[0]) : 0,
                                         a != null && a.Count > 1 ? (int)Json.Num(a[1]) : 0,
                                         a != null && a.Count > 2 ? (int)Json.Num(a[2]) : 0 };
            }
        }
        /// <summary>[deco, engrave, inscribe] socket counts for a grade (all-zero if unknown).</summary>
        public static int[] Counts(string grade) { _g.TryGetValue(grade ?? "", out var c); return c ?? new int[3]; }
    }

    /// <summary>The socket-material catalog (fit_mats.json): each material item with its type and pre-resolved
    /// per-gear-group effect. { "matKey": { t, n, w:[stat,mod,val,tier], a:[...], c:[...] } }.</summary>
    public static class MatCatalog
    {
        private static readonly Dictionary<int, SockMat> _byKey = new Dictionary<int, SockMat>();
        private static readonly Dictionary<char, List<SockMat>> _byType = new Dictionary<char, List<SockMat>>();

        public static void Load(string json)
        {
            _byKey.Clear(); _byType.Clear();
            var obj = Json.Obj(Json.Parse(json));
            if (obj == null) return;
            foreach (var kv in obj)
            {
                int key; if (!int.TryParse(kv.Key, out key)) continue;
                var o = kv.Value;
                var m = new SockMat
                {
                    Key = key,
                    Type = (Json.Str(Json.Get(o, "t")) ?? "D")[0],
                    NameKey = Json.Str(Json.Get(o, "n")) ?? "",
                };
                ReadEff(Json.Get(o, "w"), ref m.W, ref m.WTier, ref m.HasW);
                ReadEff(Json.Get(o, "a"), ref m.A, ref m.ATier, ref m.HasA);
                ReadEff(Json.Get(o, "c"), ref m.C, ref m.CTier, ref m.HasC);
                _byKey[key] = m;
                if (!_byType.TryGetValue(m.Type, out var l)) { l = new List<SockMat>(); _byType[m.Type] = l; }
                l.Add(m);
            }
        }
        private static void ReadEff(object node, ref GearStat g, ref int tier, ref bool has)
        {
            var a = Json.Arr(node);
            if (a == null || a.Count < 3) { has = false; return; }
            g = new GearStat(Json.Str(a[0]) ?? "", Json.Str(a[1]) ?? "FLAT", Json.Num(a[2]));
            tier = a.Count > 3 ? (int)Json.Num(a[3]) : 0;
            has = true;
        }
        public static SockMat Get(int key) { _byKey.TryGetValue(key, out var m); return m; }
        public static List<SockMat> ByType(char t) { _byType.TryGetValue(t, out var l); return l ?? new List<SockMat>(); }
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

        /// <summary>Gear stats + socketed-material effects for a loadout. gearBySlot[s] = item key; sockets[s] =
        /// the material keys plugged into slot s (resolved against that item's gear group).</summary>
        public static List<GearStat> CollectLines(int[] gearBySlot, Dictionary<int, int[]> sockets)
        {
            var lines = new List<GearStat>();
            if (gearBySlot == null) return lines;
            for (int s = 0; s < gearBySlot.Length; s++)
            {
                var g = GearDatabase.ByKey(gearBySlot[s]);
                if (g == null) continue;
                lines.AddRange(g.Stats);
                if (sockets != null && sockets.TryGetValue(s, out var socks) && socks != null)
                    foreach (var mk in socks)
                    {
                        if (mk == 0) continue;
                        var m = MatCatalog.Get(mk);
                        if (m != null && m.HasFor(g.GearGroup)) lines.Add(m.Effect(g.GearGroup));
                    }
            }
            return lines;
        }

        public static Dictionary<string, double> LoadoutStats(int[] gearBySlot, Dictionary<int, int[]> sockets)
            => StatAggregator.Aggregate(CollectLines(gearBySlot, sockets));

        public static double LoadoutDps(int[] gearBySlot, Dictionary<int, int[]> sockets)
            => DamageFormula.ExpectedDps(ToCombat(LoadoutStats(gearBySlot, sockets)));

        /// <summary>The effective effect of one socket cell: an edited material wins (sentinel -1 = not edited,
        /// 0 = edited-empty); otherwise the item's REAL applied effect (when allowReal). Empty -> Stat=="".</summary>
        public static GearStat EffectiveCell(GearStat[] real, int[] edited, int pos, string gg, bool allowReal)
        {
            if (edited != null && pos >= 0 && pos < edited.Length && edited[pos] != -1)
            {
                int mk = edited[pos];
                if (mk == 0) return default;
                var m = MatCatalog.Get(mk);
                return (m != null && m.HasFor(gg)) ? m.Effect(gg) : default;
            }
            return (allowReal && real != null && pos >= 0 && pos < real.Length) ? real[pos] : default;
        }

        // aggregate gear + ready-made per-slot socket effect lines (caller resolves real-vs-edited)
        public static Dictionary<string, double> LoadoutStatsWith(int[] gear, Dictionary<int, List<GearStat>> socketLines)
        {
            var lines = new List<GearStat>();
            if (gear != null)
                for (int s = 0; s < gear.Length; s++)
                {
                    var g = GearDatabase.ByKey(gear[s]);
                    if (g != null) lines.AddRange(g.Stats);
                    if (socketLines != null && socketLines.TryGetValue(s, out var sl) && sl != null) lines.AddRange(sl);
                }
            return StatAggregator.Aggregate(lines);
        }
        public static double LoadoutDpsWith(int[] gear, Dictionary<int, List<GearStat>> socketLines)
            => DamageFormula.ExpectedDps(ToCombat(LoadoutStatsWith(gear, socketLines)));
    }

    /// <summary>Immutable store of each equipped item's REAL applied socket effects (read from the save's
    /// EnchantData), positioned by socket cell. Kept out of the injected MonoBehaviour so GearStat never
    /// appears in its field/method layout.</summary>
    public static class RealSockets
    {
        private static readonly Dictionary<int, Dictionary<int, GearStat[]>> _d = new Dictionary<int, Dictionary<int, GearStat[]>>();
        public static void Clear() => _d.Clear();
        public static void Set(int hero, int slot, GearStat[] cells)
        {
            if (!_d.TryGetValue(hero, out var m)) { m = new Dictionary<int, GearStat[]>(); _d[hero] = m; }
            m[slot] = cells;
        }
        public static GearStat[] Get(int hero, int slot) => (_d.TryGetValue(hero, out var m) && m.TryGetValue(slot, out var c)) ? c : null;
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
