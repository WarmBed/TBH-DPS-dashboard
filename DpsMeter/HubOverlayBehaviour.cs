using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (F1): the Control Center / 中控台. Lists every panel registered in
    /// <see cref="PanelRegistry"/> as a toggle button (lit when the panel is visible, dim when hidden)
    /// and shows a tiny live summary (DPS · session time · boxes opened). Flipping a button calls the
    /// owning overlay's Set delegate, so panels can be shown/hidden from one place. Drag/close/scale
    /// behave exactly like the other overlays; auto-hides while a game menu is open.</summary>
    public class HubOverlayBehaviour : MonoBehaviour
    {
        public HubOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const float Pad = 10f;
        private Rect _rect = new Rect(24, 80, 260, 0);
        private bool _visible, _placed;
        private float _wantX, _wantY;
        private Vector2 _dragOffset; private bool _dragging;

        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box, _tagR, _icon, _tip;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;   // font sizes the styles were last built with (live-rebuild on change)

        private Rect _closeRect, _handleRect, _scaleDownRect, _scaleUpRect, _hideRect;
        private Rect _fontBigDownRect, _fontBigUpRect, _fontSmDownRect, _fontSmUpRect;
        private Rect _borderRect;
        private readonly List<Rect> _swatchRects = new List<Rect>();
        private Rect _settingsRect;
        private bool _settingsOpen;   // collapses the 3 settings rows behind the ⚙ button (session-only)
        private readonly List<Rect> _panelRects = new List<Rect>();
        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();

        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        void Awake()
        {
            _rect.width = Mathf.Max(220, Plugin.HubPanelWidth.Value);
            _visible = Plugin.HubStartVisible.Value;
        }
        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.HubPosX.Value, py = Plugin.HubPosY.Value;
            if (px < 0 || py < 0) { _rect.x = 24f; _rect.y = 80f; }
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y; _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.SetPanel(5, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.HubToggleKey)) _visible = !_visible;
                if (_visible) HandlePointer();
                else if (_dragging) _dragging = false;
            }
            catch { }
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(5); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            // resize grip (bottom-right): width only
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(5, m, ref rw, ref dh, 220f, Mathf.Max(220f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 260f; Plugin.HubPanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.HubPanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                if (_settingsRect.Contains(m)) { _settingsOpen = !_settingsOpen; return; }
                if (_settingsOpen)
                {
                if (_scaleDownRect.Contains(m)) { UiScale.Adjust(-UiScale.Step); return; }
                if (_scaleUpRect.Contains(m)) { UiScale.Adjust(UiScale.Step); return; }
                if (_fontBigDownRect.Contains(m)) { Plugin.FontSize.Value = Mathf.Clamp(Plugin.FontSize.Value - 1, 9, 30); return; }
                if (_fontBigUpRect.Contains(m)) { Plugin.FontSize.Value = Mathf.Clamp(Plugin.FontSize.Value + 1, 9, 30); return; }
                if (_fontSmDownRect.Contains(m)) { Plugin.FontSizeSmall.Value = Mathf.Clamp(Plugin.FontSizeSmall.Value - 1, 8, 26); return; }
                if (_fontSmUpRect.Contains(m)) { Plugin.FontSizeSmall.Value = Mathf.Clamp(Plugin.FontSizeSmall.Value + 1, 8, 26); return; }
                if (_hideRect.Contains(m)) { Plugin.HideOnGameMenu.Value = !Plugin.HideOnGameMenu.Value; return; }
                if (_borderRect.Contains(m)) { Plugin.BorderOn.Value = !Plugin.BorderOn.Value; return; }
                for (int i = 0; i < _swatchRects.Count && i < PanelBorder.Palette.Length; i++)
                    if (_swatchRects[i].Contains(m)) { Plugin.BorderColor.Value = PanelBorder.Palette[i].Id; Plugin.BorderOn.Value = true; return; }
                }
                // panel toggle buttons (rebuilt in OnGUI, tested in registry order)
                var panels = PanelRegistry.Panels;
                int n = Mathf.Min(_panelRects.Count, panels.Count);
                for (int i = 0; i < n; i++)
                {
                    if (_panelRects[i].Contains(m))
                    {
                        try { var e = panels[i]; e.Set(!e.Get()); } catch { }
                        return;
                    }
                }
                // drag from anywhere on the panel except the buttons (handled/returned above)
                if (_rect.Contains(m) && InputCompat.ClaimDrag(5)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(5)) { _dragging = false; return; }
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased()) { _dragging = false; InputCompat.ReleaseDrag(5); _wantX = _rect.x; _wantY = _rect.y; Plugin.HubPosX.Value = _rect.x; Plugin.HubPosY.Value = _rect.y; }
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
            _tagR = new GUIStyle { fontSize = Mathf.Max(9, fsm - 2), richText = true, alignment = TextAnchor.MiddleRight }; _tagR.normal.textColor = new Color(0.7f, 0.75f, 0.85f);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = fsm, fontStyle = FontStyle.Bold, richText = true };
            _icon = new GUIStyle { fontSize = fs + 3, richText = true, alignment = TextAnchor.MiddleCenter }; _icon.normal.textColor = Color.white;
            _tip = new GUIStyle { fontSize = fsm, richText = true, alignment = TextAnchor.MiddleCenter }; _tip.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
            _box = new GUIStyle(); _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _label, _dim, _tiny, _tagR, _btn, _icon, _tip);
            _stylesReady = true;
        }

        private void DrawRect(float x, float y, float w, float h, Color c) { var p = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(x, y, w, h), _white); GUI.color = p; }

        void OnGUI()
        {
            if (!_visible || GameUiState.MenuOpen()) return;
            GUI.depth = -8;
            var prevM = GUI.matrix;
            try
            {
                EnsureAssets();
                if (!_placed) PlaceDefault();
                int fs = Plugin.FontSize.Value; float lh = fs + 6;
                float x = _rect.x, ix = x + Pad, w = _rect.width, iw = w - Pad * 2;

                var panels = PanelRegistry.Panels;

                // height: title + summary + separator gap + one icon row + tooltip line + padding
                // icons shrink to fit the panel width so the row never overflows as panels are added
                float gap = 6f;
                int nIcons = Mathf.Max(1, panels.Count);
                float iconSz = Mathf.Min(lh + 10f, Mathf.Floor((iw - (nIcons - 1) * gap) / nIcons));
                if (iconSz < 12f) iconSz = 12f;
                float h = Pad + lh /*title*/ + lh /*summary*/ + lh * 0.4f /*separator*/ + iconSz /*icon row*/ + (_settingsOpen ? lh * 3 : 0) /*settings/font/border rows*/ + lh /*tooltip*/ + Pad;
                _rect.height = h;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                if (!_dragging) { _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale)); _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale)); }
                x = _rect.x; ix = x + Pad;
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                float cy = _rect.y + Pad;
                // title bar (whole row is the drag handle)
                _handleRect = new Rect(x, _rect.y, w, lh);
                GUI.Label(new Rect(ix, cy, iw - 50, lh), Loc.G("hub_title"), _title);
                _settingsRect = new Rect(x + w - 50, cy - 2, 22, lh);
                GUI.Button(_settingsRect, _settingsOpen ? "<color=#7fffa0>⚙</color>" : "⚙", _btn);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh); GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                // summary: DPS <val>   時長 <m:ss>   寶箱 <n>
                string dps = "—", dur = "—"; int boxes = 0;
                try
                {
                    if (Plugin.Tracker != null)
                    {
                        var snap = Plugin.Tracker.GetSnapshot(UnityEngine.Time.time);
                        dps = snap.LiveDps >= 1000f ? (snap.LiveDps / 1000f).ToString("0.#") + "k" : snap.LiveDps.ToString("0");
                        int secs = Mathf.Max(0, (int)snap.DurationSeconds);
                        dur = (secs / 60) + ":" + (secs % 60).ToString("00");
                    }
                }
                catch { }
                try { boxes = BoxTracker.Events.Count; } catch { }
                GUI.Label(new Rect(ix, cy, iw, lh),
                    $"<color=#aeb6c2>DPS <color=#eaf3ee>{dps}</color>　{Loc.G("duration")} <color=#eaf3ee>{dur}</color>　{Loc.G("boxes")} <color=#eaf3ee>{boxes}</color></color>", _label);
                cy += lh;

                // separator
                DrawRect(ix, cy + lh * 0.2f, iw, 1, new Color(1, 1, 1, 0.12f));
                cy += lh * 0.4f;

                // horizontal icon row: one toggle icon per registered panel (lit when on, dim when off)
                _panelRects.Clear();
                Vector2 mLocal = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
                int hover = -1;
                float rowW = panels.Count * iconSz + Mathf.Max(0, panels.Count - 1) * gap;
                float bx = ix + Mathf.Max(0f, (iw - rowW) * 0.5f);   // center the icons
                for (int i = 0; i < panels.Count; i++)
                {
                    var e = panels[i];
                    bool on = false; string ic;
                    try { on = e.Get(); } catch { }
                    try { ic = string.IsNullOrEmpty(e.Icon) ? "▣" : e.Icon; } catch { ic = "▣"; }
                    var r = new Rect(bx + i * (iconSz + gap), cy, iconSz, iconSz);
                    _panelRects.Add(r);
                    if (r.Contains(mLocal)) hover = i;
                    GUI.Button(r, GUIContent.none, _btn);
                    if (on) DrawRect(r.x + 2, r.y + r.height - 3, r.width - 4, 2, new Color(0.5f, 1f, 0.63f, 0.95f)); // lit underline
                    string col = on ? "#eaf3ee" : "#6b7480";
                    GUI.Label(r, $"<color={col}>{ic}</color>", _icon);
                }
                cy += iconSz + 4f;

                // settings rows (UI scale / fonts / border) live behind the title-bar ⚙ toggle
                if (_settingsOpen)
                {
                // settings row: global UI scale (moved here from the DPS panel) + hide-in-menu toggle
                _scaleDownRect = new Rect(ix, cy, 18, lh); GUI.Button(_scaleDownRect, "−", _btn);
                GUI.Label(new Rect(ix + 20, cy + 1, 46, lh), $"<size=10><color=#9fb4cc>UI {UiScale.User * 100f:0}%</color></size>", _dim);
                _scaleUpRect = new Rect(ix + 66, cy, 18, lh); GUI.Button(_scaleUpRect, "+", _btn);
                bool hideOn = Plugin.HideOnGameMenu.Value;
                float hideW = Mathf.Max(56f, iw - 92f);
                _hideRect = new Rect(ix + iw - hideW, cy, hideW, lh);
                GUI.Button(_hideRect, $"{Loc.G("hide_on_menu")} <color={(hideOn ? "#7fffa0" : "#ff8a8a")}>{(hideOn ? Loc.G("snd_on") : Loc.G("snd_off"))}</color>", _btn);
                cy += lh;

                // font row: 大字 (primary) + 小字 (detail) sizes, each with a −/+ stepper (live-applied)
                float fx = ix;
                GUI.Label(new Rect(fx, cy + 1, 34, lh), $"<size=10><color=#9fb4cc>{Loc.G("font_big")}</color></size>", _dim); fx += 34;
                _fontBigDownRect = new Rect(fx, cy, 18, lh); GUI.Button(_fontBigDownRect, "−", _btn); fx += 20;
                GUI.Label(new Rect(fx, cy + 1, 20, lh), $"<size=10><color=#eaf3ee>{Plugin.FontSize.Value}</color></size>", _dim); fx += 20;
                _fontBigUpRect = new Rect(fx, cy, 18, lh); GUI.Button(_fontBigUpRect, "+", _btn); fx += 28;
                GUI.Label(new Rect(fx, cy + 1, 34, lh), $"<size=10><color=#9fb4cc>{Loc.G("font_small")}</color></size>", _dim); fx += 34;
                _fontSmDownRect = new Rect(fx, cy, 18, lh); GUI.Button(_fontSmDownRect, "−", _btn); fx += 20;
                GUI.Label(new Rect(fx, cy + 1, 20, lh), $"<size=10><color=#eaf3ee>{Plugin.FontSizeSmall.Value}</color></size>", _dim); fx += 20;
                _fontSmUpRect = new Rect(fx, cy, 18, lh); GUI.Button(_fontSmUpRect, "+", _btn);
                cy += lh;

                // border row: on/off toggle + color swatches (clicking a swatch also turns the border on)
                bool bOn = false; try { bOn = Plugin.BorderOn.Value; } catch { }
                float bw = 64f;
                _borderRect = new Rect(ix, cy, bw, lh);
                GUI.Button(_borderRect, $"{Loc.G("border")} <color={(bOn ? "#7fffa0" : "#ff8a8a")}>{(bOn ? Loc.G("snd_on") : Loc.G("snd_off"))}</color>", _btn);
                _swatchRects.Clear();
                float sw = Mathf.Min(lh - 2f, (iw - bw - 8f) / PanelBorder.Palette.Length - 4f), sx = ix + bw + 8f;
                string curCol = "white"; try { curCol = Plugin.BorderColor.Value; } catch { }
                for (int i = 0; i < PanelBorder.Palette.Length; i++)
                {
                    var pp = PanelBorder.Palette[i];
                    var r = new Rect(sx + i * (sw + 4f), cy + (lh - sw) * 0.5f, sw, sw);
                    _swatchRects.Add(r);
                    bool sel = bOn && pp.Id == curCol;
                    if (sel) DrawRect(r.x - 2, r.y - 2, r.width + 4, r.height + 4, new Color(1f, 1f, 1f, 0.85f));
                    DrawRect(r.x, r.y, r.width, r.height, pp.C);
                }
                cy += lh;
                }

                // hover tooltip: panel name + hotkey, centered under the hovered icon
                if (hover >= 0 && hover < panels.Count)
                {
                    var e = panels[hover];
                    string name, tag;
                    try { name = e.Name(); } catch { name = e.Id ?? "?"; }
                    try { tag = e.Hotkey == KeyCode.None ? "" : e.Hotkey.ToString(); } catch { tag = ""; }
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#eaf3ee>{name}</color>  <color=#9fb4cc>{tag}</color>", _tip);
                }
                else if (panels.Count == 0)
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#8a93a0>{Loc.G("no_runs")}</color>", _tip);
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }
    }
}
