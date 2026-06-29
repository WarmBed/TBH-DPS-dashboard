using System;
using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>One repeating attack stream of a hero: a hit of <see cref="PerHit"/> damage every
    /// <see cref="Interval"/> seconds, striking up to <see cref="Targets"/> monsters per cast (1 = single,
    /// &gt;1 = AoE). Auto-attacks and each skill are separate streams. PerHit already folds in the crit
    /// expectation; Interval = 1/AttackSpeed (auto) or the skill's effective cooldown.</summary>
    public struct AtkStream
    {
        public double PerHit;
        public double Interval;
        public int Targets;
        public AtkStream(double perHit, double interval, int targets) { PerHit = perHit; Interval = interval; Targets = targets; }
    }

    /// <summary>Event-driven battle/clear-time SIMULATOR. Unlike the closed-form `floor + active/F`, this kills
    /// monsters HIT BY HIT, so it captures the things that model can't: OVERKILL (a hit's excess over a
    /// monster's remaining HP is wasted, so once you one-shot, more damage stops helping), the CADENCE FLOOR
    /// (a wave still needs one killing blow per monster at your attack rate), and AoE (one cast clears several).
    /// Pure C#, no Unity deps — unit-tested.</summary>
    public static class WaveSim
    {
        /// <summary>Seconds to clear one wave: <paramref name="count"/> monsters each with <paramref name="hp"/>
        /// effective HP, struck by the given attack streams. Each cast hits up to its Targets alive monsters
        /// (the lowest-HP ones, i.e. finish-off / focus-fire); excess damage on a kill is wasted (overkill).
        /// First hit of each stream lands after one Interval (cast windup). Returns +∞ if nothing can damage.</summary>
        public static double WaveTime(double hp, int count, IList<AtkStream> atks)
        {
            if (count <= 0 || hp <= 0) return 0;
            if (atks == null || atks.Count == 0) return double.PositiveInfinity;
            // schedule: next fire time per usable stream
            var nextT = new double[atks.Count];
            bool any = false;
            for (int k = 0; k < atks.Count; k++)
            {
                bool usable = atks[k].PerHit > 0 && atks[k].Interval > 0;
                nextT[k] = usable ? atks[k].Interval : double.PositiveInfinity;
                any |= usable;
            }
            if (!any) return double.PositiveInfinity;

            var rem = new double[count];
            for (int i = 0; i < count; i++) rem[i] = hp;
            var pick = new int[count];     // the distinct targets struck by one cast (lowest-HP first)
            int alive = count;
            double t = 0;
            long guard = 0, guardMax = (long)count * 1000 + 1000;   // backstop vs a non-killing config
            while (alive > 0 && guard++ < guardMax)
            {
                int ke = -1; double te = double.MaxValue;
                for (int k = 0; k < atks.Count; k++) if (nextT[k] < te) { te = nextT[k]; ke = k; }
                if (ke < 0 || double.IsPositiveInfinity(te)) return double.PositiveInfinity;
                t = te;
                var a = atks[ke];
                int hits = Math.Min(a.Targets < 1 ? 1 : a.Targets, alive);
                // pick `hits` DISTINCT lowest-HP alive monsters (finish-off / spread), then damage each ONCE — a
                // multi-target cast must not re-hit the same monster (that was an AoE under-credit bug).
                int picked = 0;
                for (int h = 0; h < hits; h++)
                {
                    int li = -1; double lo = double.MaxValue;
                    for (int i = 0; i < count; i++)
                    {
                        if (rem[i] <= 0 || rem[i] >= lo) continue;
                        bool already = false;
                        for (int p = 0; p < picked; p++) if (pick[p] == i) { already = true; break; }
                        if (!already) { lo = rem[i]; li = i; }
                    }
                    if (li < 0) break;
                    pick[picked++] = li;
                }
                for (int p = 0; p < picked; p++)
                {
                    rem[pick[p]] -= a.PerHit;        // overkill: excess is simply lost
                    if (rem[pick[p]] <= 0) alive--;
                }
                nextT[ke] = t + a.Interval;
            }
            return t;
        }

        /// <summary>Party KILL-RATE (kills/sec) = Σ over attack streams of Targets/Interval. On one-shot farm
        /// content every cast kills its targets, so clear time ∝ 1/rate — this is the cadence the active/kill
        /// phase compresses to. It is independent of PER-HIT DAMAGE (raising attack/crit doesn't change Targets
        /// or Interval), so damage edits correctly produce ZERO clear-time change; only AttackSpeed/CDR/CastSpeed
        /// (Interval) and AoE/Multistrike/ProjectileCount (Targets) move it. Verified by the 100%-coverage audit.</summary>
        public static double CadenceRate(IList<AtkStream> atks)
        {
            if (atks == null) return 0;
            double r = 0;
            foreach (var a in atks) if (a.Interval > 1e-9 && a.Targets > 0) r += a.Targets / a.Interval;
            return r;
        }

        /// <summary>Pure BATTLE time only (no spawn/approach/end floor): Σ over waves of the iterative kill time,
        /// plus the boss. The bench adds an EMPIRICAL per-wave floor (from the run's measured per-wave durations)
        /// on top — that floor is the part DPS can't compress, so it must not be inside the calibrated battle.</summary>
        public static double BattleTime(int waves, int monstersPerWave, double monsterHp, IList<AtkStream> atks, double bossHp)
        {
            double t = (waves > 0 ? waves : 0) * WaveTime(monsterHp, monstersPerWave, atks);
            if (bossHp > 0) t += WaveTime(bossHp, 1, atks);
            return t;
        }

        /// <summary>Full stage clear time. Each wave = fixed spawn-anim (0.8s) + approach (move-bound) + the
        /// iterative battle; the boss is a 1-monster wave. Plus stage entry and the 4.25s stage-end. The fixed
        /// delays are the decompiled constants (see [[combat-clear-model]]). approachPerWave is the move/range
        /// term (0 when ranged/one-shot dominate).</summary>
        public static double StageTime(int waves, int monstersPerWave, double monsterHp,
            IList<AtkStream> atks, double approachPerWave, double stageEntry, double bossHp)
        {
            double spawn = 0.8, end = 4.25;
            double t = stageEntry;
            double battle = WaveTime(monsterHp, monstersPerWave, atks);
            for (int w = 0; w < (waves > 0 ? waves : 0); w++)
                t += spawn + (approachPerWave > 0 ? approachPerWave : 0) + battle;
            if (bossHp > 0)
                t += spawn + (approachPerWave > 0 ? approachPerWave : 0) + WaveTime(bossHp, 1, atks);
            return t + end;
        }
    }
}
