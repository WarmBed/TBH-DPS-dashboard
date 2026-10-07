using System;
using System.Collections.Generic;
using System.Reflection;

namespace TbhDpsMeter
{
    /// <summary>Reads a hero's LIVE stat modifiers straight out of the game's own stat engine and re-folds
    /// them with the game's own aggregation, so "what would this stat be if I changed X" is exact arithmetic
    /// rather than a tooltip-derived guess.
    ///
    /// The engine (TaskbarHero: stat block `bat` -> registry `ws` -> modifiers `wr`) keeps every stat as a
    /// list of modifiers, each carrying StatType + MODTYPE{FLAT,ADDITIVE,MULTIPLICATIVE} + value +
    /// MODSOURCE{BASE,ITEM,ATTRIBUTE,PASSIVE,AccountStatus,StatusEffect,BuffSkill,ENVIROUNMENT} + a source
    /// tag (for ITEM mods the tag is the item's UniqueId, so a single item's contribution is separable).
    ///
    /// Aggregation is ΣFLAT × (1 + ΣADDITIVE) × Π(1 + MULTIPLICATIVE), confirmed against the live engine:
    /// Armor ΣFLAT 11460 × (1 + 4.787) = 66318.8 vs the engine's own 66319.023. <see cref="Verify"/> re-runs
    /// that reconciliation every session and logs it, so a game update that changes the folding shows up as a
    /// loud mismatch instead of silently wrong predictions.
    ///
    /// Everything is resolved by TYPE shape (Dictionary&lt;StatType, List&lt;…&gt;&gt;, property types MODTYPE /
    /// MODSOURCE / Single), never by obfuscated member name, per the project's resolution convention.</summary>
    internal static class LiveStats
    {
        public const int StatAttackDamage = 1, StatAttackSpeed = 2, StatCritChance = 3,
                         StatCritDamage = 4, StatMaxHp = 5, StatArmor = 6;

        public struct Mod
        {
            public int Type;       // 0 FLAT, 1 ADDITIVE, 2 MULTIPLICATIVE
            public double Value;
            public string Source;  // MODSOURCE name
            public string Tag;     // ITEM mods: the item's UniqueId as a string
            public Mod(int t, double v, string src, string tag) { Type = t; Value = v; Source = src; Tag = tag; }
        }

        /// <summary>Fold a modifier set the way the engine does.</summary>
        public static double Compute(IEnumerable<Mod> mods)
        {
            double flat = 0, add = 0, mul = 1;
            if (mods != null)
                foreach (var m in mods)
                {
                    if (m.Type == 0) flat += m.Value;
                    else if (m.Type == 1) add += m.Value;
                    else mul *= 1.0 + m.Value;
                }
            return flat * (1.0 + add) * mul;
        }

        /// <summary>Fold a baseline with one item's contribution swapped out for another's: every modifier
        /// tagged <paramref name="dropTag"/> is removed and <paramref name="add"/> is folded in instead.
        /// Pass a null/empty tag to only add, or an empty list to only remove.</summary>
        public static double ComputeWith(List<Mod> baseline, string dropTag, IEnumerable<Mod> add)
        {
            var merged = new List<Mod>();
            if (baseline != null)
                foreach (var m in baseline)
                    if (string.IsNullOrEmpty(dropTag) || m.Tag != dropTag) merged.Add(m);
            if (add != null) merged.AddRange(add);
            return Compute(merged);
        }

        /// <summary>Convert a gear/socket stat line (GearStat-style: stat name, "FLAT"/"ADDITIVE"/
        /// "MULTIPLICATIVE", raw data value) into an engine modifier. Percent values in the game data are
        /// stored x10 (ADDITIVE 616 = 61.6%), which the engine's own lists carry already divided — so
        /// non-FLAT values are scaled by /1000 to match. <paramref name="sign"/> -1 removes a contribution.</summary>
        public static Mod FromGearStat(string mod, double value, int sign)
        {
            int type = mod == "ADDITIVE" ? 1 : (mod == "MULTIPLICATIVE" ? 2 : 0);
            double v = type == 0 ? value : value / 1000.0;
            return new Mod(type, v * sign, "SIM", "");
        }

        // ---- live registry reading -------------------------------------------------------------

        /// <summary>Every live modifier feeding one stat on this stat block, or an empty list. The registry
        /// exposes SEVERAL Dictionary&lt;StatType, List&lt;…&gt;&gt; properties — some are distinct modifier sets
        /// (missing one silently under-counts: AttackDamage folded 1.9x low off a single set) and some are
        /// read-only views of another, so they're merged with reference-identity dedupe.</summary>
        public static List<Mod> Read(object statBlock, int statType)
        {
            var outp = new List<Mod>();
            foreach (var dict in StatDicts(statBlock))
            {
                try
                {
                    foreach (var kv in Refl.EnumerateE(dict))
                    {
                        object key = Refl.Get(kv, "Key");
                        if (key == null || Convert.ToInt32(key) != statType) continue;
                        foreach (var mod in Refl.Enumerate(Refl.Get(kv, "Value")))
                            outp.Add(ReadMod(mod));
                        break;
                    }
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("LiveStats.Read: " + e.Message); }
            }
            return outp;
        }

        private static Mod ReadMod(object mod)
        {
            int type = 0; double val = 0; string src = "?", tag = "";
            foreach (var p in mod.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                object v = null;
                try { v = p.GetValue(mod); } catch { continue; }
                switch (p.PropertyType.Name)
                {
                    case "MODTYPE": try { type = Convert.ToInt32(v); } catch { } break;
                    case "MODSOURCE": src = v?.ToString() ?? "?"; break;
                    case "Single": try { val = Convert.ToDouble(v); } catch { } break;
                    case "String": if (string.IsNullOrEmpty(tag)) tag = v as string ?? ""; break;
                }
            }
            return new Mod(type, val, src, tag);
        }

        // stat block -> every distinct modifier dictionary on its registry, cached per block type.
        private static MemberInfo _regAccessor; private static List<PropertyInfo> _regDicts; private static Type _blockType;

        private static List<object> StatDicts(object statBlock)
        {
            var result = new List<object>();
            if (statBlock == null) return result;
            try
            {
                if (_blockType != statBlock.GetType()) { _blockType = statBlock.GetType(); _regAccessor = null; _regDicts = null; }
                object reg = _regAccessor != null ? Invoke(statBlock, _regAccessor) : null;
                if (reg == null)
                {
                    foreach (var a in Accessors(statBlock.GetType()))
                    {
                        var t = MemberType(a);
                        if (t == null || t.IsPrimitive || t.IsEnum || t.IsValueType || t == typeof(string)) continue;
                        var dps = new List<PropertyInfo>();
                        try { foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) if (IsStatListDict(p.PropertyType)) dps.Add(p); }
                        catch { continue; }
                        if (dps.Count == 0) continue;
                        object cand = Invoke(statBlock, a);
                        if (cand == null) continue;
                        _regAccessor = a; _regDicts = dps; reg = cand;
                        break;
                    }
                }
                if (reg == null || _regDicts == null) return result;
                foreach (var p in _regDicts)
                {
                    object d = null;
                    try { d = p.GetValue(reg); } catch { }
                    if (d == null) continue;
                    bool dup = false;
                    foreach (var seen in result) if (ReferenceEquals(seen, d)) { dup = true; break; }
                    if (!dup) result.Add(d);
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("LiveStats.StatDicts: " + e.Message); }
            return result;
        }

        private static bool IsStatListDict(Type t)
            => t != null && t.IsGenericType && t.Name.StartsWith("Dictionary")
               && t.GetGenericArguments().Length == 2
               && t.GetGenericArguments()[0].Name == "StatType"
               && t.GetGenericArguments()[1].Name.StartsWith("List");

        private static IEnumerable<MemberInfo> Accessors(Type t)
        {
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance)) if (p.CanRead && p.GetIndexParameters().Length == 0) yield return p;
            foreach (var f in t.GetFields(BindingFlags.Public | BindingFlags.Instance)) yield return f;
        }

        private static Type MemberType(MemberInfo m)
            => m is PropertyInfo p ? p.PropertyType : (m as FieldInfo)?.FieldType;

        private static object Invoke(object o, MemberInfo m)
        {
            try { return m is PropertyInfo p ? p.GetValue(o) : (m as FieldInfo)?.GetValue(o); }
            catch { return null; }
        }

        // ---- reconciliation receipt ------------------------------------------------------------

        private static bool _verified;

        /// <summary>Once per session: re-fold every stat we predict from and compare against the engine's own
        /// value. A mismatch means the aggregation changed (game update) and predictions can't be trusted —
        /// logged loudly rather than silently returning wrong numbers.</summary>
        public static void Verify(object statBlock, Func<int, double> liveGetter)
        {
            if (_verified || statBlock == null || liveGetter == null) return;
            if (StatDicts(statBlock).Count == 0) return;
            _verified = true;
            int[] stats = { StatAttackDamage, StatAttackSpeed, StatCritChance, StatCritDamage, StatMaxHp, StatArmor };
            foreach (int s in stats)
            {
                var mods = Read(statBlock, s);
                if (mods.Count == 0) continue;
                double mine = Compute(mods), live = liveGetter(s);
                double diff = Math.Abs(mine - live);
                bool ok = diff <= Math.Max(0.05, Math.Abs(live) * 0.001);
                Plugin.Logger?.LogInfo($"[livestats] stat={s} mods={mods.Count} computed={mine:0.###} live={live:0.###} {(ok ? "OK" : "MISMATCH")}");
            }
        }
    }
}
