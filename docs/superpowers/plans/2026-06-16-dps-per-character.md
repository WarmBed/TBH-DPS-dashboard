# DPS Per-Character Split — Implementation Plan

> **For agentic workers:** execute task-by-task; steps use `- [ ]`.

**Goal:** F9 DPS panel gains a Simple/Detailed toggle; Detailed adds a per-character damage breakdown (class-coloured name + DPS + total + share), hidden when solo.

**Architecture:** `DpsTracker` tags each windowed event with a character key and rolls up per-character total/share/DPS in its snapshot (pure, unit-tested). The damage hook reads the attacker hero's key via reflection. `OverlayBehaviour` renders the per-character rows in Detailed mode.

**Tech Stack:** C# / BepInEx / Harmony / IMGUI. Spec: `docs/superpowers/specs/2026-06-16-dps-per-character-design.md`.

---

## Task 1: DpsTracker per-character rollup + tests

**Files:** Modify `DpsMeter/DpsTracker.cs`; Test `TrackerTests/Program.cs`.

- [ ] Window tuple gains a char key: `Queue<(float t, float dmg, int ck)>`. `_byChar` = `Dictionary<int,double>`. Clear both in `StartEncounter`.
- [ ] `Record(float amount, bool isCritical, int damageTypeFlag, float now, int charKey = 0)` — enqueue `(now, amount, charKey)`, accumulate `_byChar[charKey]`.
- [ ] Add `CharPart { int CharKey; double Amount; float Share; float Dps; }` and `Snapshot.ByChar` (List, sorted by Amount desc). DPS per char = (sum of windowed dmg for that ck) / same divisor as `LiveDps`.
- [ ] `DistinctChars` count helper (for solo-hide): `_byChar` keys with Amount>0.
- [ ] Tests: two chars (ck=101 deals 300, ck=201 deals 100) → ByChar[0].CharKey==101, Share 0.75; live per-char DPS splits the window; single char → ByChar.Count==1.

## Task 2: HeroProbe attacker→heroKey helper

**Files:** Modify `DpsMeter/HeroProbe.cs`.

- [ ] `public static int ReadHeroKeyOf(object unit)` — `Refl.Get(unit,"cache")`, resolve `EEquipClassType` (reuse the same by-type lookup as `ReadClass`), return `cls>0 ? cls*100+1 : 0`. Cache the PropertyInfo. Returns 0 on failure.

## Task 3: Hook passes attacker key

**Files:** Modify `DpsMeter/Hooks.cs` (`MonsterDealt`).

- [ ] After the `attacker.b_isHero` check: `int ck = HeroProbe.ReadHeroKeyOf(attacker);` then `Plugin.Tracker.Record(amount, crit, type, Time.time, ck);`.

## Task 4: Panel — Simple/Detailed toggle + per-character rows

**Files:** Modify `DpsMeter/OverlayBehaviour.cs`.

- [ ] Add `private bool _detailed;` and `private Rect _modeRect;`.
- [ ] In OnGUI header row, draw a mode button left of Reset: `_modeRect`; label `Loc.G(_detailed ? "mode_detailed" : "mode_simple")`. Hit-test in `HandlePointer` → toggle `_detailed`.
- [ ] After `DrawDistribution`, if `_detailed && !reviewing && snapshot.ByChar.Count > 1`: draw a `by_character` header then one row per char: class-colour dot + `GameLoc("HeroName_"+ck)` (fallback ck) + `Fmt(dps)` + `Fmt(total)` + `share%`.
- [ ] Add the per-character block height into the `height` measurement (only when shown).
- [ ] Class colour: small `heroKey → hex` switch (Knight/Ranger/Sorcerer/Priest…, by `ck/100`).

## Task 5: Localization

**Files:** Modify `DpsMeter/Localization.cs`.

- [ ] Add `by_character` key (繁中「按角色」/ en "By character" / ja / 简中 / es). `mode_simple`/`mode_detailed` already exist.

## Task 6: Build, deploy, verify

- [ ] `dotnet build` + `dotnet run TrackerTests` green.
- [ ] Deploy DLL, restart game, open F9, toggle Detailed in a multi-hero stage → per-character rows show with correct names/DPS/share; solo hides the block; Simple unchanged.
- [ ] Confirm attacker→key path from the in-game log (DebugDamage) if rows are empty; adjust `ReadHeroKeyOf` if needed.

## Self-Review
- Spec coverage: toggle (T4), per-char rows DPS+total+share (T1/T4), solo-hide (T1 DistinctChars + T4 guard), attacker key (T2/T3), graceful degradation (key 0 + Count>1 guard). Simple mode untouched. ✓
