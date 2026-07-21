using System;
using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>One skill's stream parameters (from fit_skills.json, built from SkillInfoData/SkillLevelInfoData).</summary>
    public struct SkillInfo
    {
        public int Act;        // 0 BASEATTACK / 1 BASEATTACK_COUNT / 2 COOLDOWN
        public double Av;      // ActivationValue: N (count) or cooldown seconds
        public bool Aoe;
        public double[] Coeffs;  // damage coefficient per level (1..10); single-element if level-independent
        public double Coeff(int lvl)
        {
            if (Coeffs == null || Coeffs.Length == 0) return 1.0;
            int i = lvl - 1; if (i < 0) i = 0; if (i >= Coeffs.Length) i = Coeffs.Length - 1;
            return Coeffs[i];
        }
    }

    /// <summary>Skill catalog (fit_skills.json): {skillKey: [act, av, aoe, [coeff per level]]}.</summary>
    public static class SkillDb
    {
        static readonly Dictionary<int, SkillInfo> _m = new Dictionary<int, SkillInfo>();
        public static bool Loaded;
        public static int Count => _m.Count;
        public static void Load(string json)
        {
            _m.Clear();
            var obj = Json.Obj(Json.Parse(json));
            if (obj == null) return;
            foreach (var kv in obj)
            {
                if (!int.TryParse(kv.Key, out var key)) continue;
                var a = Json.Arr(kv.Value);
                if (a == null || a.Count < 4) continue;
                var cr = Json.Arr(a[3]);
                var coeffs = new double[cr != null ? cr.Count : 0];
                for (int i = 0; i < coeffs.Length; i++) coeffs[i] = Json.Num(cr[i]);
                _m[key] = new SkillInfo { Act = (int)Json.Num(a[0]), Av = Json.Num(a[1]), Aoe = Json.Num(a[2]) != 0, Coeffs = coeffs };
            }
            Loaded = true;
        }
        public static bool TryGet(int key, out SkillInfo s) => _m.TryGetValue(key, out s);
    }

    /// <summary>Per-stage structure (fit_stages.json): {"Act-Stage DIFF": [waves, monstersPerWave, effHP, bossHP]}.
    /// effHP/bossHP are the raw CSV values (MaxLife × MonsterHpMultiplier); the bench auto-calibrates a scale
    /// against the measured run, since the CSV multiplier is overstated ~10× (see [[combat-clear-model]]).</summary>
    public struct StageInfo { public int Waves, Mpw; public double EffHp, BossHp; }
    public static class StageDb
    {
        static readonly Dictionary<string, StageInfo> _m = new Dictionary<string, StageInfo>();
        public static int Count => _m.Count;
        public static void Load(string json)
        {
            _m.Clear();
            var obj = Json.Obj(Json.Parse(json));
            if (obj == null) return;
            foreach (var kv in obj)
            {
                var a = Json.Arr(kv.Value);
                if (a == null || a.Count < 4) continue;
                _m[kv.Key] = new StageInfo { Waves = (int)Json.Num(a[0]), Mpw = (int)Json.Num(a[1]), EffHp = Json.Num(a[2]), BossHp = Json.Num(a[3]) };
            }
        }
        public static bool TryGet(string id, out StageInfo s) => _m.TryGetValue(id, out s);
    }

    /// <summary>Precomputed per-stage context for the socket optimizer. Clear = Floor(approach/idle) + BM(kill/
    /// active) × ratio, where ratio = 1 / Σ_hero share_h·(rate_h/curRate_h). Only the FOCUSED hero's rate changes
    /// per candidate, so each stage caches its measured damage SHARE + CURRENT kill-rate; the others contribute a
    /// fixed (1−FocShare). Share-weighting grounds the per-hero split in the run's real DamageDealt (so a buffing
    /// support that barely kills can't dominate). Parallel arrays indexed by farmed-stage. Plain class.</summary>
    public class OptCtx
    {
        public int N;
        public double[] Floor, BM;          // per-stage approach(idle) floor + kill(active) seconds
        public double[] FocShare, FocCur;   // focused hero's measured damage share + current-gear kill-rate, per stage
        public bool[] FocIn, Valid;
    }

    /// <summary>One socket the optimizer changed: which hero/slot/position, the chosen material key, and the
    /// socket type + gear-group needed to resolve its icon/name/effect for the changelog panel.</summary>
    public class OptChange { public int Hero, Slot, Pos, MatKey; public char Type; public string Gg = ""; }

    /// <summary>Turns a hero's final combat stats + equipped skills into the per-attack streams the iterative
    /// WaveSim consumes. auto = attack·critMult every 1/AttackSpeed; skill = attack·critMult·coeff every
    /// (BASEATTACK_COUNT: N/AttackSpeed; COOLDOWN: max(0.1, baseCD·(1−CDR))). AoE skills hit AoeTargets.</summary>
    public static class StreamBuilder
    {
        public const int AoeTargets = 2;   // calibrated: drip-spawn + spread ⇒ a nuke effectively hits ~2 at a time

        public static List<AtkStream> Build(int heroKey, double attack, double aspd, double critRate, double critDmg, double cdr,
            IList<int> skillKeys, IList<int> skillLevels, double aoeMult = 1.0)
        {
            var streams = new List<AtkStream>();
            if (attack <= 0 || aspd <= 0) return streams;
            int baseKey = (heroKey / 100) * 10000 + 1;   // 201→20001, 301→30001, …  (implicit base attack)
            bool baseInList = false;
            if (skillKeys != null)
                for (int i = 0; i < skillKeys.Count; i++)
                {
                    if (skillKeys[i] == baseKey) baseInList = true;
                    int lvl = (skillLevels != null && i < skillLevels.Count) ? skillLevels[i] : 1;
                    Add(streams, skillKeys[i], lvl, attack, critRate, critDmg, aspd, cdr, aoeMult);
                }
            if (!baseInList) Add(streams, baseKey, 1, attack, critRate, critDmg, aspd, cdr, aoeMult);
            return streams;
        }

        static void Add(List<AtkStream> streams, int key, int lvl, double attack, double critRate, double critDmg, double aspd, double cdr, double aoeMult)
        {
            if (!SkillDb.TryGet(key, out var s)) return;
            double interval;
            if (s.Act == 0) interval = 1.0 / aspd;
            else if (s.Act == 1) interval = s.Av > 0 ? s.Av / aspd : 0;
            else if (s.Act == 2) interval = s.Av > 0 ? Math.Max(0.1, s.Av * (1.0 - cdr)) : 0;
            else return;
            if (interval <= 0) return;
            // AoE target count scales with the 範圍/AreaOfEffect stat (verified: detect radius = base × AoE mult,
            // radial test over all alive units, no count cap). Baseline AoeTargets=2 ⇒ the RATIO is what moves it.
            int targets = s.Aoe ? Math.Max(1, (int)Math.Round(AoeTargets * (aoeMult > 0 ? aoeMult : 1.0))) : 1;
            double coeff = s.Coeff(lvl);
            // Split crit vs non-crit so OVERKILL is judged per hit-type, not on the average. A normal (non-crit)
            // hit = attack·coeff lands (1−critRate) of the time; a crit = attack·critDmg·coeff lands critRate of
            // the time. Total hit rate is preserved (=1/interval). If the non-crit hit already one-shots, more
            // crit is pure overkill and dropping crit rate costs nothing — which is exactly what the user expects.
            double cr = critRate < 0 ? 0 : (critRate > 1 ? 1 : critRate);
            if (1 - cr > 0.001) streams.Add(new AtkStream(attack * coeff, interval / (1 - cr), targets));
            if (cr > 0.001) streams.Add(new AtkStream(attack * critDmg * coeff, interval / cr, targets));
        }

        public const double CdrCap = 0.75;        // CooldownReduction hard cap (decompiled Hero.gre, 0x3F400000)
        public const double BaseCastClip = 0.5;   // est. cooldown-skill cast/animation lock (s); CastSpeed divides it

        /// <summary>Per-hero KILLS/SEC on one-shot content, modelled as the game's REAL serialized action queue
        /// (decompiled Unit.gqh: one action per frame, a ready COOLDOWN skill takes priority and SUPPRESSES the
        /// base auto; the cast occupies an animation lock = clip/CastSpeed, NOT bounded by AttackSpeed).
        ///   • COOLDOWN skills (act 2) recast every max(castTime, max(0.1, baseCD·(1−CDR_eff))) — CDR_eff is HARD
        ///     CAPPED at 0.75 (Hero.gre), so a hero already past 0.75 gets ZERO from more CDR. Each kills its targets.
        ///   • the cast occupies castTime each, so the base auto only fills the LEFTOVER timeline: AttackSpeed·(1−Σocc).
        ///   • BASEATTACK_COUNT (act 1) REPLACES every Av-th auto — adds (targets−1)/Av extra targets per auto.
        ///   • if casts over-subscribe the timeline (Σocc≥1) the hero is cast-bound — rate = cooldownKills/Σocc, and
        ///     only CastSpeed (shorter castTime) or AoE (more targets) help further, NOT AttackSpeed/CDR.
        /// Damage-independent (no attack/crit term) ⇒ damage edits give ZERO clear change. AttackSpeed & AoE are
        /// uncapped (decompiled); CDR capped at 0.75; CastSpeed shortens the cast lock.</summary>
        public static double HeroKillRate(int heroKey, double aspd, double cdr,
            IList<int> skillKeys, IList<int> skillLevels, double aoeMult = 1.0, double castSpeed = 1.0)
        {
            if (aspd <= 0) return 0;
            double mult = aoeMult > 0 ? aoeMult : 1.0;
            double cdrEff = cdr > CdrCap ? CdrCap : cdr;                       // combat clamps CDR to 0.75
            double castTime = BaseCastClip / Math.Max(0.3, castSpeed);        // animation lock = clip / CastSpeed
            double cooldownKills = 0, occupancy = 0, autoExtraTargets = 0;
            int baseKey = (heroKey / 100) * 10000 + 1;
            bool baseInList = false;
            void Acc(int key, int lvl)
            {
                if (!SkillDb.TryGet(key, out var s)) return;
                // EXPECTED monsters hit per cast — fractional (a wider AoE radius catches more on average, smoothly;
                // integer-rounding made small 範圍 gains vanish at the 2.4→2 step). Single-target skills hit 1.
                double targets = s.Aoe ? Math.Max(1.0, AoeTargets * mult) : 1.0;
                if (s.Act == 2 && s.Av > 0)                       // COOLDOWN — own cadence, suppresses the auto
                {
                    double interval = Math.Max(castTime, Math.Max(0.1, s.Av * (1.0 - cdrEff)));
                    double rate = 1.0 / interval;
                    cooldownKills += targets * rate; occupancy += castTime * rate;
                }
                else if (s.Act == 1 && s.Av > 0 && targets > 1)   // count-skill upgrades every Av-th auto's targets
                    autoExtraTargets += (targets - 1.0) / s.Av;
                // act 0 (base auto) contributes through the auto budget below, not as its own stream
            }
            if (skillKeys != null)
                for (int i = 0; i < skillKeys.Count; i++)
                {
                    if (skillKeys[i] == baseKey) baseInList = true;
                    Acc(skillKeys[i], (skillLevels != null && i < skillLevels.Count) ? skillLevels[i] : 1);
                }
            if (!baseInList) Acc(baseKey, 1);
            if (occupancy >= 1.0) return cooldownKills / occupancy;           // cast-bound: casts saturate the timeline
            double autoRate = aspd * (1.0 - occupancy);                       // base auto fills the gaps between casts
            return cooldownKills + (1.0 + autoExtraTargets) * autoRate;
        }

        /// <summary>Binary-search the effHP scale k so the iterative BATTLE time (no floor) reproduces the
        /// measured battle portion (= measured clear − the empirical per-wave floor) with the CURRENT streams.
        /// BattleTime is monotonic decreasing in 1/k, so monotonic in k.</summary>
        public static double CalibrateHpScale(int waves, int mpw, double effHp, double bossHp, IList<AtkStream> streams, double battleSec)
        {
            double lo = 0.0001, hi = 5.0;
            double tLo = WaveSim.BattleTime(waves, mpw, effHp * lo, streams, bossHp * lo);
            double tHi = WaveSim.BattleTime(waves, mpw, effHp * hi, streams, bossHp * hi);
            if (battleSec <= tLo) return lo;
            if (battleSec >= tHi) return hi;
            for (int it = 0; it < 36; it++)
            {
                double mid = (lo + hi) * 0.5;
                double t = WaveSim.BattleTime(waves, mpw, effHp * mid, streams, bossHp * mid);
                if (t < battleSec) lo = mid; else hi = mid;
            }
            return (lo + hi) * 0.5;
        }
    }
}
