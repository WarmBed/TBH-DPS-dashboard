using System;
using System.Collections.Generic;
using System.IO;

namespace TbhDpsMeter
{
    /// <summary>Saves/loads run records as simple text files under BepInEx/config/dpsmeter_runs.
    /// Serialization lives in RunSerializer (pure C#, unit-tested); this class is just file I/O.</summary>
    public static class RunStore
    {
        // Retention is PER STAGE (see RunRetention): keep the newest runs of each stage so farming one
        // stage never evicts another stage's comparison history. A global ceiling bounds total disk/memory.
        private const int PerStageCap = 60;
        private const int GlobalCap = 400;
        private static string Dir => Path.Combine(BepInEx.Paths.ConfigPath, "dpsmeter_runs");

        /// <summary>Bumped whenever the saved set changes (save / delete), so open UIs can auto-refresh.</summary>
        public static int Version;

        public static void Save(RunRecord r)
        {
            try
            {
                Directory.CreateDirectory(Dir);
                string file = Path.Combine(Dir, "run_" + DateTime.Now.ToString("yyyyMMdd_HHmmss_fff") + ".txt");
                File.WriteAllText(file, RunSerializer.Serialize(r));
                Prune();
                Version++;
            }
            catch (Exception e) { Plugin.Logger?.LogError("RunStore.Save: " + e.Message); }
        }

        private static void Prune()
        {
            try
            {
                var files = new List<string>(Directory.GetFiles(Dir, "run_*.txt"));
                files.Sort();   // filename embeds yyyyMMdd_HHmmss_fff -> chronological (oldest first)
                var entries = new List<(string path, string stage)>(files.Count);
                foreach (var f in files) entries.Add((f, ReadStageId(f)));
                foreach (var path in RunRetention.SelectExpired(entries, PerStageCap, GlobalCap))
                    try { File.Delete(path); } catch { }
            }
            catch { }
        }

        /// <summary>Cheap stage-id read for pruning: the "stageid=" line is near the top of the file,
        /// so stop as soon as we see it (or the first character block). Empty if unreadable.</summary>
        private static string ReadStageId(string file)
        {
            try
            {
                foreach (var line in File.ReadLines(file))
                {
                    if (line.StartsWith("stageid=")) return line.Substring(8).Trim();
                    if (line.StartsWith("char=") || line.StartsWith("snap=")) break;   // past the header
                }
            }
            catch { }
            return "";
        }

        /// <summary>Delete all saved run records. Returns the number of files removed.</summary>
        public static int DeleteAll()
        {
            int n = 0;
            try
            {
                if (!Directory.Exists(Dir)) return 0;
                foreach (var f in Directory.GetFiles(Dir, "run_*.txt"))
                    try { File.Delete(f); n++; } catch { }
                Version++;
            }
            catch (Exception e) { Plugin.Logger?.LogError("RunStore.DeleteAll: " + e.Message); }
            return n;
        }

        /// <summary>Delete a single run by its backing file. Returns true if removed.</summary>
        public static bool Delete(RunRecord r)
        {
            try
            {
                if (r == null || string.IsNullOrEmpty(r.SourceFile) || !File.Exists(r.SourceFile)) return false;
                File.Delete(r.SourceFile);
                Version++;
                return true;
            }
            catch (Exception e) { Plugin.Logger?.LogError("RunStore.Delete: " + e.Message); return false; }
        }

        /// <summary>Returns runs oldest..newest.</summary>
        public static List<RunRecord> LoadAll()
        {
            var list = new List<RunRecord>();
            try
            {
                if (!Directory.Exists(Dir)) return list;
                var files = new List<string>(Directory.GetFiles(Dir, "run_*.txt"));
                files.Sort();
                foreach (var f in files)
                {
                    try { var rec = RunSerializer.Deserialize(File.ReadAllLines(f)); rec.SourceFile = f; list.Add(rec); }
                    catch (Exception e) { Plugin.Logger?.LogError("RunStore.Load one: " + e.Message); }
                }
            }
            catch (Exception e) { Plugin.Logger?.LogError("RunStore.LoadAll: " + e.Message); }
            return list;
        }
    }
}
