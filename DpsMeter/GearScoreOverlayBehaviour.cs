using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (F7): live GearScore for the whole party. A 詳細/簡易 toggle expands per-item
    /// rows (icon + name + rarity + level + score) and, in detailed mode, each effect line's point
    /// contribution. The party is split into per-class tabs (全部 / 騎士 / …) so it never dumps every
    /// character at once; the body is a FIXED-height, mouse-wheel-scrolled viewport. Read-only.</summary>
    public class GearScoreOverlayBehaviour : MonoBehaviour
    {
        public GearScoreOverlayBehaviour(System.IntPtr ptr) : base(ptr) { }

        private const int Slot = 9;          // InputCompat panel slot
        private const float Pad = 10f;
        private const int FiltAll = -1, FiltUninit = -2;   // _classFilter sentinels
        private Rect _rect = new Rect(60, 60, 360, 0);
        private bool _visible;
        private bool _detailed;               // 詳細/簡易 toggle
        private float _opacity = 0.9f;
        private bool _placed;
        private float _wantX, _wantY, _scale = 1f;
        private Vector2 _dragOffset; private bool _dragging;
        private float _scrollY;               // body scroll offset (snapped to whole lines)
        private int _classFilter = FiltUninit; // -1 = 全部, -2 = uninitialised, >=0 = class id (heroKey/100)
        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box; private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;
        private Rect _closeRect, _modeRect;
        private readonly PanelResize _resize = new PanelResize();
        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        // per-frame scratch (reused to avoid GC): present class ids, filtered+sorted party indices,
        // flattened render lines, and the current tab hit-rects.
        private readonly List<int> _present = new List<int>();
        private readonly List<int> _order = new List<int>();
        private readonly List<Line> _lines = new List<Line>();
        private readonly List<Rect> _tabRects = new List<Rect>();
        private readonly List<int> _tabIds = new List<int>();

        private enum LK : byte { Char, Item, Part }
        private struct Line { public LK K; public float H; public int Snap, Item, Part; public GearScore.ItemScore Score; }

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
                InputCompat.SetPanel(Slot, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.GearScoreToggleKey)) { _visible = !_visible; if (_visible) Refresh(); }
                if (_visible && Time.realtimeSinceStartup >= _nextRefresh) { _nextRefresh = Time.realtimeSinceStartup + 1.5f; Refresh(); }
                if (_visible)
                {
                    float wd = InputCompat.WheelDelta(Slot);
                    if (wd != 0f) { float lh = Plugin.FontSize.Value + 7; _scrollY -= (wd / 120f) * 3f * lh; }
                    HandlePointer();
                }
                else if (_dragging) _dragging = false;
            }
            catch { }
        }

        private void Refresh()
        {
            // Gear comes from the decrypted save (same source F11 uses) via CharacterReader.CaptureParty —
            // the in-memory ACTk uid collection can't be enumerated, so HeroProbe.ReadGear reads 0.
            try
            {
                var party = CharacterReader.CaptureParty();
                _party.Clear();
                if (party != null) _party.AddRange(party);
            }
            catch { }
        }

        // hero class id from the stable HeroKey (keys are class*100+variant); 0 = unknown.
        private static int ClassOf(CharacterSnapshot s)
            => (s != null && int.TryParse(s.Character, out int k) && k > 0) ? k / 100 : 0;

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(Slot); } return; }
            Vector2 ms = InputCompat.MouseGuiPos();                                  // raw GUI/screen space (for drag)
            Vector2 m = UiScale.ToLocal(ms, _rect.x, _rect.y, _scale);              // unscaled local space (for hit-tests)
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(Slot, m, ref rw, ref dh, 300f, Mathf.Max(300f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 360f; Plugin.GearScorePanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.GearScorePanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                if (_modeRect.Contains(m)) { _detailed = !_detailed; _scrollY = 0f; return; }
                for (int t = 0; t < _tabRects.Count; t++)
                    if (_tabRects[t].Contains(m)) { _classFilter = _tabIds[t]; _scrollY = 0f; return; }
                // drag delta is in SCREEN space: the panel's top-left always renders at (_rect.x,_rect.y)
                // regardless of _scale, so screen-space tracking is 1:1 and stable. (Using the scaled local
                // 'm' here fed _rect back into ToLocal's pivot, which diverged when _scale dropped below ~0.5
                // — the "亂跳" bug.)
                if (_rect.Contains(m) && InputCompat.ClaimDrag(Slot)) { _dragging = true; _dragOffset = ms - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(Slot)) { _dragging = false; return; }
                if (InputCompat.MouseHeld()) { _rect.x = ms.x - _dragOffset.x; _rect.y = ms.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased())
                {
                    _dragging = false; InputCompat.ReleaseDrag(Slot);
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
                float w = _rect.width, iw = w - Pad * 2;
                float rowH = lh * 1.25f;   // item row height (icon + main-font name), kept tight

                // ---- which classes are present, and resolve the selected tab ----
                _present.Clear();
                foreach (var s in _party) { int c = ClassOf(s); if (!_present.Contains(c)) _present.Add(c); }
                _present.Sort();
                bool showTabs = _present.Count > 1;
                if (_classFilter == FiltUninit || (_classFilter != FiltAll && !_present.Contains(_classFilter)))
                    _classFilter = _present.Count > 0 ? _present[0] : FiltAll;
                int filter = showTabs ? _classFilter : FiltAll;

                // ---- filtered + class-sorted party, then flattened into render lines ----
                _order.Clear();
                for (int i = 0; i < _party.Count; i++)
                    if (filter == FiltAll || ClassOf(_party[i]) == filter) _order.Add(i);
                _order.Sort((a, b) => { int ca = ClassOf(_party[a]), cb = ClassOf(_party[b]); if (ca != cb) return ca - cb; return string.CompareOrdinal(_party[a].CharacterName, _party[b].CharacterName); });

                _lines.Clear();
                float contentH = 0f;
                foreach (int i in _order)
                {
                    var snap = _party[i];
                    _lines.Add(new Line { K = LK.Char, H = lh, Snap = i }); contentH += lh;
                    var eq = snap.Equipment;
                    for (int j = 0; j < eq.Count; j++)
                    {
                        var sc = GearScore.ScoreItem(eq[j]);
                        _lines.Add(new Line { K = LK.Item, H = rowH, Snap = i, Item = j, Score = sc }); contentH += rowH;
                        if (_detailed)
                            for (int p = 0; p < sc.Parts.Count; p++)
                            { _lines.Add(new Line { K = LK.Part, H = lh * 0.92f, Snap = i, Item = j, Part = p, Score = sc }); contentH += lh * 0.92f; }
                    }
                }
                if (_order.Count == 0) contentH += lh;   // "no characters" line

                // ---- fixed-height layout: header (+tabs) on top, capped scroll viewport below ----
                float headerBlock = Pad + lh + (showTabs ? lh : 0f);
                float maxPanelH = Screen.height * 0.88f / Mathf.Max(0.3f, UiScale.User);   // unscaled cap
                float maxBodyH = Mathf.Max(lh * 4f, maxPanelH - headerBlock - Pad);
                float bodyH = Mathf.Min(contentH, maxBodyH);
                _rect.height = headerBlock + bodyH + Pad;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                if (!_dragging)
                {
                    _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale));
                    _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale));
                }
                float x = _rect.x, ix = x + Pad;
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                // ---- header row: title + 詳細/簡易 + close ----
                float cy = _rect.y + Pad;
                GUI.Label(new Rect(ix, cy, iw - 130, lh), Loc.G("gearscore_title"), _title);
                _modeRect = new Rect(x + w - 116, cy - 1, 86, lh);
                GUI.Button(_modeRect, Loc.G(_detailed ? "mode_detailed" : "mode_simple"), _btn);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh);
                GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                // ---- class tabs (only with 2+ classes) ----
                _tabRects.Clear(); _tabIds.Clear();
                if (showTabs)
                {
                    float tx = ix; float maxX = x + w - Pad;
                    DrawTab(ref tx, maxX, cy, lh, FiltAll, Loc.G("gearscore_all"));
                    foreach (int c in _present) DrawTab(ref tx, maxX, cy, lh, c, TabLabel(c));
                    cy += lh;
                }

                if (_order.Count == 0)
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), Loc.G("gearscore_empty"), _dim);
                    _resize.DrawGrip(_white, _rect);
                    return;
                }

                // ---- body: fixed viewport, wheel-scrolled, whole-line windowed (no clip) ----
                float bodyTop = cy;
                float maxScroll = Mathf.Max(0f, contentH - bodyH);
                _scrollY = Mathf.Clamp(_scrollY, 0f, maxScroll);
                int first = 0; float acc = 0f;
                while (first < _lines.Count && acc + _lines[first].H <= _scrollY) { acc += _lines[first].H; first++; }
                _scrollY = acc;   // snap so the top line is whole
                float drawn = 0f;
                for (int li = first; li < _lines.Count; li++)
                {
                    var ln = _lines[li];
                    if (drawn + ln.H > bodyH + 0.5f) break;
                    DrawLine(ln, bodyTop + drawn, x, w, ix, iw, lh, rowH);
                    drawn += ln.H;
                }

                // scroll indicator on the right edge of the viewport
                if (contentH > bodyH + 0.5f)
                {
                    float trackX = x + w - 4f;
                    DrawRect(trackX, bodyTop, 2.5f, bodyH, new Color(1f, 1f, 1f, 0.10f));
                    float thumbH = Mathf.Max(18f, bodyH * (bodyH / contentH));
                    float t = maxScroll > 0f ? _scrollY / maxScroll : 0f;
                    DrawRect(trackX, bodyTop + (bodyH - thumbH) * t, 2.5f, thumbH, new Color(0.55f, 0.7f, 1f, 0.65f));
                }

                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        // tab label for a class id: the (localized) name of the first character in that class.
        private string TabLabel(int classId)
        {
            foreach (var s in _party)
                if (ClassOf(s) == classId)
                    return !string.IsNullOrEmpty(s.CharacterName) ? s.CharacterName
                         : (!string.IsNullOrEmpty(s.Character) ? s.Character : HeroProbe.HeroName(classId * 100 + 1));
            return HeroProbe.HeroName(classId * 100 + 1);
        }

        private void DrawTab(ref float tx, float maxX, float cy, float lh, int id, string label)
        {
            bool sel = (_classFilter == id);
            float bw = Mathf.Min(_btn.CalcSize(new GUIContent(label)).x + 12f, 140f);
            if (tx + bw > maxX && _tabRects.Count > 0) return;   // out of room — drop overflow tabs
            var r = new Rect(tx, cy - 1, bw, lh);
            GUI.Button(r, sel ? $"<color=#FFD24D>{label}</color>" : label, _btn);
            if (sel) DrawRect(r.x + 3, r.y + lh - 2, r.width - 6, 2, new Color(1f, 0.82f, 0.30f, 0.95f));
            _tabRects.Add(r); _tabIds.Add(id);
            tx += bw + 4f;
        }

        private void DrawLine(Line ln, float ry, float x, float w, float ix, float iw, float lh, float rowH)
        {
            if (ln.K == LK.Char)
            {
                var snap = _party[ln.Snap];
                var cs = GearScore.ScoreCharacter(snap);
                string nm = string.IsNullOrEmpty(snap.CharacterName) ? snap.Character : snap.CharacterName;
                DrawRect(ix, ry + lh * 0.34f, 8f, 8f, ClassColor(ClassOf(snap)));
                GUI.Label(new Rect(ix + 13, ry, iw - 13, lh),
                    $"<b><color=#FFC857>{nm}</color></b>  <color=#7FB2FF>{cs.Total:0}</color>" +
                    $"   <color=#ef6a5a>⚔{cs.Attack:0}</color> <color=#5fd07c>⛨{cs.Defense:0}</color>", _label);
                return;
            }
            if (ln.K == LK.Item)
            {
                var g = _party[ln.Snap].Equipment[ln.Item];
                var iconRect = new Rect(ix, ry, rowH - 3, rowH - 3);
                Texture tex = GearIconCache.Get(g.ItemKey) ?? g.Icon as Texture;
                if (tex != null) GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
                else { var prev = GUI.color; GUI.color = new Color(1, 1, 1, 0.12f); GUI.DrawTexture(iconRect, _white); GUI.color = prev; }
                float tx = ix + rowH;
                float ty = ry + (rowH - lh) * 0.5f;   // vertically centre the name in the row
                string lvl = g.Level > 0 ? $" <color=#9aa1ad>Lv{g.Level}</color>" : "";
                int sk = g.DecoCount + g.EngraveCount + g.InscribeCount;
                string socks = sk > 0 ? $" <color=#67d6c3>{new string('◆', Mathf.Min(sk, 6))}{(sk > 6 ? "+" : "")}</color>" : "";
                GUI.Label(new Rect(tx, ty, iw - rowH - 64, lh), $"<color=#{GradeColor(g.Grade)}>{g.Name}</color>{lvl}{socks}", _label);
                GUI.Label(new Rect(x + w - Pad - 60, ty, 56, lh), $"<color=#7FB2FF>{ln.Score.Total:0}</color>", _label);
                return;
            }
            // LK.Part — one scored effect line under its item
            var p = ln.Score.Parts[ln.Part];
            float subH = lh * 0.92f;
            GUI.Label(new Rect(ix + rowH + 6, ry, iw - rowH - 64, subH), $"<color=#9aa3af>{Loc.G(p.Label)}</color>", _dim);
            GUI.Label(new Rect(x + w - Pad - 60, ry, 56, subH), $"<color=#8aa0b6>{p.Points:0.#}</color>", _dim);
        }

        private void DrawRect(float rx, float ry, float rw, float rh, Color c)
        {
            var prev = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(rx, ry, rw, rh), _white); GUI.color = prev;
        }

        // hero-class colour by class id (heroKey/100): Knight/Ranger/Sorcerer/Priest/…  Unknown -> grey.
        private static Color ClassColor(int classId)
        {
            switch (classId)
            {
                case 1: return new Color(0.90f, 0.78f, 0.35f);  // Knight — gold
                case 2: return new Color(0.40f, 0.86f, 0.46f);  // Ranger — green
                case 3: return new Color(0.55f, 0.60f, 0.97f);  // Sorcerer — blue-violet
                case 4: return new Color(0.95f, 0.62f, 0.40f);  // Priest — orange
                case 5: return new Color(0.85f, 0.45f, 0.85f);  // (extra class) — magenta
                default: return new Color(0.7f, 0.74f, 0.8f);
            }
        }

        // grade -> hex colour (NO leading '#') across TBH's 10-tier rarity ladder. Unknown -> grey.
        // Public so the fitting bench colours item names from the same proven palette (one source of truth).
        public static string GradeColor(string grade)
        {
            switch ((grade ?? "").ToUpperInvariant())
            {
                case "COSMIC": return "FF4D6D";      // red-pink
                case "DIVINE": return "FFD24D";      // gold
                case "CELESTIAL": return "63E6E2";   // cyan
                case "BEYOND": return "F783AC";      // pink (matches in-game 超凡)
                case "ARCANA": return "B197FC";      // violet (matches in-game 至寶)
                case "IMMORTAL": return "FF922B";    // orange
                case "LEGENDARY": return "FFB13B";   // amber
                case "RARE": return "4FA8FF";        // blue
                case "UNCOMMON": return "5FD07C";    // green
                default: return "CDD5DF";            // common / unknown — grey
            }
        }
    }
}
