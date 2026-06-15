# GearScore 裝備評分 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Add a WoW-GearScore-style "裝備評分" panel that scores each party character's full loadout (rarity + item level + affixes + the three socket types) into one number, with a live per-character panel that toggles between simple and detailed (per-effect point contributions) views and shows each item's icon.

**Architecture:** Pure scoring logic in a new `GearScore.cs` (no Unity deps, unit-tested in `TrackerTests`), mirroring `StageCompare.cs`. `GearItem` gains `Grade`/`Level`/`Sockets`/`Icon`. `HeroProbe.ReadGear` is extended to capture those (paths confirmed by an in-game `DiagGearScore` dump, with graceful degradation). A new `GearScoreOverlayBehaviour` IMGUI panel registers itself with `PanelRegistry` so it appears in the F1 hub, modelled on `CompareOverlayBehaviour`.

**Tech Stack:** C# / BepInEx / Il2CppInterop reflection / Unity IMGUI (OnGUI). Spec: `docs/superpowers/specs/2026-06-16-gear-score-design.md`.

---

## File Structure

- `DpsMeter/RunModels.cs` — add fields to `GearItem` + a `GearSocket`/reuse `Affix`.
- `DpsMeter/GearScore.cs` (new) — pure scoring: `ScoreItem`, `ScoreCharacter`, coefficient tables, breakdown structs.
- `DpsMeter/HeroProbe.cs` — extend `ReadGear` (grade/level/sockets/icon) + add `DiagGearScore`.
- `DpsMeter/Plugin.cs` — config entries + `AddComponent<GearScoreOverlayBehaviour>()` + toggle key.
- `DpsMeter/GearScoreOverlayBehaviour.cs` (new) — the panel.
- `DpsMeter/Localization.cs` — new UI string keys.
- `TrackerTests/Program.cs` — unit tests for `GearScore`.

---

## Task 1: GearItem data fields

**Files:**
- Modify: `DpsMeter/RunModels.cs`

- [ ] **Step 1: Add fields to `GearItem`**

In `DpsMeter/RunModels.cs`, replace the `GearItem` class body with:

```csharp
    /// <summary>One equipped item with its affixes/mods.</summary>
    public class GearItem
    {
        public string Slot = "";
        public string Name = "";
        /// <summary>Item template key (transient; used to resolve the display name at capture). Not serialized.</summary>
        public int ItemKey;
        /// <summary>Item instance uid from the save (transient; used to fetch the live item for name lookup).</summary>
        public ulong Uid;
        /// <summary>EGradeType name (e.g. "LEGENDARY"). "" = unknown -> scored as the lowest grade.</summary>
        public string Grade = "";
        /// <summary>Required/item level (the "需要等級" number). 0 = unknown -> contributes no level points.</summary>
        public int Level;
        public readonly List<Affix> Affixes = new List<Affix>();
        /// <summary>Contents of the 裝飾/雕刻/銘文 sockets, each as a stat+value (empty sockets omitted).</summary>
        public readonly List<Affix> Sockets = new List<Affix>();
        /// <summary>Item icon texture (transient, not serialized). null when unavailable -> panel shows a glyph.</summary>
        [System.NonSerialized] public UnityEngine.Texture Icon;
    }
```

- [ ] **Step 2: Build to confirm it compiles**

Run: `dotnet build DpsMeter/DpsMeter.csproj -c Release`
Expected: Build succeeded (Icon uses UnityEngine.Texture; RunModels already lives in the Unity-referencing assembly).

- [ ] **Step 3: Commit**

```bash
git add DpsMeter/RunModels.cs
git commit -m "feat(gear): add Grade/Level/Sockets/Icon to GearItem"
```

---

## Task 2: GearScore pure logic + tests

**Files:**
- Create: `DpsMeter/GearScore.cs`
- Test: `TrackerTests/Program.cs`

- [ ] **Step 1: Write `GearScore.cs`**

```csharp
using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>Pure (no Unity / no BepInEx) GearScore logic, unit-tested in TrackerTests.
    /// itemScore = gradeBase[grade] + level*LevelWeight + Σ affix(value*statWeight) + Σ socket(value*statWeight).
    /// Coefficient tables are tunable constants; calibrate against real in-game values (see plan Task 7).</summary>
    public static class GearScore
    {
        // Base points per rarity. Keys are upper-cased EGradeType names; unknown -> Default.
        private static readonly Dictionary<string, double> GradeBase = new Dictionary<string, double>
        {
            { "COMMON", 0 }, { "NORMAL", 0 },
            { "UNCOMMON", 50 }, { "MAGIC", 50 },
            { "RARE", 120 },
            { "EPIC", 250 },
            { "LEGENDARY", 450 },
            { "MYTHIC", 700 },
        };
        private const double GradeDefault = 0;
        private const double LevelWeight = 2.0;

        // Per-stat normalisation so attack (~hundreds) and crit% (~0.05) contribute comparably.
        // Keys are the affix/stat names emitted by HeroProbe (lower-cased on lookup). Default = 1.
        private static readonly Dictionary<string, double> StatWeight = new Dictionary<string, double>
        {
            { "attack", 1.0 },
            { "hp", 0.1 },
            { "armor", 1.0 },
            { "critrate", 1000.0 },
            { "critdmg", 200.0 },
            { "aspd", 300.0 },
            { "mspd", 50.0 },
        };
        private const double StatWeightDefault = 1.0;

        public static double WeightOf(string stat)
        {
            if (string.IsNullOrEmpty(stat)) return StatWeightDefault;
            return StatWeight.TryGetValue(stat.ToLowerInvariant(), out var w) ? w : StatWeightDefault;
        }

        public static double GradePoints(string grade)
        {
            if (string.IsNullOrEmpty(grade)) return GradeDefault;
            return GradeBase.TryGetValue(grade.ToUpperInvariant(), out var b) ? b : GradeDefault;
        }

        /// <summary>One scored line for the detailed view: a label and its point contribution.</summary>
        public struct Part { public string Label; public double Points; public Part(string l, double p) { Label = l; Points = p; } }

        public class ItemScore
        {
            public double Total;
            public readonly List<Part> Parts = new List<Part>();
        }

        public static ItemScore ScoreItem(GearItem g)
        {
            var s = new ItemScore();
            if (g == null) return s;
            double gb = GradePoints(g.Grade);
            s.Parts.Add(new Part("grade", gb)); s.Total += gb;
            double lv = g.Level * LevelWeight;
            if (lv != 0) { s.Parts.Add(new Part("level", lv)); s.Total += lv; }
            foreach (var a in g.Affixes) { double p = a.Value * WeightOf(a.Name); s.Parts.Add(new Part(a.Name, p)); s.Total += p; }
            foreach (var a in g.Sockets) { double p = a.Value * WeightOf(a.Name); s.Parts.Add(new Part("socket:" + a.Name, p)); s.Total += p; }
            return s;
        }

        public static double ScoreCharacter(CharacterSnapshot snap)
        {
            if (snap == null) return 0;
            double total = 0;
            foreach (var g in snap.Equipment) total += ScoreItem(g).Total;
            return total;
        }
    }
}
```

- [ ] **Step 2: Add tests to `TrackerTests/Program.cs`**

Insert before the `if (_fail == 0)` / `return _fail;` tail of `Main` (use the existing `Check`/`Near` helpers):

```csharp
        // --- GearScore ---
        var giLegendary = new GearItem { Grade = "LEGENDARY", Level = 80 };
        giLegendary.Affixes.Add(new Affix("attack", 100));   // 100*1
        giLegendary.Sockets.Add(new Affix("critrate", 0.03)); // 0.03*1000 = 30
        var isLeg = GearScore.ScoreItem(giLegendary);
        // 450 (grade) + 160 (80*2) + 100 (attack) + 30 (socket crit) = 740
        Check("gearscore item = 740", Near(isLeg.Total, 740), isLeg.Total);
        Check("gearscore unknown grade = 0 base", Near(GearScore.GradePoints(""), 0), GearScore.GradePoints(""));
        Check("gearscore unknown stat weight = 1", Near(GearScore.WeightOf("nonsense"), 1), GearScore.WeightOf("nonsense"));

        var snap = new CharacterSnapshot();
        snap.Equipment.Add(giLegendary);
        var gi2 = new GearItem { Grade = "RARE", Level = 0 };   // 120 + 0
        snap.Equipment.Add(gi2);
        Check("gearscore character = 860", Near(GearScore.ScoreCharacter(snap), 860), GearScore.ScoreCharacter(snap));
        Check("gearscore null item = 0", Near(GearScore.ScoreItem(null).Total, 0), GearScore.ScoreItem(null).Total);
```

- [ ] **Step 3: Run tests, expect FAIL (GearScore undefined)**

Run: `dotnet run --project TrackerTests/TrackerTests.csproj`
Expected: compile error or FAIL — `GearScore` not yet referenced until Step 1 file is included.

- [ ] **Step 4: Confirm `GearScore.cs` is compiled by the test project**

`TrackerTests` references the DpsMeter sources. Confirm `GearScore.cs` is picked up (check `TrackerTests.csproj` for how it includes DpsMeter `.cs` files — it Compile-links them). If `GearScore.cs` is not auto-included, add `<Compile Include="..\DpsMeter\GearScore.cs" />` alongside the existing `StageCompare.cs` include.

Run: `dotnet run --project TrackerTests/TrackerTests.csproj`
Expected: all GearScore checks PASS.

- [ ] **Step 5: Commit**

```bash
git add DpsMeter/GearScore.cs TrackerTests/Program.cs TrackerTests/TrackerTests.csproj
git commit -m "feat(gear): GearScore pure scoring + unit tests"
```

---

## Task 3: Capture grade/level/sockets/icon in ReadGear + diagnostic

**Files:**
- Modify: `DpsMeter/HeroProbe.cs`

- [ ] **Step 1: Capture grade + best-effort level/icon in `ReadGear`**

In `HeroProbe.ReadGear`, inside the per-item block where `g` is built (after `g.Slot = ...`), add grade/level/icon reads using the same live item object `item`:

```csharp
                        // rarity: same EGradeType the price overlay reads (brkk on the live item).
                        g.Grade = Refl.Str(Refl.Get(item, "brkk"));
                        // item level (需要等級): resolved by DiagGearScore; try known candidates by name,
                        // else the int member on the info struct. 0 stays = unknown (no level points).
                        g.Level = ReadItemLevel(item, info);
                        // icon sprite/texture for the panel (best-effort; null -> panel shows a glyph).
                        g.Icon = ReadItemIcon(item, info);
                        ReadSockets(item, g);
```

- [ ] **Step 2: Add the helper readers + diagnostic to `HeroProbe`**

Add these methods to `HeroProbe` (the obfuscated member names are placeholders confirmed/replaced by `DiagGearScore` at Task 7; until then they fail safe to 0/null):

```csharp
        // Item-level member candidates (replace/confirm via DiagGearScore). Reads the first that yields
        // a plausible level (1..200). info is the ItemInfoData (brke); the level may live on it or the item.
        private static readonly string[] LevelMembers = { "RequireLevel", "RequiredLevel", "Level", "ItemLevel" };
        private static int ReadItemLevel(object item, object info)
        {
            foreach (var src in new[] { info, item })
                foreach (var m in LevelMembers)
                {
                    int v = Refl.ToI(Refl.Get(src, m) ?? Refl.Call(src, m));
                    if (v >= 1 && v <= 200) return v;
                }
            return 0;
        }

        // Icon-sprite member candidates. Returns a Texture (Sprite.texture if a Sprite is found).
        private static readonly string[] IconMembers = { "Icon", "icon", "Sprite", "sprite", "IconSprite" };
        private static UnityEngine.Texture ReadItemIcon(object item, object info)
        {
            foreach (var src in new[] { item, info })
                foreach (var m in IconMembers)
                {
                    var v = Refl.Get(src, m) ?? Refl.Call(src, m);
                    if (v == null) continue;
                    if (v is UnityEngine.Texture tex) return tex;
                    if (v is UnityEngine.Sprite sp) return sp.texture;
                    var t = Refl.Get(v, "texture") as UnityEngine.Texture;   // Sprite-like wrapper
                    if (t != null) return t;
                }
            return null;
        }

        // Socket containers (裝飾/雕刻/銘文). Each yields gem entries with a stat+value, read like affixes.
        // Container + entry member names confirmed via DiagGearScore; fail safe to "no sockets".
        private static readonly string[] SocketContainers = { "DecoSlots", "CarveSlots", "InscribeSlots" };
        private static void ReadSockets(object item, GearItem g)
        {
            foreach (var c in SocketContainers)
            {
                var col = Refl.Get(item, c) ?? Refl.Call(item, c);
                if (col == null) continue;
                foreach (var entry in Refl.Enumerate(col))
                {
                    if (entry == null) continue;
                    string st = Refl.Str(Refl.Call(entry, "iox") ?? Refl.Get(entry, "iox"));
                    if (string.IsNullOrEmpty(st) || st == "NONE" || st == "None") continue;
                    double val = Refl.ToD(Refl.Call(entry, "ioy") ?? Refl.Get(entry, "ioy"));
                    g.Sockets.Add(new Affix(st, val));
                }
            }
        }

        /// <summary>One-shot dump to pin down item-level, socket-container, and icon member paths.
        /// Gated by DebugSnapshot; call once from ReadGear's first item when the flag is on.</summary>
        public static void DiagGearScore(object item, object info)
        {
            try
            {
                Log("[gearscore] item members:");
                DumpIntMembers(info, "   info.");
                DumpIntMembers(item, "   item.");
                // dump non-primitive members on item so we can spot socket collections + icon sprites
                foreach (var p in item.GetType().GetProperties(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
                {
                    if (!p.CanRead || p.GetIndexParameters().Length != 0) continue;
                    object v; try { v = p.GetValue(item); } catch { continue; }
                    if (v == null) continue;
                    var tn = v.GetType().Name;
                    if (tn.Contains("List") || tn.Contains("Collection") || tn.Contains("Sprite") || tn.Contains("Texture") || tn.Contains("[]"))
                        Log($"   item.{p.Name} -> {v.GetType().FullName}");
                }
            }
            catch (System.Exception e) { Log("[gearscore] diag ex: " + e.Message); }
        }
```

- [ ] **Step 3: Wire the diagnostic into `ReadGear` (debug-gated, first item only)**

In `ReadGear`, where `dbg` is already computed, add after `info` is read for the first diagnostic item:

```csharp
                        if (dbg && gdiag <= 1) DiagGearScore(item, info);
```

- [ ] **Step 4: Build**

Run: `dotnet build DpsMeter/DpsMeter.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 5: Commit**

```bash
git add DpsMeter/HeroProbe.cs
git commit -m "feat(gear): read grade/level/sockets/icon in ReadGear + DiagGearScore"
```

---

## Task 4: Config + plugin wiring

**Files:**
- Modify: `DpsMeter/Plugin.cs`

- [ ] **Step 1: Add config fields**

Near the other `ConfigEntry` declarations (around line 55) add:

```csharp
        public static ConfigEntry<float> GearScorePosX, GearScorePosY, GearScorePanelWidth;
        public static ConfigEntry<bool> GearScoreStartVisible;
        public static KeyCode GearScoreToggleKey = KeyCode.F7;
        private static ConfigEntry<string> _gearScoreToggleKeyName;
```

- [ ] **Step 2: Bind config (mirror the CompareUI block, ~line 159)**

```csharp
            GearScorePosX = Config.Bind("GearScoreUI", "PosX", -1f, "Gear-score overlay X (auto-saved when dragged). -1 = auto.");
            GearScorePosY = Config.Bind("GearScoreUI", "PosY", -1f, "Gear-score overlay Y (auto-saved when dragged). -1 = auto.");
            GearScorePanelWidth = Config.Bind("GearScoreUI", "PanelWidth", 360f, "Gear-score overlay panel width in pixels.");
            GearScoreStartVisible = Config.Bind("GearScoreUI", "StartVisible", false, "Show the gear-score overlay on launch.");
            _gearScoreToggleKeyName = Config.Bind("GearScoreUI", "ToggleKey", "F7", "Key to show/hide the gear-score overlay (UnityEngine.KeyCode name).");
```

- [ ] **Step 3: Parse the toggle key (next to the F11 parse, ~line 203)**

```csharp
            try { GearScoreToggleKey = (KeyCode)System.Enum.Parse(typeof(KeyCode), _gearScoreToggleKeyName.Value, true); }
            catch { GearScoreToggleKey = KeyCode.F7; }
```

- [ ] **Step 4: Add the component (after CompareOverlayBehaviour, ~line 254)**

```csharp
                go.AddComponent<GearScoreOverlayBehaviour>();
```

- [ ] **Step 5: Build**

Run: `dotnet build DpsMeter/DpsMeter.csproj -c Release`
Expected: fails only on the missing `GearScoreOverlayBehaviour` type (added in Task 5). Compile errors limited to that symbol confirm wiring is otherwise correct.

- [ ] **Step 6: Commit (after Task 5 compiles) — defer commit to Task 5 Step N.**

---

## Task 5: GearScoreOverlayBehaviour panel

**Files:**
- Create: `DpsMeter/GearScoreOverlayBehaviour.cs`

- [ ] **Step 1: Write the panel**

Model the boilerplate (drag, resize, styles, EnsureAssets, scale, PanelRegistry.Register) on `CompareOverlayBehaviour`. The panel-specific content:

```csharp
using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (F7): live GearScore for the whole party. Each character shows a total
    /// score; a 詳細/簡易 toggle expands per-item rows (icon + name + rarity + level + score), and in
    /// detailed mode each effect line shows its point contribution. Read-only over the live party.</summary>
    public class GearScoreOverlayBehaviour : MonoBehaviour
    {
        public GearScoreOverlayBehaviour(System.IntPtr ptr) : base(ptr) { }

        private const float Pad = 10f;
        private Rect _rect = new Rect(60, 60, 360, 0);
        private bool _visible;
        private bool _detailed;            // 詳細/簡易 toggle
        private float _opacity = 0.9f;
        private bool _placed;
        private float _wantX, _wantY, _scale = 1f;
        private Vector2 _dragOffset; private bool _dragging;
        private Texture2D _white, _bgTex; private float _bgAlphaBaked = -1f;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box; private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;
        private Rect _closeRect, _modeRect;
        private readonly PanelResize _resize = new PanelResize();
        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        // refreshed ~2/sec from the live party so the panel reflects gear changes without re-capture
        private readonly List<CharacterSnapshot> _party = new List<CharacterSnapshot>();
        private float _nextRefresh;

        void Awake()
        {
            _opacity = Mathf.Clamp01(Plugin.Opacity.Value + 0.15f);
            _rect.width = Mathf.Max(320, Plugin.GearScorePanelWidth.Value);
            _visible = Plugin.GearScoreStartVisible.Value;
            PanelRegistry.Register("gearscore", 3, "G", () => Loc.G("gearscore_title"), Plugin.GearScoreToggleKey,
                () => _visible, v => _visible = v);
        }

        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.GearScorePosX.Value, py = Plugin.GearScorePosY.Value;
            if (px < 0 || py < 0) { _rect.x = 80f; _rect.y = 80f; } else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y; _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.Poll();
                InputCompat.SetPanel(3, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.GearScoreToggleKey)) _visible = !_visible;
                if (_visible && Time.realtimeSinceStartup >= _nextRefresh) { _nextRefresh = Time.realtimeSinceStartup + 0.5f; Refresh(); }
                if (_visible) HandlePointer(); else if (_dragging) _dragging = false;
            }
            catch { }
        }

        private void Refresh()
        {
            _party.Clear();
            try
            {
                foreach (var h in HeroProbe.FindParty())
                {
                    if (h == null) continue;
                    var snap = new CharacterSnapshot();
                    HeroProbe.ReadIdentity(h, snap);
                    HeroProbe.ReadGear(h, snap);
                    _party.Add(snap);
                }
            }
            catch { }
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(3); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(3, m, ref rw, ref dh, 300f, Mathf.Max(300f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 360f; Plugin.GearScorePanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.GearScorePanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                if (_modeRect.Contains(m)) { _detailed = !_detailed; return; }
                if (_rect.Contains(m) && InputCompat.ClaimDrag(3)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(3)) { _dragging = false; return; }
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased())
                {
                    _dragging = false; InputCompat.ReleaseDrag(3);
                    _wantX = _rect.x; _wantY = _rect.y;
                    Plugin.GearScorePosX.Value = _rect.x; Plugin.GearScorePosY.Value = _rect.y;
                }
            }
        }

        private void EnsureAssets()
        {
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_bgTex == null) { _bgTex = new Texture2D(1, 1); _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f)); _bgTex.Apply(); }
            int fs = Plugin.FontSize.Value, fsm = Plugin.FontSizeSmall.Value;
            if (_stylesReady && _builtFs == fs && _builtFsm == fsm) return;
            _builtFs = fs; _builtFsm = fsm;
            _title = new GUIStyle { fontSize = fs, fontStyle = FontStyle.Bold, richText = true };
            _title.normal.textColor = new Color(1f, 0.86f, 0.35f);
            _label = new GUIStyle { fontSize = fs, richText = true }; _label.normal.textColor = new Color(0.93f, 0.93f, 0.93f);
            _dim = new GUIStyle { fontSize = fsm, richText = true }; _dim.normal.textColor = new Color(0.78f, 0.84f, 0.95f);
            _tiny = new GUIStyle { fontSize = Mathf.Max(9, fsm - 2), richText = true, wordWrap = true }; _tiny.normal.textColor = new Color(0.7f, 0.75f, 0.85f);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = fsm, fontStyle = FontStyle.Bold, richText = true };
            _box = new GUIStyle(); _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _label, _dim, _tiny, _btn);
            _stylesReady = true;
        }

        void OnGUI()
        {
            if (!_visible || GameUiState.MenuOpen()) return;
            GUI.depth = -10;
            var prevM = GUI.matrix;
            try
            {
                EnsureAssets();
                if (!_placed) PlaceDefault();
                int fs = Plugin.FontSize.Value; float lh = fs + 7;
                float x = _rect.x, w = _rect.width, ix = x + Pad, iw = w - Pad * 2;

                // measure: header + per character (1 header row + N item rows, item rows taller in detailed)
                float rowH = lh * 1.5f;
                float h = Pad + lh;   // header
                foreach (var snap in _party)
                {
                    h += lh;          // character name + total
                    foreach (var g in snap.Equipment) { h += rowH; if (_detailed) h += lh * Mathf.Max(1, GearScore.ScoreItem(g).Parts.Count) * 0.9f; }
                }
                if (_party.Count == 0) h += lh;
                h += Pad;
                _rect.height = h;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                if (!_dragging)
                {
                    _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale));
                    _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale));
                }
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                float cy = _rect.y + Pad;
                GUI.Label(new Rect(ix, cy, iw - 130, lh), Loc.G("gearscore_title"), _title);
                _modeRect = new Rect(x + w - 116, cy - 1, 86, lh);
                GUI.Button(_modeRect, Loc.G(_detailed ? "mode_detailed" : "mode_simple"), _btn);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh);
                GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                if (_party.Count == 0) { GUI.Label(new Rect(ix, cy, iw, lh), Loc.G("gearscore_empty"), _dim); _resize.DrawGrip(_white, _rect); return; }

                foreach (var snap in _party)
                {
                    double total = GearScore.ScoreCharacter(snap);
                    string nm = string.IsNullOrEmpty(snap.CharacterName) ? snap.Character : snap.CharacterName;
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<b><color=#FFC857>{nm}</color></b>  <color=#7FB2FF>{total:0}</color>", _label);
                    cy += lh;
                    foreach (var g in snap.Equipment)
                    {
                        var sc = GearScore.ScoreItem(g);
                        // icon
                        var iconRect = new Rect(ix, cy, rowH - 4, rowH - 4);
                        if (g.Icon != null) GUI.DrawTexture(iconRect, g.Icon, ScaleMode.ScaleToFit);
                        else { var prev = GUI.color; GUI.color = new Color(1, 1, 1, 0.12f); GUI.DrawTexture(iconRect, _white); GUI.color = prev; }
                        float tx = ix + rowH;
                        string slot = string.IsNullOrEmpty(g.Slot) ? "" : Loc.G(g.Slot);
                        string lvl = g.Level > 0 ? $" <size=10><color=#8a93a0>Lv{g.Level}</color></size>" : "";
                        GUI.Label(new Rect(tx, cy, iw - rowH - 70, rowH), $"<color=#{GradeColor(g.Grade)}>{g.Name}</color>{lvl}", _tiny);
                        GUI.Label(new Rect(x + w - Pad - 64, cy, 60, rowH), $"<color=#7FB2FF>{sc.Total:0}</color>", _label);
                        cy += rowH;
                        if (_detailed)
                            foreach (var p in sc.Parts)
                            {
                                GUI.Label(new Rect(tx + 8, cy, iw - rowH - 70, lh * 0.9f), $"<size=10><color=#9aa3af>{Loc.G(p.Label)}</color></size>", _tiny);
                                GUI.Label(new Rect(x + w - Pad - 64, cy, 60, lh * 0.9f), $"<size=10><color=#7c93ad>{p.Points:0.#}</color></size>", _tiny);
                                cy += lh * 0.9f;
                            }
                    }
                }
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        // grade -> hex colour (common grey .. legendary gold). Unknown -> grey.
        private static string GradeColor(string grade)
        {
            switch ((grade ?? "").ToUpperInvariant())
            {
                case "LEGENDARY": case "MYTHIC": return "FFB13B";
                case "EPIC": return "C56BFF";
                case "RARE": return "4FA8FF";
                case "UNCOMMON": case "MAGIC": return "5fd07c";
                default: return "cdd5df";
            }
        }
    }
}
```

- [ ] **Step 2: Verify `OverlayFonts.Apply` signature accepts this style set**

Run: `grep -n "public static void Apply" DpsMeter/OverlayFonts.cs`
If `Apply` is a `params GUIStyle[]` it already accepts any count. If it is fixed-arity, pass the same number of styles it expects (drop `_box`, which needs no font). Adjust the call accordingly.

- [ ] **Step 3: Build**

Run: `dotnet build DpsMeter/DpsMeter.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 4: Commit (Task 4 + Task 5 together)**

```bash
git add DpsMeter/Plugin.cs DpsMeter/GearScoreOverlayBehaviour.cs
git commit -m "feat(gear): GearScore F7 panel + config wiring + F1 hub entry"
```

---

## Task 6: Localization keys

**Files:**
- Modify: `DpsMeter/Localization.cs`

- [ ] **Step 1: Add UI string keys**

In the dictionary literal (the `{ "key", new[] { zhHant, en, ja, zhHans, es } }` block near line 150), add:

```csharp
            { "gearscore_title", new[] { "裝備評分", "Gear Score", "装備スコア", "装备评分", "Puntuación" } },
            { "gearscore_empty", new[] { "找不到角色", "No characters found", "キャラ未検出", "找不到角色", "Sin personajes" } },
            { "mode_simple",     new[] { "簡易", "Simple", "簡易", "简易", "Simple" } },
            { "mode_detailed",   new[] { "詳細", "Detailed", "詳細", "详细", "Detalle" } },
            { "grade",           new[] { "稀有度", "Rarity", "レア度", "稀有度", "Rareza" } },
            { "level",           new[] { "等級", "Level", "レベル", "等级", "Nivel" } },
```

(Affix/stat names like `attack`, `critrate`, and socket entries reuse keys that already exist in this table from the compare panel; `socket:<stat>` lines fall through `Loc.G` to show the raw label, which is acceptable.)

- [ ] **Step 2: Build**

Run: `dotnet build DpsMeter/DpsMeter.csproj -c Release`
Expected: Build succeeded.

- [ ] **Step 3: Commit**

```bash
git add DpsMeter/Localization.cs
git commit -m "feat(gear): localization keys for the gear-score panel"
```

---

## Task 7: In-game verification + coefficient calibration

**Files:**
- Modify (as needed): `DpsMeter/HeroProbe.cs` (real obfuscated names), `DpsMeter/GearScore.cs` (calibrated coefficients).

This task REQUIRES running the game (deploy DLL, restart the game yourself, confirm load — see memory `deploy-restart-verify`).

- [ ] **Step 1: Deploy the built DLL to the BepInEx plugins folder** (per the project's existing deploy step).

- [ ] **Step 2: Enable `Debug.LogSnapshot` and trigger a capture** so `DiagGearScore` dumps item members. Read the BepInEx log for `[gearscore]` lines.

- [ ] **Step 3: Map real member names** for item level, the three socket containers, and the icon sprite from the dump. Replace the candidate arrays (`LevelMembers`, `SocketContainers`, `IconMembers`) and socket entry getters (`iox`/`ioy`) in `HeroProbe` with the confirmed names.

- [ ] **Step 4: Capture a few real affix/socket values + grades** from the dump/log. Sanity-check that no single term dominates the total; adjust `GradeBase`, `LevelWeight`, `StatWeight` in `GearScore.cs` so a clearly-better item scores higher. Re-run `TrackerTests` (update the expected numbers in the test if coefficients change).

- [ ] **Step 5: Verify the panel in-game** — open F7, confirm: party totals show, simple mode lists items with icons + scores, detailed mode expands per-effect points, the 詳細/簡易 button toggles, drag/resize/close work, F1 hub shows the toggle.

- [ ] **Step 6: Commit calibration**

```bash
git add DpsMeter/HeroProbe.cs DpsMeter/GearScore.cs TrackerTests/Program.cs
git commit -m "fix(gear): confirmed obfuscated paths + calibrated GearScore coefficients"
```

---

## Self-Review notes

- **Spec coverage:** rarity+level+affix+socket formula → Task 2; data capture → Task 3; new F7 panel with simple/detailed toggle + per-effect points + icon → Task 5; F1 hub entry → Task 5 Awake; graceful degradation → Task 3 helpers (0/null) + Task 5 glyph fallback; coefficient calibration + path confirmation → Task 7. Stage-compare untouched (per spec YAGNI).
- **Placeholders:** the obfuscated member-name arrays are deliberate candidate lists resolved in Task 7 via a real dump — not unspecified TODOs; they fail safe meanwhile.
- **Type consistency:** `GearScore.ItemScore`/`Part` used in Task 5 match Task 2 definitions; `GearItem.Grade/Level/Sockets/Icon` used in Tasks 3/5 match Task 1.
