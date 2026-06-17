using System;
using System.Reflection;
using HarmonyLib;

namespace TbhDpsMeter
{
    /// <summary>Captures opened-box item quality. A prefix on StageBox's open method (single EBoxType
    /// param) records the kind currently being opened; a postfix on BoxOpenLog..ctor(string, EGradeType)
    /// records each dropped item with that kind. Type names (BoxOpenLog/EGradeType/StageBox/EBoxType) are
    /// readable and survive game updates; the StageBox open method is obfuscated so it is resolved by
    /// signature (instance method, one EBoxType param) rather than by name.</summary>
    public static class BoxOpenTracker
    {
        public static readonly BoxOpenStats Stats = new BoxOpenStats();

        private static int _openingKind = (int)BoxKind.Unknown;
        private static DateTime _lastFlush = DateTime.MinValue;

        /// <summary>Register both hooks. Call once from Plugin.Load with the shared Harmony instance.</summary>
        public static void Install(Harmony harmony)
        {
            // Hook A: the REAL box-open method on StageBox takes a WillRemoveBoxData (whose public field
            // `EBoxType BoxType` IS the box being opened) — lgz/lha(WillRemoveBoxData, uc) -> UniTask<bool>.
            // The old "single EBoxType param" hooks were panel-setup spam (lgl sweeps all slots; lgx never
            // fires on a real open) and left kind=Unknown. Resolve by SIGNATURE (1st param type name
            // "WillRemoveBoxData") so method-name churn (lgz/lha→…) doesn't break it; BoxType/m_boxType are
            // non-obfuscated so the value reads survive updates.
            try
            {
                var sb = AccessTools.TypeByName("TaskbarHero.UI.StageBox");
                int hooked = 0;
                if (sb != null)
                {
                    var pre = new HarmonyMethod(typeof(BoxOpenTracker).GetMethod(nameof(OpenBoxPrefix), BindingFlags.NonPublic | BindingFlags.Static));
                    foreach (var m in sb.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        var ps = m.GetParameters();
                        if (ps.Length >= 1 && ps[0].ParameterType.Name == "WillRemoveBoxData")
                        {
                            try { harmony.Patch(m, prefix: pre); hooked++; Plugin.Logger?.LogInfo("[boxopen] hooked StageBox." + m.Name + "(WillRemoveBoxData)"); }
                            catch (Exception e) { Plugin.Logger?.LogWarning("[boxopen] StageBox patch " + m.Name + ": " + e.Message); }
                        }
                    }
                }
                else Plugin.Logger?.LogWarning("[boxopen] StageBox type not found");
                Plugin.Logger?.LogInfo($"[boxopen] StageBox open hooks: {hooked}");
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[boxopen] StageBox hook failed: " + e.Message); }

            // Hook B: LogManager.jtr(LogData) == AddLog. When the added log is a BoxOpenLog, read its
            // grade + name. A regular instance method patches reliably under Il2CppInterop (unlike the
            // BoxOpenLog constructor, whose detour backend fails to init). Resolved by signature
            // (instance, returns void, one LogData param) to survive obfuscation churn.
            try
            {
                var lm = AccessTools.TypeByName("TaskbarHero.Log.LogManager");
                var logData = AccessTools.TypeByName("TaskbarHero.Log.LogData");
                MethodInfo add = null;
                if (lm != null && logData != null)
                {
                    foreach (var m in lm.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        var ps = m.GetParameters();
                        if (m.ReturnType == typeof(void) && ps.Length == 1 && ps[0].ParameterType == logData) { add = m; break; }
                    }
                }
                if (add != null)
                {
                    var post = new HarmonyMethod(typeof(BoxOpenTracker).GetMethod(nameof(AddLogPostfix), BindingFlags.NonPublic | BindingFlags.Static));
                    harmony.Patch(add, postfix: post);
                    Plugin.Logger?.LogInfo("[boxopen] hooked LogManager.AddLog (" + add.Name + ")");
                }
                else Plugin.Logger?.LogWarning("[boxopen] LogManager.AddLog(LogData) not found");
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[boxopen] LogManager hook failed: " + e.Message); }
        }

        // The opening box's source type = WillRemoveBoxData.BoxType (the EBoxType the open was started with).
        // __0 is that struct; __instance is the StageBox (its m_boxType is the same box, used as a backup).
        private static void OpenBoxPrefix(object __instance, object __0)
        {
            int k = (int)BoxKind.Unknown;
            try { var bt = Refl.Get(__0, "BoxType"); if (bt != null) { int v = Convert.ToInt32(bt); if (v >= 0 && v <= 2) k = v; } } catch { }
            if (k == (int)BoxKind.Unknown) { try { int v = Convert.ToInt32(Refl.Get(__instance, "m_boxType")); if (v >= 0 && v <= 2) k = v; } catch { } }
            _openingKind = k;
            if (_openDiag < 10) { _openDiag++; int inst = -1; try { inst = Convert.ToInt32(Refl.Get(__instance, "m_boxType")); } catch { } Plugin.Logger?.LogInfo($"[boxopen] OPEN BoxType={k} m_boxType={inst}"); }
        }
        private static int _openDiag;

        private static int _diagCount;

        // Fires for every log added. We only care about BoxOpenLog entries (one per opened item).
        private static void AddLogPostfix(TaskbarHero.Log.LogData __0)
        {
            try
            {
                if (__0 == null) return;
                var bol = ((Il2CppInterop.Runtime.InteropTypes.Il2CppObjectBase)(object)__0)
                    .TryCast<TaskbarHero.Log.BoxOpenLog>();
                if (bol == null) return;

                // Resolve grade/name by PROPERTY TYPE, not obfuscated name (beoi/beoh → bfds/bfdr → …
                // churn every game update). BoxOpenLog has exactly one EGradeType and one string prop.
                int grade = 0; string name = "";
                bool gotGrade = false, gotName = false;
                foreach (var p in bol.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                    if (!gotGrade && p.PropertyType.Name == "EGradeType")
                    { try { grade = Convert.ToInt32(p.GetValue(bol)); gotGrade = true; } catch { } }
                    else if (!gotName && p.PropertyType == typeof(string))
                    { try { name = p.GetValue(bol) as string ?? ""; gotName = true; } catch { } }
                }
                // Post-2026-06 the name field carries a raw localization key ("ItemName_520017") rather than
                // a localized string; resolve it through the embedded name store (digits = ItemKey).
                if (!string.IsNullOrEmpty(name) && name.IndexOf("ItemName", StringComparison.OrdinalIgnoreCase) >= 0)
                {
                    var dm = System.Text.RegularExpressions.Regex.Match(name, @"\d+");
                    if (dm.Success && int.TryParse(dm.Value, out int ik))
                    { string loc = ItemNameStore.Get(ik); if (!string.IsNullOrEmpty(loc)) name = loc; }
                }
                string stage = ""; try { stage = CharacterReader.CurrentStageId(); } catch { }

                Stats.Add(new BoxOpenEvent { Time = DateTime.Now, Grade = grade, Kind = _openingKind, Name = name, Stage = stage });

                if (_diagCount < 8) { _diagCount++; Plugin.Logger?.LogInfo($"[boxopen] CAPTURED grade={grade} kind={_openingKind} name={name}"); }

                var now = DateTime.Now;
                if ((now - _lastFlush).TotalSeconds >= 2.0) { _lastFlush = now; BoxOpenStore.Save(Stats); }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[boxopen] addlog postfix: " + e.Message); }
        }

        public static void Flush() { try { BoxOpenStore.Save(Stats); } catch { } }

        public static void ClearAll() { Stats.Clear(); BoxOpenStore.Clear(); }
    }
}
