# TBH DPS Meter

**English** · [日本語](README.ja.md) · [繁體中文](README.zh-Hant.md) · [简体中文](README.zh-Hans.md)

In-game overlay for **TaskBarHero** (TBH: Task Bar Hero) — **DPS · damage-taken · stage-compare ·
farming-planner · box-log · opened-box stats · loot-heatmap · Steam-market price-peek**, all toggled
from an **F1 control center**.
Built as a BepInEx 6 IL2CPP plugin. Tested on game **v1.00.09** (Unity 6 / IL2CPP).
UI auto-detects **English / 日本語 / 繁體中文 / 简体中文 / Español**.

> ⬇️ **Players: just download the zip from [Releases](../../releases/latest) — no compiling needed.**

> 🌐 **Website:** https://tbh-dps-meter.zeabur.app

![In-game overlay — DPS, stage-compare, farming-planner, damage-taken and box-log panels](image/overview.png)

<table>
<tr>
<td><img src="image/TaskBarHero_FkEGMBj3Kq.png" alt="DPS panel"></td>
<td><img src="image/TaskBarHero_3TGLxaOOR2.png" alt="Damage-taken panel"></td>
</tr>
<tr>
<td align="center"><b>DPS panel</b> (damage you deal)</td>
<td align="center"><b>Damage-taken panel</b> (damage you receive)</td>
</tr>
</table>

---

## What it shows

**DPS panel:**
- Live DPS (5s sliding window) + Peak + Average
- Total damage + encounter duration + wave count
- Damage-type breakdown (melee / projectile / area / summon / DoT / trap, with combined flags)
- Crit rate + crit-damage share

**Damage-taken panel:**
- Live DTPS (damage per second taken) + Peak + Average
- Total taken + duration + biggest single hit
- **Hits** (times you were hit) + **incoming crit** (monsters' crit rate against you)
- Two distribution bars: element attribute (physical/fire/ice/lightning/chaos) and damage type

## Stage Compare (F11)
Press **F11** to open the **stage-compare panel**: it groups your saved runs by stage (per difficulty)
and shows the current run against a **baseline** (fastest clear by default, or a run you pin), with
deltas for duration, **active vs idle (running) time**, avg/peak/crit, attributes, damage distribution
and **per-wave times** — plus the **full per-character loadout**: equipped **gear** (with item names &
affixes) and **skills** (with levels), per party member, highlighting what changed vs the baseline.
A clear-time trend chart sits on top; click a point to inspect that run. Use ◀ ▶ to browse runs,
≪ ≫ to switch stages, the character tabs to switch hero, and the pin button to set a baseline.

Stage / character / skill / item names follow the **in-game language** — set the game to English /
日本語 / 繁體中文 / 简体中文 / Español and the panel switches live (no restart needed).

<img src="image/TaskBarHero_5BRF6aiQF5.png" alt="Stage-compare panel" width="420">

> *Clear-time trend on top (click a point to inspect that run); below, **baseline ∣ this run** in aligned columns with green/red deltas, plus the character's gear & skills.*

## Farming Planner (F6)
Press **F6** for a **personalized** "what should I farm?" ranking of every stage — **gold/sec** and
**exp/sec** side by side, sortable, with difficulty filter chips. Unlike a static wiki table, it is
calibrated to **your own build**:
- Stages you've cleared use your **real measured** numbers (green, `Real`).
- Stages you haven't use the wiki baseline × **your personal multiplier** (learned from your runs),
  with the clear time fit from your measured runs (`time = perHP·HP + perWave·waves`) — marked `Est.`.
- An **EXP-retention %** column reflects the in-game level penalty (your level vs the stage level),
  so over-/under-leveled stages are ranked honestly — higher stage ≠ always better.
- **Build-aware:** each run is fingerprinted (gear + affixes + skills + level); only runs from your
  **current build** count toward calibration. Change gear and it auto-detects, prompting a re-clear.

A **basis** line shows what the calibration rests on (runs · stages · level · current vs old build).

<img src="image/TaskBarHero_5vnzsSLfTW.png" alt="Farming planner panel" width="420">

> *Every stage ranked by your real gold/sec & exp/sec; green = measured, grey = estimated from your personal multiplier; the **Keep** column is the in-game EXP-retention penalty.*

## Box Log (F5)
Press **F5** for the **treasure-box log**: every box pickup recorded with **time · stage · box name**.
**Stage Boss Boxes** show in **blue** and get their own **boss-box tally**, alongside per-stage counts
and a boxes-per-hour rate. A **sound** plays on every pickup — open the **⚙ settings** to toggle it,
adjust the **volume**, **test** it, or choose your own **.wav** (a built-in two-note chime by default).

<img src="image/box-panel.png" alt="Box log panel" width="420">

> *Each box logged with time, stage and name; Stage Boss Boxes in blue with a separate tally.*

## Opened-box Stats (F4)
Press **F4** for **opened-box stats** — what you actually *pull* when you open boxes. A **grade × box-kind
matrix** (count and %) over your lifetime tally shows the rarity distribution per box type (so you can see
which boxes are worth opening), alongside a paged, time-ordered **open log**.

<img src="image/boxopen-panel.png" alt="Opened-box stats panel" width="420">

## Loot Heatmap (F3)
Press **F3** for the **loot heatmap** — two stacked **day × 24-hour** grids that reveal *when* your loot
happens: the top grid = **box pickups** (mirrors the F5 log, blue), the bottom = **legendary-or-better opens**
(from F4, grade ≥ 3, gold-green), with a summary row on the side. A **clear-time trend** chart at the bottom
follows whichever stage is currently selected in the F11 compare panel, so you can line up *when you grinded*
against *how fast you were clearing*.

<img src="image/heatmap-panel.png" alt="Loot heatmap panel" width="460">

## Price Peek (Steam Market)
Hover an item — in your **backpack**, a reward popup, anywhere it has a tooltip — and a small box shows its
**Steam Community Market** price: current price, **24h change** (波動), **median** sale price, **listings**,
**24h volume**, and a **7-day price curve**. Prices come from a cron-built feed refreshed every ~30 min, so
no per-player Steam scraping. **Right-click** an item to **pin** the box to it — it stays put while you move
the cursor onto the curve, where hovering any point shows that point's **time · price · change vs now**.
Press **F4** to enter position-adjust mode and drag the box where you want it. Toggle it from the F1 control center.

<table>
<tr>
<td><img src="image/背包顯示價格.png" alt="Price box on a backpack item" width="300"></td>
<td><img src="image/價格曲線圖.png" alt="Interactive 7-day price curve" width="260"></td>
</tr>
<tr>
<td align="center"><b>Hover any item</b> for its Steam Market price</td>
<td align="center"><b>Pin + hover the curve</b> to read each day's price</td>
</tr>
</table>

## Control Center (F1)
Press **F1** for the **control center** — one compact hub that lists **every** panel as a toggle button
(lit when shown, dim when hidden), so you can flip DPS, damage-taken, compare, planner, box-log, opened-box
and loot-heatmap on/off from a single place instead of memorizing every hotkey. A tiny live summary
(**DPS · session time · boxes opened**) sits at the top. Shown on launch by default; new panels register
themselves automatically. The bottom rows hold the global settings: **UI scale**, **hide-in-menu**, and two
independent **font-size** steppers — **大字 (big)** for titles, main numbers and list rows, **小字 (small)**
for dim hints, axis labels and buttons — applied live across every panel.

<img src="image/中控台2.png" alt="Control center / hub with UI-scale and big/small font controls" width="300">

## Display scaling
Panels **auto-shrink** so they never run off the screen on small or low-resolution displays. Set your own
size with the **− UI % +** control in the F1 control center, or **Ctrl + PageUp / PageDown** — applied
to every panel and saved as `UI.UIScale`. Two separate **font-size** steppers (**大字** / **小字**) sit next
to it for the text itself, saved as `UI.FontSize` and `UI.FontSizeSmall`.

<img src="image/dps-uiscale.png" alt="DPS panel with the UI scale control" width="300">

## Controls
- **F1** — toggle the control center / hub (configurable: `HubUI.ToggleKey`)
- **F9** — toggle the DPS panel (configurable: `ToggleKey`)
- **F10** — toggle the damage-taken panel (configurable: `TakenUI.ToggleKey`)
- **F11** — toggle the stage-compare panel (configurable: `CompareUI.ToggleKey`)
- **F6** — toggle the farming planner (configurable: `FarmUI.ToggleKey`)
- **F5** — toggle the box log (configurable: `BoxUI.ToggleKey`)
- **F7** — toggle the opened-box stats panel (configurable: `BoxOpenUI.ToggleKey`)
- **F3** — toggle the loot heatmap (configurable: `LootMapUI.ToggleKey`)
- **F4** — enter the price-box position-adjust (drag) mode (configurable: `Price.AdjustKey`)
- **Right-click an item** — pin / unpin the price box to it (then hover the curve to read each day)
- **Mouse drag** — move a panel (positions saved independently; panels never drag off-screen)
- **Reset** button (top-right) zeroes the meter; **◀ ▶** browse past-stage records
- **PageUp / PageDown** — adjust panel opacity; **Ctrl + PageUp / PageDown** — scale all panels

> ⚠️ Clicks **pass through** to the game (the plugin only reads the mouse, it does not capture input), so your character still acts when you click on a panel. This is expected.

---

## Install

### A. First-time install (BepInEx not yet installed)
1. Download `TBH-DpsMeter-vX.Y.Z.zip` from **[Releases](../../releases/latest)**.
2. Steam → right-click **"TBH: Task Bar Hero"** → Manage → Browse local files
   (you should see `TaskBarHero.exe`).
3. Extract **ALL** files from the zip into that folder so `winhttp.dll`,
   `doorstop_config.ini`, `dotnet`, and `BepInEx` sit **next to** `TaskBarHero.exe`
   (choose "Yes" to overwrite if asked).
4. **Launch through Steam** (launching the exe directly will NOT load the plugin).
5. First launch shows a black screen for 1–3 minutes (one-time setup). Then it's normal.

### B. macOS CrossOver check/repair
On CrossOver, Wine may load its built-in `winhttp.dll` instead of the BepInEx proxy, or macOS may
quarantine downloaded files. If no overlay appears and `<game folder>\BepInEx\LogOutput.log` is not
created, run this from the repo root:

```sh
bash scripts/repair-crossover-macos.sh --check
```

If the check reports a missing `winhttp` override or quarantine marker, repair and relaunch through
Steam:

```sh
bash scripts/repair-crossover-macos.sh --repair --launch
```

You usually do not need to specify a bottle; the script scans for the CrossOver bottle containing
`TaskBarHero.exe`. If you have multiple CrossOver bottles, add `--bottle "<your-bottle-name>"`; for a
non-standard Steam library, add `--game-dir "/path/to/TaskbarHero"`. `--check` is read-only.
`--repair` only clears macOS quarantine metadata and sets the scoped Wine override for
`TaskBarHero.exe`; it does not change game values or plugin code.

### C. Updating the plugin (already installed before)
**Yes — updating only needs the single DLL.** BepInEx itself stays untouched.

Overwrite the new `TBH.DpsMeter.dll` into:
```
<game folder>\BepInEx\plugins\TBH.DpsMeter.dll
```
> **Close the game completely first** — while it is running the DLL is locked and cannot be overwritten. Relaunch through Steam afterward.

---

## Config
File: `<game folder>\BepInEx\config\tbh.dpsmeter.cfg` (created after the first run)
```
[General]
Language = Auto   # force a language: zh-Hant / zh-Hans / en / ja / es
```

## Uninstall
Delete from the game folder: `winhttp.dll`, `doorstop_config.ini`, `.doorstop_version`,
`dotnet\`, and `BepInEx\`. This fully restores the vanilla game.

---

## Build from source (developers)
```
dotnet build DpsMeter/DpsMeter.csproj -c Release
# output: DpsMeter\bin\Release\TBH.DpsMeter.dll
copy DpsMeter\bin\Release\TBH.DpsMeter.dll  <game>\BepInEx\plugins\
```
Restart the game **through Steam** (on this Unity 6 build, launching the exe directly
does not inject the BepInEx winhttp proxy).

### How it works
- **Damage dealt:** Harmony postfix on `TaskbarHero.Monster.ebj(DamageInfo, bool)`,
  filtered to player-side hits via `Unit.b_isHero`; reads `OriginDamage` / `IsCritical` / `DamageType`.
- **Damage taken:** Harmony postfix on `TaskbarHero.Hero.ebj(DamageInfo, bool)`, counting any
  hit whose attacker is not a hero; reads `OriginDamage` / `IsCritical` / `DamageType` / `DamageAttribute`.
- **Wave boundaries:** polls `StageManager.stageState` (MONSTERSPAWN → BATTLE → REORGANIZATION);
  resets each MONSTERSPAWN, freezes at REORGANIZATION.
- DPS / DTPS math lives in pure-C# `DpsTracker` / `DamageTakenTracker`, unit-tested in `TrackerTests`.

---

## ⚠️ Disclaimer
This plugin injects via BepInEx, **only reads** damage data, does not modify any game value,
and the game is single-player. Nevertheless, **any third-party mod / injection tool may violate
the game's or platform's (e.g. Steam) Terms of Service** and carries a risk of account ban,
save corruption, or other loss.

**You use this software entirely at your own risk.** The author is **not liable** for any account
ban, suspension, data loss, or other direct or indirect damages arising from use of this plugin.
If you do not accept these terms, do not use it.

## License
[MIT](LICENSE) © 2026 WarmBed
