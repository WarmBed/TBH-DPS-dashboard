using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using TaskbarHero;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>
    /// Damage hooks, resolved by SIGNATURE rather than obfuscated name (the game re-randomises
    /// private method names every update: ebj→edg/gpw, gnr→…). The "take damage" entry on the
    /// Unit hierarchy is a virtual <c>Void (DamageInfo, bool)</c>; Monster and Hero both inherit it.
    ///
    /// Monster exposes TWO such virtuals after the 2026-06 reshuffle (edg, gpw). We patch BOTH on
    /// Monster as a probe (one active → Tracker, one log-only) so a single fight pins down which one
    /// actually carries hero-dealt damage. We do NOT patch the same virtual on both Monster AND Hero —
    /// that produced a broken MonoMod trampoline → infinite recursion → stack overflow (0xc00000fd).
    /// Hero-taken is wired in a follow-up build onto the OTHER virtual (a different method → safe).
    /// </summary>
    internal static class DamageHooks
    {
        public static void TryHook(Harmony harmony)
        {
            try
            {
                var mons = AccessTools.TypeByName("TaskbarHero.Monster");
                var hero = AccessTools.TypeByName("TaskbarHero.Hero");
                if (mons == null) { Plugin.Logger?.LogWarning("DamageHooks: Monster type not found"); return; }

                // Unit exposes TWO virtual Void(DamageInfo,bool) take-damage entries (edg/gpw post-2026-06);
                // both fire identically per hit. Monster offensive DPS uses the first; Hero-taken uses the
                // OTHER one — patching the SAME virtual on both Monster and Hero stack-overflows (0xc00000fd).
                var mCands = DamageVirtuals(mons);
                if (mCands.Count == 0) { Plugin.Logger?.LogWarning("DamageHooks: no Monster (DamageInfo,bool) method found"); return; }

                string primary = mCands[0].Name;   // offensive take-damage (verified live: hero hits land here)
                var dealtPost = new HarmonyMethod(typeof(DamageHooks).GetMethod(nameof(MonsterDealt), BindingFlags.NonPublic | BindingFlags.Static));
                try { harmony.Patch(mCands[0], postfix: dealtPost); Plugin.Logger?.LogInfo("Patched: Monster." + primary + " (DamageInfo,bool) [DPS]"); }
                catch (Exception e) { Plugin.Logger?.LogWarning("DamageHooks: patch Monster." + primary + " failed: " + e.Message); }

                // Hero-taken on the distinct virtual (name != primary) so it never collides with Monster's.
                if (hero != null)
                {
                    MethodInfo hTaken = null;
                    foreach (var m in DamageVirtuals(hero)) if (m.Name != primary) { hTaken = m; break; }
                    if (hTaken != null)
                    {
                        var takenPost = new HarmonyMethod(typeof(DamageHooks).GetMethod(nameof(HeroTaken), BindingFlags.NonPublic | BindingFlags.Static));
                        try { harmony.Patch(hTaken, postfix: takenPost); Plugin.Logger?.LogInfo("Patched: Hero." + hTaken.Name + " (DamageInfo,bool) [taken]"); }
                        catch (Exception e) { Plugin.Logger?.LogWarning("DamageHooks: patch Hero." + hTaken.Name + " failed: " + e.Message); }
                    }
                    else Plugin.Logger?.LogWarning("DamageHooks: no distinct Hero take-damage virtual; taken hook skipped");
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("DamageHooks.TryHook: " + e.Message); }
        }

        /// <summary>All instance methods shaped <c>Void (DamageInfo, bool)</c> on a type, metadata order.</summary>
        private static List<MethodInfo> DamageVirtuals(Type t)
        {
            var list = new List<MethodInfo>();
            foreach (var m in t.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.DeclaredOnly))
            {
                var ps = m.GetParameters();
                if (m.ReturnType == typeof(void) && ps.Length == 2
                    && ps[0].ParameterType.Name == "DamageInfo" && ps[1].ParameterType == typeof(bool))
                    list.Add(m);
            }
            return list;
        }

        // Damage dealt to a monster — count hits whose attacker is on the hero/player side.
        private static void MonsterDealt(DamageInfo __0)
        {
            try
            {
                var a = __0;
                if (a == null) return;
                var attacker = a.Attacker;
                if (attacker == null || !attacker.b_isHero) return;

                float amount = a.OriginDamage;
                if (amount <= 0f) return;
                bool crit = a.IsCritical;
                int type = (int)a.DamageType;
                int ck = HeroProbe.ReadHeroKeyOf(attacker);   // which hero dealt it (0 = unknown)

                Plugin.Tracker.Record(amount, crit, type, Time.time, ck);
                if (Plugin.DebugDamage != null && Plugin.DebugDamage.Value)
                    Plugin.LogDamageSample(amount, crit, type);
            }
            catch { /* never let a hook crash the game */ }
        }

        // Damage taken by a hero — count unless the attacker is itself a hero (skip hero/self damage).
        private static void HeroTaken(DamageInfo __0)
        {
            try
            {
                var a = __0;
                if (a == null) return;
                var attacker = a.Attacker;
                if (attacker != null && attacker.b_isHero) return;

                float amount = a.OriginDamage;
                if (amount <= 0f) return;
                bool crit = a.IsCritical;
                int type = (int)a.DamageType;
                int attr = (int)a.DamageAttribute;

                if (Plugin.DebugDamage != null && Plugin.DebugDamage.Value)
                    Plugin.Logger?.LogInfo($"[taken] amount={amount:0.#} crit={crit} type={type} attr={attr}");

                Plugin.TakenTracker.Record(amount, crit, type, attr, Time.time);
            }
            catch { /* never let a hook crash the game */ }
        }
    }

    /// <summary>
    /// Drives encounter boundaries off the stage state machine.
    /// EStageState: NONE=0, MONSTERSPAWN=1, BATTLE=2, REORGANIZATION=3.
    /// Each wave cycles MONSTERSPAWN -> BATTLE -> REORGANIZATION, so we reset the
    /// meter when a wave starts spawning and freeze it when reorganization begins.
    /// </summary>
    [HarmonyPatch(typeof(StageManager), "set_stageState")]
    internal static class StageState_Patch
    {
        // il2cpp property setter param is unnamed; bind positionally with __0.
        static void Postfix(EStageState __0)
        {
            // NOTE: this setter is not actually called by the game (the field is assigned
            // directly), so OverlayBehaviour.PollStageState drives the boundaries. With a
            // single-character party NONE flickers BETWEEN WAVES (not just at round end), so
            // resetting here would wrongly zero an accumulating run — deliberately a no-op.
        }
    }
}
