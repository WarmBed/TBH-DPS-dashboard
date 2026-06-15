using System;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (F4): opened-box quality stats. A grade×kind matrix (count + %) over the
    /// lifetime tally, plus a paged time-ordered open log. Read-only; resize-safe; auto-hides with the
    /// other panels while a game menu is open.</summary>
    public class BoxOpenOverlayBehaviour : MonoBehaviour
    {
        public BoxOpenOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const float Pad = 10f;
        private Rect _rect = new Rect(80, 80, 460, 0);
        private bool _visible, _placed;
        private float _wantX, _wantY;
        private Vector2 _dragOffset; private bool _dragging;

        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box, _col, _cell;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;   // font sizes the styles were last built with (live-rebuild on change)
        private Rect _closeRect, _clearRect;
        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();
        private float _scrollY;   // log-list scroll offset (snapped to whole rows)
        private float _listH;     // resizable log-list viewport height (px), from BoxOpenPanelHeight

        // per-grade row colors (index == grade int) — matched to the in-game grade chips
        private static readonly Color[] GradeColors = {
            new Color(0.62f,0.64f,0.68f), // 0 普通  grey
            new Color(0.40f,0.82f,0.45f), // 1 罕見  green
            new Color(0.32f,0.74f,0.96f), // 2 稀有  blue
            new Color(1.00f,0.60f,0.20f), // 3 傳奇  orange
            new Color(0.95f,0.36f,0.36f), // 4 不朽  red
            new Color(0.70f,0.45f,0.96f), // 5 至寶  purple
            new Color(0.96f,0.40f,0.86f), // 6 超凡  magenta
            new Color(0.36f,0.88f,0.90f), // 7 天界  cyan
            new Color(0.96f,0.95f,0.80f), // 8 神聖  cream
            new Color(1.00f,1.00f,1.00f), // 9 宇宙  white
        };

        private static string Hex(Color c) => $"#{(int)(c.r*255):X2}{(int)(c.g*255):X2}{(int)(c.b*255):X2}";

        /// <summary>Hex color for a grade, matching this panel's per-grade row colors. Public so the
        /// loot-heatmap tooltip can color grade names identically (one source of truth).</summary>
        public static string GradeHex(int g) => Hex(g >= 0 && g < GradeColors.Length ? GradeColors[g] : Color.white);

        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        void Awake()
        {
            _rect.width = Mathf.Max(420, Plugin.BoxOpenPanelWidth.Value);
            _listH = Mathf.Max(60f, Plugin.BoxOpenPanelHeight.Value);
            _visible = Plugin.BoxOpenStartVisible.Value;
            PanelRegistry.Register("boxopen", 5, "◆", () => Loc.G("boxopen_title"), Plugin.BoxOpenToggleKey, () => _visible, v => _visible = v);
        }
        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.BoxOpenPosX.Value, py = Plugin.BoxOpenPosY.Value;
            if (px < 0 || py < 0) { _rect.x = Mathf.Max(24, (Screen.width - _rect.width) * 0.5f); _rect.y = 110f; }
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y; _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.SetPanel(6, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.BoxOpenToggleKey)) _visible = !_visible;
                if (_visible)
                {
                    float wd = InputCompat.WheelDelta(6);
                    if (wd != 0f) { float lh = Plugin.FontSize.Value + 6; _scrollY -= (wd / 120f) * 3f * lh; }
                    HandlePointer();
                }
                else if (_dragging) _dragging = false;
            }
            catch { }
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(6); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            // resize grip (bottom-right): width + scrollable-log height
            float glh = Plugin.FontSize.Value + 6;
            float rw = _rect.width;
            var rr = _resize.Handle(6, m, ref rw, ref _listH,
                360f, Mathf.Max(360f, Screen.width * 0.9f), glh * 3f, Screen.height * 0.85f, true);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 460f; _listH = 180f; SaveBoxOpenSize(); return; }
            if (rr == PanelResize.Result.Committed) { SaveBoxOpenSize(); return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                if (_clearRect.Contains(m)) { BoxOpenTracker.ClearAll(); _scrollY = 0; return; }
                if (_rect.Contains(m) && InputCompat.ClaimDrag(6)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(6)) { _dragging = false; return; }   // a panel on top stole the press
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased()) { _dragging = false; _wantX = _rect.x; _wantY = _rect.y; Plugin.BoxOpenPosX.Value = _rect.x; Plugin.BoxOpenPosY.Value = _rect.y; }
            }
        }

        private void EnsureAssets()
        {
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_bgTex == null) { _bgTex = new Texture2D(1, 1); _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f)); _bgTex.Apply(); if (_box != null) _box.normal.background = _bgTex; }
            int fs = Plugin.FontSize.Value, fsm = Plugin.FontSizeSmall.Value;
            if (_stylesReady && _builtFs == fs && _builtFsm == fsm) return;
            _builtFs = fs; _builtFsm = fsm;
            _title = new GUIStyle { fontSize = fs, fontStyle = FontStyle.Bold, richText = true }; _title.normal.textColor = new Color(1f, 0.86f, 0.35f);
            _label = new GUIStyle { fontSize = fs, richText = true }; _label.normal.textColor = new Color(0.93f, 0.93f, 0.93f);
            _dim = new GUIStyle { fontSize = fsm, richText = true }; _dim.normal.textColor = new Color(0.78f, 0.84f, 0.95f);
            _tiny = new GUIStyle { fontSize = Mathf.Max(9, fsm - 2), richText = true }; _tiny.normal.textColor = new Color(0.7f, 0.75f, 0.85f);
            _col = new GUIStyle { fontSize = fs, richText = true, alignment = TextAnchor.MiddleRight }; _col.normal.textColor = Color.white;
            _cell = new GUIStyle { fontSize = Mathf.Max(9, fsm - 1), richText = true, alignment = TextAnchor.MiddleRight }; _cell.normal.textColor = Color.white;
            _btn = new GUIStyle(GUI.skin.button) { fontSize = fsm, fontStyle = FontStyle.Bold, richText = true };
            _box = new GUIStyle(); _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _label, _dim, _tiny, _col, _cell, _btn);
            _stylesReady = true;
        }

        private void DrawRect(float x, float y, float w, float h, Color c) { var p = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(x, y, w, h), _white); GUI.color = p; }

        private static readonly int[] KindCols = { (int)BoxKind.Normal, (int)BoxKind.Boss, (int)BoxKind.ActBoss };

        void OnGUI()
        {
            if (!_visible || GameUiState.MenuOpen()) return;
            GUI.depth = -8;
            var prevM = GUI.matrix;
            try
            {
                EnsureAssets();
                if (!_placed) PlaceDefault();
                var st = BoxOpenTracker.Stats;
                int fs = Plugin.FontSize.Value; float lh = fs + 6;
                bool hasUnknown = st.KindTotal((int)BoxKind.Unknown) > 0;

                int gradeRows = 0; for (int g = 0; g < BoxGrade.Count; g++) if (st.GradeTotal(g) > 0) gradeRows++;

                int n = st.Log.Count;
                int visible = Mathf.Max(1, Mathf.FloorToInt(_listH / lh));
                float listAreaH = visible * lh;

                float h = Pad + lh /*title*/ + lh /*matrix header*/ + lh * Mathf.Max(gradeRows, 1)
                        + lh * 0.5f + lh /*log header*/ + listAreaH /*scrollable log*/ + Pad;
                _rect.height = h;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                if (!_dragging) { _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale)); _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale)); }
                float x = _rect.x, ix = x + Pad, w = _rect.width, iw = w - Pad * 2;
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                float cy = _rect.y + Pad;
                float clearW = Mathf.Max(60f, _btn.CalcSize(new GUIContent(Loc.G("reset_all"))).x + 12f);
                GUI.Label(new Rect(ix, cy, iw, lh), $"{Loc.G("boxopen_title")}  <size=11><color=#9fb4cc>{Loc.G("boxopen_total")} {st.Total()}</color></size>", _title);
                _clearRect = new Rect(x + w - 28 - clearW, cy - 1, clearW, lh); GUI.Button(_clearRect, Loc.G("reset_all"), _btn);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh); GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                // ---- matrix: rows = grade, cols = Normal | Boss | ActBoss | (Unknown) | Total ----
                int nCols = KindCols.Length + (hasUnknown ? 1 : 0) + 1; // + Total
                float gradeColW = iw * 0.26f;
                float cellW = (iw - gradeColW) / nCols;
                GUI.Label(new Rect(ix, cy, gradeColW, lh), $"<size=11><color=#9fb4cc>{Loc.G("boxopen_grade")}</color></size>", _dim);
                float hx = ix + gradeColW;
                string[] kindKeys = { "box_kind_normal", "box_kind_boss", "box_kind_actboss" };
                for (int c = 0; c < KindCols.Length; c++) { GUI.Label(new Rect(hx, cy, cellW, lh), $"<size=11><color=#9fb4cc>{Loc.G(kindKeys[c])}</color></size>", _cell); hx += cellW; }
                if (hasUnknown) { GUI.Label(new Rect(hx, cy, cellW, lh), $"<size=11><color=#9fb4cc>{Loc.G("box_kind_unknown")}</color></size>", _cell); hx += cellW; }
                GUI.Label(new Rect(hx, cy, cellW, lh), "<size=11><color=#cfd6e0>Σ</color></size>", _cell);
                cy += lh;
                DrawRect(ix, cy - 1, iw, 1, new Color(1, 1, 1, 0.12f));

                for (int g = 0; g < BoxGrade.Count; g++)
                {
                    if (st.GradeTotal(g) == 0) continue;
                    string gc = Hex(g < GradeColors.Length ? GradeColors[g] : Color.white);
                    GUI.Label(new Rect(ix, cy, gradeColW, lh), $"<color={gc}>{Loc.G("grade_" + BoxGrade.KeyOf(g))}</color>", _label);
                    float cx = ix + gradeColW;
                    for (int c = 0; c < KindCols.Length; c++) { DrawCell(cx, cy, cellW, lh, st, KindCols[c], g, gc); cx += cellW; }
                    if (hasUnknown) { DrawCell(cx, cy, cellW, lh, st, (int)BoxKind.Unknown, g, gc); cx += cellW; }
                    long gt = st.GradeTotal(g); double gp = st.Total() > 0 ? 100.0 * gt / st.Total() : 0;
                    GUI.Label(new Rect(cx, cy, cellW, lh), $"<color={gc}>{gt}</color> <size=9><color=#9aa3b0>{gp:0.#}%</color></size>", _cell);
                    cy += lh;
                }
                if (gradeRows == 0) { GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#8a93a0>{Loc.G("box_empty")}</color>", _tiny); cy += lh; }

                // ---- log ----
                cy += lh * 0.5f;
                DrawRect(ix, cy + lh - 1, iw, 1, new Color(1, 1, 1, 0.12f));
                GUI.Label(new Rect(ix, cy, iw * 0.22f, lh), $"<size=11><color=#9fb4cc>{Loc.G("time_col")}</color></size>", _dim);
                GUI.Label(new Rect(ix + iw * 0.22f, cy, iw * 0.20f, lh), $"<size=11><color=#9fb4cc>{Loc.G("boxopen_kind")}</color></size>", _dim);
                GUI.Label(new Rect(ix + iw * 0.42f, cy, iw * 0.22f, lh), $"<size=11><color=#9fb4cc>{Loc.G("boxopen_grade")}</color></size>", _dim);
                GUI.Label(new Rect(ix + iw * 0.64f, cy, iw * 0.36f, lh), $"<size=11><color=#9fb4cc>{Loc.G("boxopen_item")}</color></size>", _dim);
                cy += lh;

                string[] kindShort = { "box_kind_normal", "box_kind_boss", "box_kind_actboss", "box_kind_unknown" };
                float listTop = cy;
                int maxFirst = Mathf.Max(0, n - visible);
                int first = Mathf.Clamp(Mathf.RoundToInt(_scrollY / lh), 0, maxFirst);
                _scrollY = first * lh;
                for (int r = 0; r < visible && (first + r) < n; r++)
                {
                    int i = first + r;
                    float ry = listTop + r * lh;
                    var e = st.Log[n - 1 - i];
                    if ((i & 1) == 1) DrawRect(ix, ry, iw, lh, new Color(1, 1, 1, 0.03f));
                    string gc = Hex(e.Grade >= 0 && e.Grade < GradeColors.Length ? GradeColors[e.Grade] : Color.white);
                    int kind = (e.Kind >= 0 && e.Kind < kindShort.Length) ? e.Kind : (int)BoxKind.Unknown;
                    GUI.Label(new Rect(ix, ry, iw * 0.22f, lh), $"<color=#aeb6c2>{e.Time:HH:mm:ss}</color>", _tiny);
                    GUI.Label(new Rect(ix + iw * 0.22f, ry, iw * 0.20f, lh), $"<color=#9aa3b0>{Loc.G(kindShort[kind])}</color>", _tiny);
                    GUI.Label(new Rect(ix + iw * 0.42f, ry, iw * 0.22f, lh), $"<color={gc}>{Loc.G("grade_" + BoxGrade.KeyOf(e.Grade))}</color>", _tiny);
                    GUI.Label(new Rect(ix + iw * 0.64f, ry, iw * 0.36f, lh), $"<color=#eaf3ee>{ResolveItem(e.Name)}</color>", _tiny);
                }
                if (n == 0) GUI.Label(new Rect(ix, listTop, iw, lh), $"<color=#8a93a0>{Loc.G("box_empty")}</color>", _tiny);
                if (n > visible)
                {
                    float trackX = ix + iw - 3f;
                    DrawRect(trackX, listTop, 3f, listAreaH, new Color(1, 1, 1, 0.08f));
                    float thumbH = Mathf.Max(14f, listAreaH * visible / n);
                    float thumbY = listTop + (listAreaH - thumbH) * (first / (float)maxFirst);
                    DrawRect(trackX, thumbY, 3f, thumbH, new Color(1, 1, 1, 0.35f));
                }
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        private void SaveBoxOpenSize() { Plugin.BoxOpenPanelWidth.Value = _rect.width; Plugin.BoxOpenPanelHeight.Value = _listH; }

        // BoxOpenLog stores a loc key like "ItemName_113003"; resolve the trailing id via the bundled
        // item table (live-localized), falling back to the raw key if unknown.
        private static string ResolveItem(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int us = key.LastIndexOf('_');
            string digits = us >= 0 ? key.Substring(us + 1) : key;
            if (int.TryParse(digits, out int id))
            {
                string nm = ItemNameStore.Get(id);
                if (!string.IsNullOrEmpty(nm)) return nm;
            }
            return key;
        }

        private void DrawCell(float x, float y, float w, float lh, BoxOpenStats st, int kind, int grade, string gc)
        {
            long c = st.Count(kind, grade);
            if (c == 0) { GUI.Label(new Rect(x, y, w, lh), "<size=9><color=#5a626e>·</color></size>", _cell); return; }
            double pct = st.Percent(kind, grade);
            GUI.Label(new Rect(x, y, w, lh), $"<color={gc}>{c}</color> <size=9><color=#9aa3b0>{pct:0.#}%</color></size>", _cell);
        }
    }
}
