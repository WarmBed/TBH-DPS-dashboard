using System;
using System.Reflection;

namespace TbhDpsMeter
{
    /// <summary>Startup health check for the obfuscation-sensitive bits. The game re-obfuscates private
    /// member names on every update, so anything resolved by a hard-coded name can silently break.
    /// This logs, once at load, whether each critical TYPE and each TYPE-resolved field is present —
    /// so when an update does shift things, `[selfcheck]` lines in the log pinpoint what broke instantly
    /// instead of the user noticing "something's wrong" later. All checks are type-level (no live
    /// instance needed) and never throw.</summary>
    public static class SelfCheck
    {
        public static void Run()
        {
            try
            {
                // critical types (these names are NOT obfuscated, so they're the stable anchor points)
                string types =
                    T("UIManager", "TaskbarHero.UIManager") + " " +
                    T("StageManager", "TaskbarHero.StageManager") + " " +
                    T("Hero", "TaskbarHero.Hero") + " " +
                    T("EMainTab", "TaskbarHero.EMainTab") + " " +
                    T("ESTAGEDIFFICULTY", "TaskbarHero.Data.ESTAGEDIFFICULTY") + " " +
                    "StageCache=" + (FindStageCache() != null ? "OK" : "MISSING");
                Plugin.Logger?.LogInfo("[selfcheck] types: " + types);

                // TYPE-resolved fields (the resilient lookups the plugin actually uses at runtime).
                // If any reads MISSING after a game update, that resolver is the thing to re-map.
                var ui = Refl.FindType("TaskbarHero.UIManager");
                Plugin.Logger?.LogInfo("[selfcheck] UIManager.currentTab (settable EMainTab) -> " + Prop(ui, "EMainTab", true));

                var sc = FindStageCache();
                Plugin.Logger?.LogInfo("[selfcheck] StageCache.difficulty (ESTAGEDIFFICULTY) -> " + Prop(sc, "ESTAGEDIFFICULTY", false)
                    + " ; label = first string field shaped \"N-N\" (resolved at capture)");

                // item-name localizer (separate Localization table from skills/heroes). May be too early at
                // load — GameLocItem self-retries lazily, so an "(unresolved)" here is benign.
                try
                {
                    foreach (var k in new[] { "ItemName_300001", "ItemName_620014" })
                    {
                        string probe = HeroProbe.GameLocItem(k);
                        Plugin.Logger?.LogInfo($"[selfcheck] item loc {k} -> "
                            + (string.IsNullOrEmpty(probe) ? "(unresolved at load; retries lazily)" : probe));
                    }
                }
                catch { }

                Plugin.Logger?.LogInfo("[selfcheck] Harmony hooks logged above as 'Patched: ...' / 'hooked ...' "
                    + "(a missing one means that method name changed).");
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[selfcheck] " + e.Message); }
        }

        private static string T(string label, string full) => label + "=" + (Refl.FindType(full) != null ? "OK" : "MISSING");

        /// <summary>Resolve the nested StageCache type by its (non-obfuscated) simple name, regardless of
        /// which obfuscated wrapper currently declares it (ue+StageCache → vb+StageCache → … churns).</summary>
        internal static Type FindStageCache()
        {
            try
            {
                foreach (var t in typeof(TaskbarHero.Hero).Assembly.GetTypes())
                    if (t.Name == "StageCache") return t;
            }
            catch { }
            return null;
        }

        /// <summary>Name of the first property of the given type (optionally settable), or a status token.</summary>
        private static string Prop(Type t, string propTypeName, bool needSetter)
        {
            if (t == null) return "TYPE-MISSING";
            try
            {
                foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
                    if (p.CanRead && (!needSetter || p.CanWrite) && p.PropertyType.Name == propTypeName)
                        return p.Name + " (OK)";
            }
            catch { }
            return "MISSING";
        }
    }
}
