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
            // Hook A: box SOURCE kind (一般/王箱/首領). The per-box open on StageBox is an async UniTask
            // kickoff (OpenBoxAsync/ExchangeOpenBoxAsync, obf. lkp/lkq) whose Harmony prefix NEVER runs
            // under Il2CppInterop — and the mass "auto-open" path bypasses StageBox entirely — so the old
            // StageBox(WillRemoveBoxData) hook left every drop kind=Unknown. Instead hook the DATA-LAYER
            // box manager: static, non-async Void methods that EVERY open (manual + auto) funnels through
            // to exchange box→inventory. They carry the box type as a literal `EBoxType` first arg
            // (irs/cqn/dxb/kml(EBoxType, Action<BoxData>, WillRemoveBoxData, ua)). Reach the manager type by
            // walking up from the readable `WillRemoveBoxData` struct (StageBox's open param) to its outer
            // container, then hook every nested static Void method shaped (EBoxType, …, WillRemoveBoxData, …).
            // Everything anchors on readable type/enum names (StageBox, WillRemoveBoxData, EBoxType) so
            // private-member name churn can't break it.
            try
            {
                var sb = AccessTools.TypeByName("TaskbarHero.UI.StageBox");
                Type wrbd = null;
                if (sb != null)
                {
                    foreach (var m in sb.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
                    {
                        var ps = m.GetParameters();
                        if (ps.Length >= 1 && ps[0].ParameterType.Name == "WillRemoveBoxData") { wrbd = ps[0].ParameterType; break; }
                    }
                }
                int hooked = 0;
                if (wrbd != null)
                {
                    // WillRemoveBoxData is nested as <outer>+<boxData>+WillRemoveBoxData; the box manager is
                    // a sibling nested under the same <outer>. Scan <outer>'s nested types for the open methods.
                    var outer = wrbd.DeclaringType?.DeclaringType;
                    var pre = new HarmonyMethod(typeof(BoxOpenTracker).GetMethod(nameof(OpenBoxByEnumPrefix), BindingFlags.NonPublic | BindingFlags.Static));
                    if (outer != null)
                    {
                        foreach (var t in outer.GetNestedTypes(BindingFlags.Public | BindingFlags.NonPublic))
                        {
                            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static))
                            {
                                if (m.ReturnType != typeof(void)) continue;
                                var ps = m.GetParameters();
                                if (ps.Length < 2 || ps[0].ParameterType.Name != "EBoxType") continue;
                                bool hasData = false;
                                foreach (var p in ps) if (p.ParameterType.Name == "WillRemoveBoxData") { hasData = true; break; }
                                if (!hasData) continue;
                                try { harmony.Patch(m, prefix: pre); hooked++; Plugin.Logger?.LogInfo("[boxopen] hooked " + t.Name + "." + m.Name + "(EBoxType,…,WillRemoveBoxData)"); }
                                catch (Exception e) { Plugin.Logger?.LogWarning("[boxopen] patch " + t.Name + "." + m.Name + ": " + e.Message); }
                            }
                        }
                    }
                    else Plugin.Logger?.LogWarning("[boxopen] box-manager outer type not reached");
                }
                else Plugin.Logger?.LogWarning("[boxopen] WillRemoveBoxData type not reached from StageBox");
                Plugin.Logger?.LogInfo($"[boxopen] box-manager open hooks: {hooked}");
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[boxopen] box-manager hook failed: " + e.Message); }

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

        // The box manager's open methods carry the source type as their first arg (EBoxType: NORMAL=0,
        // BOSS=1, ACTBOSS=2 — same ordering as BoxKind). __0 is that enum; convert to int, clamp to 0..2.
        private static void OpenBoxByEnumPrefix(object __0)
        {
            int k = EBoxTypeToInt(__0);
            if (k >= 0 && k <= 2) _openingKind = k;
            if (_openDiag < 12) { _openDiag++; Plugin.Logger?.LogInfo($"[boxopen] OPEN EBoxType={k}"); }
        }
        private static int _openDiag;

        // EBoxType arrives as an Il2Cpp enum; Convert.ToInt32 usually works, else fall back to its name.
        private static int EBoxTypeToInt(object o)
        {
            if (o == null) return -1;
            try { return Convert.ToInt32(o); } catch { }
            try
            {
                switch ((o.ToString() ?? "").Trim().ToUpperInvariant())
                {
                    case "NORMAL": return 0;
                    case "BOSS": return 1;
                    case "ACTBOSS": return 2;
                }
            }
            catch { }
            return -1;
        }

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
