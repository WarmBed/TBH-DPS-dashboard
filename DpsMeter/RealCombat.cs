using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>Live-combat probe. Rides the existing Monster take-damage hook to capture GROUND TRUTH the
    /// fitting sim otherwise has to calibrate/guess: the real monster effective HP (read off the damaged Unit)
    /// and the real AoE multiplicity (how many monsters one cast actually hits). Logged periodically; Step 1
    /// just surfaces the numbers so we can wire them into the sim and replace the ÷10 HP calibration + AoE≈2.</summary>
    public static class RealCombat
    {
        static readonly Dictionary<int, int> _hpHist = new Dictionary<int, int>();   // rounded MaxHp -> sample count
        static readonly Dictionary<long, int> _cast = new Dictionary<long, int>();    // (hero, frame) -> monsters hit
        static int _hits, _sample;

        public static void OnHit(object monster, double damage, int attackerKey, float t)
        {
            _hits++;
            // one cast ≈ the same hero's hits in the same frame (Unity Time.time is per-frame); count its targets
            long bucket = ((long)attackerKey << 20) ^ (long)Mathf.RoundToInt(t * 1000f);
            _cast[bucket] = (_cast.TryGetValue(bucket, out var n) ? n : 0) + 1;
            // real effHP: sample the damaged monster's MaxHp every 4th hit (reflection — throttled for perf)
            if ((_sample++ & 3) == 0)
            {
                double mhp = HeroProbe.ReadUnitMaxHp(monster);
                if (mhp > 0) { int k = (int)System.Math.Round(mhp); _hpHist[k] = (_hpHist.TryGetValue(k, out var c) ? c : 0) + 1; }
            }
            if (_hits % 500 == 0) Log();
        }

        public static void Log()
        {
            var hps = new List<int>();
            foreach (var kv in _hpHist) for (int i = 0; i < kv.Value; i++) hps.Add(kv.Key);
            hps.Sort();
            double med = hps.Count > 0 ? hps[hps.Count / 2] : 0, lo = hps.Count > 0 ? hps[0] : 0, hi = hps.Count > 0 ? hps[hps.Count - 1] : 0;
            int casts = 0; double tgt = 0; foreach (var kv in _cast) { casts++; tgt += kv.Value; }
            double aoe = casts > 0 ? tgt / casts : 1;
            Plugin.Logger?.LogInfo($"[realcombat] hits={_hits} effHP median={med:0} (range {lo:0}-{hi:0}, {_hpHist.Count} types) | casts={casts} AoE mean targets={aoe:0.00}");
        }

        public static void Reset() { _hpHist.Clear(); _cast.Clear(); _hits = 0; _sample = 0; }
    }
}
