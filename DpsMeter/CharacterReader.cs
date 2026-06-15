using System;
using TaskbarHero;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>Reads the player's stage id, stats, equipped gear, and active skills from the live
    /// game objects (IL2CPP). Everything is wrapped in try/catch — any read failure degrades to
    /// empty data rather than crashing. Obfuscated member access is finalized via in-game verification.</summary>
    public static class CharacterReader
    {
        private static StageManager _stage;
        private static float _nextFind;

        private static StageManager Stage()
        {
            if (_stage != null) return _stage;
            if (Time.time < _nextFind) return null;
            _nextFind = Time.time + 1f;
            try { _stage = UnityEngine.Object.FindObjectOfType<StageManager>(); } catch { _stage = null; }
            return _stage;
        }

        /// <summary>Stable stage identity (e.g. "3-6"). Empty string if it cannot be read.</summary>
        public static string CurrentStageId()
        {
            try { return StageProbe.ReadStageId(Stage()); }
            catch (Exception e) { Plugin.Logger?.LogWarning("CurrentStageId: " + e.Message); return ""; }
        }

        // ---- per-run reward baseline (captured at stage start; deltas computed at stage end) ----
        private static long _baseGold;
        private static readonly System.Collections.Generic.Dictionary<int, double> _baseHeroExp = new System.Collections.Generic.Dictionary<int, double>();
        // Boxes are granted then auto-opened within a few seconds, so the in-memory count
        // (jej) is back to ~0 by stage-end. We instead poll the live count every frame and
        // accumulate every rising edge across the run -> total boxes obtained per type.
        private static readonly System.Collections.Generic.Dictionary<int, int> _prevBoxLive = new System.Collections.Generic.Dictionary<int, int>();
        private static readonly System.Collections.Generic.Dictionary<int, int> _runBox = new System.Collections.Generic.Dictionary<int, int>();
        private static bool _boxTracking;
        private static bool _rewardDiagDone;
        private static bool _skillDiagDone;

        /// <summary>Snapshot gold / per-hero exp / box counts at the start of a run.</summary>
        public static void CaptureRewardBaseline()
        {
            try
            {
                SaveGearReader.ReadParty();   // populate LastHeroExp so ReadHeroExp can value-match the accessor
                _baseGold = HeroProbe.ReadGold();
                _baseHeroExp.Clear();
                foreach (var h in HeroProbe.FindParty())
                {
                    if (h == null) continue;
                    var s = new CharacterSnapshot();
                    HeroProbe.ReadIdentity(h, s);
                    if (int.TryParse(s.Character, out int hk))
                    {
                        double e = HeroProbe.ReadHeroExp(h);
                        if (!double.IsNaN(e)) _baseHeroExp[hk] = e;   // skip bad reads; no baseline -> 0 delta
                    }
                }
                _prevBoxLive.Clear();
                _runBox.Clear();
                foreach (var bt in HeroProbe.BoxTypes)
                {
                    _prevBoxLive[bt] = HeroProbe.ReadBoxCount(bt);
                    _runBox[bt] = 0;
                }
                _boxTracking = true;
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("CaptureRewardBaseline: " + e.Message); }
        }

        /// <summary>Poll the live box count and accumulate rising edges. Call every frame while a
        /// run is in progress; boxes auto-open within seconds so we must catch them mid-run.</summary>
        public static void TickBoxes()
        {
            if (!_boxTracking) return;
            try
            {
                foreach (var bt in HeroProbe.BoxTypes)
                {
                    int cur = HeroProbe.ReadBoxCount(bt);
                    int prev = _prevBoxLive.TryGetValue(bt, out var p) ? p : cur;
                    if (cur > prev)
                    {
                        _runBox[bt] = (_runBox.TryGetValue(bt, out var rb) ? rb : 0) + (cur - prev);
                        if (Plugin.DebugSnapshot != null && Plugin.DebugSnapshot.Value)
                            Plugin.Logger?.LogInfo($"[reward.box] type={bt} +{cur - prev} (live {prev}->{cur}, run total {_runBox[bt]})");
                    }
                    _prevBoxLive[bt] = cur;
                }
            }
            catch { }
        }

        /// <summary>Compute reward deltas (gold, account exp = sum of party, boxes) into the record.
        /// Per-character exp is already filled on each snapshot by CaptureParty.</summary>
        public static void FillRewards(RunRecord r)
        {
            try
            {
                r.GoldGained = HeroProbe.ReadGold() - _baseGold;
                double totalExp = 0;
                foreach (var snap in r.Party) totalExp += snap.ExpGained;
                r.ExpGained = totalExp;
                TickBoxes();   // catch any final rising edge before we read the accumulator
                foreach (var bt in HeroProbe.BoxTypes)
                {
                    int got = _runBox.TryGetValue(bt, out var rb) ? rb : 0;
                    if (got > 0) r.Boxes.Add(new BoxDrop(HeroProbe.BoxTypeNames[bt], got));
                }
                _boxTracking = false;
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("FillRewards: " + e.Message); }
        }

        /// <summary>Capture every party member's loadout. One CharacterSnapshot per hero.</summary>
        public static System.Collections.Generic.List<CharacterSnapshot> CaptureParty()
        {
            var list = new System.Collections.Generic.List<CharacterSnapshot>();
            try
            {
                var heroes = HeroProbe.FindParty();
                var saveGear = SaveGearReader.ReadParty();   // gear from the decrypted save, keyed by heroKey
                bool diag = Plugin.DebugSnapshot != null && Plugin.DebugSnapshot.Value;
                // one-shot: dump skill-cache members for EVERY party hero (not just slot 0) so the
                // live in-combat hero's caches are captured for the skill-level field hunt.
                if (diag && !_skillDiagDone)
                {
                    bool sawNamed = false;
                    foreach (var h in heroes) if (h != null) sawNamed |= HeroProbe.DiagSkills(h);
                    if (sawNamed) _skillDiagDone = true;   // keep retrying until live (named) skills appear
                }
                foreach (var hero in heroes)
                {
                    if (hero == null) continue;
                    var snap = CaptureOne(hero, diag);
                    if (diag && !_rewardDiagDone) { _rewardDiagDone = true; HeroProbe.DiagRewards(hero); }
                    diag = false;   // only dump diagnostics once
                    // per-character exp gained this run (current - baseline)
                    if (int.TryParse(snap.Character, out int hkx) && _baseHeroExp.TryGetValue(hkx, out var be))
                    {
                        double now = HeroProbe.ReadHeroExp(hero);
                        double d = now - be;
                        // exp is cumulative: a negative or NaN delta can only be a bad read -> 0.
                        snap.ExpGained = (double.IsNaN(d) || d < 0) ? 0 : d;
                    }
                    // attach hero level from the save (for the farming planner's exp-retention model)
                    if (int.TryParse(snap.Character, out int hkl) && SaveGearReader.LastHeroLevels.TryGetValue(hkl, out var lvl))
                        snap.Level = lvl;
                    // attach gear from the save by heroKey (snap.Character is the HeroKey string)
                    if (int.TryParse(snap.Character, out int hk) && saveGear.TryGetValue(hk, out var gl))
                    {
                        foreach (var g in gl)
                        {
                            // bundled wiki table first (stable across game updates), then in-memory tf.ipp
                            string nm = ItemNameStore.Get(g.ItemKey);
                            if (string.IsNullOrEmpty(nm)) nm = HeroProbe.ResolveItemName(g.ItemKey, g.Uid);
                            if (!string.IsNullOrEmpty(nm)) g.Name = nm;
                            // grade / item level are template-only (not on the save instance, and the live
                            // item can't be fetched by save uid) — read them from the bundled wiki meta table.
                            g.Grade = ItemMetaStore.Grade(g.ItemKey);
                            g.Level = ItemMetaStore.Level(g.ItemKey);
                            g.GearType = ItemMetaStore.GearType(g.ItemKey);
                            snap.Equipment.Add(g);
                        }
                    }
                    // skills from the save (stable) — used when the obfuscated in-memory read came up empty
                    if (snap.Skills.Count == 0 && int.TryParse(snap.Character, out int hks)
                        && SaveGearReader.LastHeroSkills.TryGetValue(hks, out var sks))
                    {
                        // equipped non-basic skills (basic attacks end in 001, no display name), sorted by key
                        var named = new System.Collections.Generic.List<int>();
                        foreach (var k in sks)
                            if (k % 1000 != 1 && !string.IsNullOrEmpty(HeroProbe.ResolveSkillName(k))) named.Add(k);
                        named.Sort();
                        // real levels come from the save's talent tree (sorted by talent key), paired
                        // positionally with the sorted skills. The in-memory level is only a last resort —
                        // it returns an effective/garbage value (e.g. 13 for a level-5 skill).
                        SaveGearReader.LastHeroSkillLevels.TryGetValue(hks, out var saveLvls);
                        var inMem = HeroProbe.ReadSkillLevels(hero);
                        for (int i = 0; i < named.Count; i++)
                        {
                            int k = named[i];
                            int lv = (saveLvls != null && i < saveLvls.Count) ? saveLvls[i]
                                   : (inMem.TryGetValue(k, out var l) ? l : 0);
                            snap.Skills.Add(new SkillEntry(HeroProbe.ResolveSkillName(k), lv, k));
                        }
                    }
                    snap.Captured = snap.HasAny;
                    if (snap.Captured) list.Add(snap);
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("CharacterReader.CaptureParty: " + e.Message); }
            return list;
        }

        private static CharacterSnapshot CaptureOne(TaskbarHero.Hero hero, bool diag)
        {
            var snap = new CharacterSnapshot();
            try
            {
                if (diag) HeroProbe.Diagnose(hero);
                HeroProbe.ReadIdentity(hero, snap);
                HeroProbe.ReadStats(hero, snap);
                // gear comes from the decrypted save (see CaptureParty); ReadGear (ACTk) is unused
                HeroProbe.ReadSkills(hero, snap);
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("CharacterReader.CaptureOne: " + e.Message); }
            return snap;
        }
    }
}
