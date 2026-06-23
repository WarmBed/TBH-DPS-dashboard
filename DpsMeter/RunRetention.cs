using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>Pure retention policy for saved run files (no IO, no BepInEx) so it can be unit-tested.
    /// Keeps the comparison history per STAGE rather than as one global FIFO, so farming one stage never
    /// evicts another stage's history (the bug behind "關卡比較 60 場左右出問題").</summary>
    public static class RunRetention
    {
        /// <summary>Given run files in chronological (oldest-first) order, return the paths to delete so
        /// each StageId keeps at most <paramref name="perStageCap"/> (newest), and the surviving total
        /// never exceeds <paramref name="globalCap"/> (newest overall).</summary>
        public static List<string> SelectExpired(IList<(string path, string stage)> chronological, int perStageCap, int globalCap)
        {
            var toDelete = new HashSet<string>();
            if (chronological == null || chronological.Count == 0) return new List<string>();

            // 1. per-stage cap: walk newest -> oldest, drop each stage's runs once it has kept perStageCap.
            var kept = new Dictionary<string, int>();
            for (int i = chronological.Count - 1; i >= 0; i--)
            {
                var e = chronological[i];
                string stg = e.stage ?? "";
                int n = kept.TryGetValue(stg, out var c) ? c : 0;
                if (n >= perStageCap) toDelete.Add(e.path);
                else kept[stg] = n + 1;
            }

            // 2. global ceiling: among survivors, drop the globally-oldest until within globalCap.
            int survivors = chronological.Count - toDelete.Count;
            for (int i = 0; i < chronological.Count && survivors > globalCap; i++)
            {
                var e = chronological[i];
                if (toDelete.Add(e.path)) survivors--;   // Add returns false if already marked
            }

            return new List<string>(toDelete);
        }
    }
}
