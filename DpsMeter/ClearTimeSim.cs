using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>A stage's representative active/idle split (有效輸出 / 停輸出), aggregated from real runs.</summary>
    public struct StageTiming
    {
        public double ActiveSec;   // 有效輸出 — time spent dealing damage (DPS-bound)
        public double IdleSec;     // 停輸出 — time with no outgoing damage: moving / waiting for spawns (speed-bound)
        public int Samples;        // runs backing this
        public bool HasData;
    }

    /// <summary>The simulated outcome for one stage under a given party-DPS factor and speed multiplier.</summary>
    public struct SimRow
    {
        public double BaseClear;   // original clear seconds
        public double NewClear;    // simulated clear seconds
        public double SavedSec;    // BaseClear - NewClear
        public double SavedPct;    // SavedSec / BaseClear (0 when BaseClear <= 0)
        public double DpsFrac;     // 0..1 fraction of the clear that is DPS-bound (active / total)
    }

    /// <summary>Aggregate over a set of simulated stages (known-clear rows only).</summary>
    public struct SimSummary
    {
        public double PartyDpsFactor;
        public double AvgSavedPct;
        public double BestSavedSec;
        public int BestIndex;
        public int Counted;
    }

    /// <summary>Pure-C# clear-time what-if simulator. A stage's clear time is split into a DPS-bound part
    /// (有效輸出, measured) and a movement-bound part (停輸出, measured); DPS rescales the first, move speed
    /// the second. Both are best-case upper bounds — overkill + cast lockout floor the DPS side, spawn waits
    /// floor the speed side. No Unity/BepInEx deps — fully unit-tested.</summary>
    public static class ClearTimeSim
    {
        /// <summary>Sum a run's equipped-gear total for one stable stat key (e.g. "AoE" = range, "attack",
        /// "mspd"). <paramref name="heroKey"/> null/empty = whole party; otherwise only that hero's gear.
        /// Stat keys come from SaveGearReader.StatName, so they're stable across languages.</summary>
        public static double BuildStat(RunRecord run, string statName, string heroKey)
        {
            if (run == null || run.Party == null || string.IsNullOrEmpty(statName)) return 0;
            double sum = 0;
            foreach (var h in run.Party)
            {
                if (h == null || h.Equipment == null) continue;
                if (!string.IsNullOrEmpty(heroKey) && h.Character != heroKey) continue;
                foreach (var g in h.Equipment)
                {
                    if (g == null || g.Affixes == null) continue;
                    foreach (var a in g.Affixes)
                        if (a.Name == statName) sum += a.Value;
                }
            }
            return sum;
        }

        /// <summary>Party-DPS factor F = Σ (share_h × mult_h) = new party DPS / old party DPS.</summary>
        public static double PartyDpsFactor(IDictionary<int, double> shares, IDictionary<int, double> mult)
        {
            if (shares == null || shares.Count == 0) return 1.0;
            double f = 0;
            foreach (var kv in shares)
            {
                double m = 1.0;
                if (mult != null && mult.TryGetValue(kv.Key, out var mv)) m = mv;
                f += kv.Value * m;
            }
            return f > 0 ? f : 1.0;
        }

        /// <summary>Representative active/idle split for a stage, as the median over its runs that captured a
        /// split (ActiveSeconds &gt; 0). When <paramref name="buildSig"/> is non-null only matching-build runs
        /// count, so the timing reflects the current loadout. No matching runs ⇒ HasData = false.</summary>
        public static StageTiming AggregateTiming(IEnumerable<RunRecord> runs, string stageId, string buildSig)
        {
            var t = new StageTiming();
            if (runs == null || string.IsNullOrEmpty(stageId)) return t;
            var actives = new List<double>();
            var idles = new List<double>();
            foreach (var r in runs)
            {
                if (r == null || r.StageId != stageId) continue;
                if (r.ActiveSeconds <= 0) continue;                 // run predates / lacks the active/idle split
                if (buildSig != null && FarmPlanner.BuildSignature(r) != buildSig) continue;
                actives.Add(r.ActiveSeconds);
                idles.Add(r.IdleSeconds);
            }
            if (actives.Count == 0) return t;
            t.ActiveSec = FarmPlanner.Median(actives);
            t.IdleSec = FarmPlanner.Median(idles);
            t.Samples = actives.Count;
            t.HasData = true;
            return t;
        }

        /// <summary>Simulate from a measured active/idle split: DPS rescales 有效輸出, speed rescales 停輸出.
        /// total ≤ 0 yields an all-zero row (nothing to simulate).</summary>
        public static SimRow SimulateSplit(double activeSec, double idleSec, double partyDpsFactor, double speedMult)
        {
            double total = activeSec + idleSec;
            var row = new SimRow { BaseClear = total };
            if (total <= 0) return row;
            double f = partyDpsFactor > 0 ? partyDpsFactor : 1.0;
            double s = speedMult > 0 ? speedMult : 1.0;
            row.DpsFrac = activeSec / total;
            row.NewClear = activeSec / f + idleSec / s;
            row.SavedSec = total - row.NewClear;
            row.SavedPct = row.SavedSec / total;
            return row;
        }

        /// <summary>Simulate an UN-cleared stage from an estimated clear time and an assumed DPS-bound fraction
        /// (e.g. the average active-fraction of your cleared stages). clearSec ≤ 0 ⇒ all-zero row.</summary>
        public static SimRow SimulateFallback(double clearSec, double dpsFrac, double partyDpsFactor, double speedMult)
        {
            var row = new SimRow { BaseClear = clearSec };
            if (clearSec <= 0) return row;
            double f = partyDpsFactor > 0 ? partyDpsFactor : 1.0;
            double s = speedMult > 0 ? speedMult : 1.0;
            if (dpsFrac < 0) dpsFrac = 0;
            if (dpsFrac > 1) dpsFrac = 1;
            row.DpsFrac = dpsFrac;
            row.NewClear = clearSec * (dpsFrac / f + (1.0 - dpsFrac) / s);
            row.SavedSec = clearSec - row.NewClear;
            row.SavedPct = row.SavedSec / clearSec;
            return row;
        }

        /// <summary>Aggregate simulated rows: mean saved-% and the biggest absolute saving, over known-clear rows.</summary>
        public static SimSummary Summarize(IList<SimRow> rows, double partyDpsFactor)
        {
            var sum = new SimSummary { PartyDpsFactor = partyDpsFactor, BestIndex = -1 };
            if (rows == null) return sum;
            double pctTotal = 0;
            for (int i = 0; i < rows.Count; i++)
            {
                var r = rows[i];
                if (r.BaseClear <= 0) continue;
                sum.Counted++;
                pctTotal += r.SavedPct;
                if (sum.BestIndex < 0 || r.SavedSec > sum.BestSavedSec)
                {
                    sum.BestSavedSec = r.SavedSec;
                    sum.BestIndex = i;
                }
            }
            if (sum.Counted > 0) sum.AvgSavedPct = pctTotal / sum.Counted;
            return sum;
        }
    }
}
