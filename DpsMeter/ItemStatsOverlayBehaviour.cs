using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (F8): counts of everything held in the backpack + warehouse (stash) +
    /// trading stash — NOT equipped gear. A 品階/種類/清單 tab switches between a rarity breakdown, a
    /// category breakdown, and a per-ItemKey list (icon-less, name + ×N). The body is a FIXED-height,
    /// mouse-wheel-scrolled viewport. Read-only; data comes from the decrypted save via
    /// <see cref="SaveGearReader.ReadInventory"/>.</summary>
    public class ItemStatsOverlayBehaviour : MonoBehaviour
    {
        public ItemStatsOverlayBehaviour(System.IntPtr ptr) : base(ptr) { }

        private const int Slot = 10;          // InputCompat panel slot (must be unique per panel)
        private const float Pad = 10f;
        private enum View { Grade, Type, List }

        private Rect _rect = new Rect(70, 70, 360, 0);
        private bool _visible;
        private View _view = View.Grade;
        private float _opacity = 0.9f;
        private bool _placed;
        private float _wantX, _wantY, _scale = 1f;
        private Vector2 _dragOffset; private bool _dragging;
        private float _scrollY;
        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _btn, _box; private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;
        private Rect _closeRect;
        private readonly List<Rect> _tabRects = new List<Rect>();
        private readonly PanelResize _resize = new PanelResize();
        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        private InventoryStats _stats = new InventoryStats();
        private float _nextRefresh;

        void Awake()
        {
            _opacity = Mathf.Clamp01(Plugin.Opacity.Value + 0.15f);
            _rect.width = Mathf.Max(300, Plugin.ItemStatsPanelWidth.Value);
            _visible = Plugin.ItemStatsStartVisible.Value;
            PanelRegistry.Register("items", 7, "I", () => Loc.G("items_title"), Plugin.ItemStatsToggleKey,
                () => _visible, v => _visible = v);
        }

        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.ItemStatsPosX.Value, py = Plugin.ItemStatsPosY.Value;
            if (px < 0 || py < 0) { _rect.x = 90f; _rect.y = 90f; } else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y; _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.Poll();
                InputCompat.SetPanel(Slot, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.ItemStatsToggleKey)) { _visible = !_visible; if (_visible) Refresh(); }
                if (_visible && Time.realtimeSinceStartup >= _nextRefresh) { _nextRefresh = Time.realtimeSinceStartup + 3f; Refresh(); }
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
            try { _stats = SaveGearReader.ReadInventory() ?? new InventoryStats(); }
            catch { }
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(Slot); } return; }
            Vector2 ms = InputCompat.MouseGuiPos();
            Vector2 m = UiScale.ToLocal(ms, _rect.x, _rect.y, _scale);
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(Slot, m, ref rw, ref dh, 280f, Mathf.Max(280f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 360f; Plugin.ItemStatsPanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.ItemStatsPanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                for (int t = 0; t < _tabRects.Count; t++)
                    if (_tabRects[t].Contains(m)) { _view = (View)t; _scrollY = 0f; return; }
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
                    Plugin.ItemStatsPosX.Value = _rect.x; Plugin.ItemStatsPosY.Value = _rect.y;
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
            _btn = new GUIStyle(GUI.skin.button) { fontSize = fsm, fontStyle = FontStyle.Bold, richText = true };
            _box = new GUIStyle(); _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _label, _dim, _btn);
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

                // ---- build the rows for the current view ----
                var rows = new List<KeyValuePair<string, int>>();   // (richTextLabel, count)
                if (_view == View.Grade)
                    foreach (var kv in _stats.ByGrade)
                        rows.Add(new KeyValuePair<string, int>($"<color=#{GradeColor(kv.Key)}>{GradeName(kv.Key)}</color>", kv.Value));
                else if (_view == View.Type)
                    foreach (var kv in _stats.ByType)
                        rows.Add(new KeyValuePair<string, int>(TypeLabel(kv.Key), kv.Value));
                else
                    foreach (var it in _stats.Items)
                        rows.Add(new KeyValuePair<string, int>($"<color=#{GradeColor(it.Grade)}>{it.Name}</color>", it.Count));

                float contentH = Mathf.Max(rows.Count, 1) * lh;

                // ---- fixed-height layout: header + summary + tabs, then capped scroll viewport ----
                float headerBlock = Pad + lh /*title*/ + lh /*summary*/ + lh /*tabs*/;
                float maxPanelH = Screen.height * 0.88f / Mathf.Max(0.3f, UiScale.User);
                // cap the viewport to ~16 rows so a long list scrolls inside a compact panel rather than
                // stretching the whole panel down the screen.
                float maxBodyH = Mathf.Min(maxPanelH - headerBlock - Pad, lh * 16f);
                maxBodyH = Mathf.Max(lh * 4f, maxBodyH);
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

                // ---- header: title + close ----
                float cy = _rect.y + Pad;
                GUI.Label(new Rect(ix, cy, iw - 30, lh), Loc.G("items_title"), _title);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh);
                GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                // ---- summary line: bag / stash / trade / total ----
                string summary =
                    $"<color=#9aa3af>{Loc.G("items_bag")}</color> <color=#cdd5df>{_stats.BagUsed}/{_stats.BagTotal}</color>   " +
                    $"<color=#9aa3af>{Loc.G("items_stash")}</color> <color=#cdd5df>{_stats.StashUsed}/{_stats.StashTotal}</color>   " +
                    (_stats.TradeTotal > 0 ? $"<color=#9aa3af>{Loc.G("items_trade")}</color> <color=#cdd5df>{_stats.TradeUsed}/{_stats.TradeTotal}</color>   " : "") +
                    $"<color=#FFC857>{Loc.G("items_total")} {_stats.Total}</color>";
                GUI.Label(new Rect(ix, cy, iw, lh), summary, _dim);
                cy += lh;

                // ---- view tabs ----
                _tabRects.Clear();
                float tx = ix; float maxX = x + w - Pad;
                DrawTab(ref tx, maxX, cy, lh, View.Grade, Loc.G("items_by_grade"));
                DrawTab(ref tx, maxX, cy, lh, View.Type, Loc.G("items_by_type"));
                DrawTab(ref tx, maxX, cy, lh, View.List, Loc.G("items_list"));
                cy += lh;

                if (_stats.Total == 0 || rows.Count == 0)
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), Loc.G("items_empty"), _dim);
                    _resize.DrawGrip(_white, _rect);
                    return;
                }

                // ---- body: fixed viewport, wheel-scrolled, whole-line windowed ----
                float bodyTop = cy;
                float maxScroll = Mathf.Max(0f, contentH - bodyH);
                _scrollY = Mathf.Clamp(_scrollY, 0f, maxScroll);
                int first = Mathf.Clamp(Mathf.FloorToInt(_scrollY / lh), 0, rows.Count);
                _scrollY = first * lh;   // snap so the top row is whole
                float drawn = 0f;
                for (int r = first; r < rows.Count; r++)
                {
                    if (drawn + lh > bodyH + 0.5f) break;
                    float ry = bodyTop + drawn;
                    GUI.Label(new Rect(ix, ry, iw - 56, lh), rows[r].Key, _label);
                    GUI.Label(new Rect(x + w - Pad - 56, ry, 52, lh), $"<color=#7FB2FF>×{rows[r].Value}</color>", _label);
                    drawn += lh;
                }

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

        private void DrawTab(ref float tx, float maxX, float cy, float lh, View v, string label)
        {
            bool sel = (_view == v);
            float bw = Mathf.Min(_btn.CalcSize(new GUIContent(label)).x + 14f, 140f);
            if (tx + bw > maxX && _tabRects.Count > 0) bw = Mathf.Max(40f, maxX - tx);
            var r = new Rect(tx, cy - 1, bw, lh);
            GUI.Button(r, sel ? $"<color=#FFD24D>{label}</color>" : label, _btn);
            if (sel) DrawRect(r.x + 3, r.y + lh - 2, r.width - 6, 2, new Color(1f, 0.82f, 0.30f, 0.95f));
            _tabRects.Add(r);
            tx += bw + 4f;
        }

        private void DrawRect(float rx, float ry, float rw, float rh, Color c)
        {
            var prev = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(rx, ry, rw, rh), _white); GUI.color = prev;
        }

        // localize the type bucket: broad category (type_*) or gear subtype (gtype_*). Loc.G returns the key
        // itself when missing, so an unrecognised token falls back to the raw token rather than "gtype_XYZ".
        private static string TypeLabel(string token)
        {
            switch (token)
            {
                case "GEAR": case "MATERIAL": case "STAGEBOX": case "UNKNOWN": return Loc.G("type_" + token);
            }
            string k = "gtype_" + token, s = Loc.G(k);
            return s == k ? token : s;
        }

        // grade -> localized display name; "" / unknown -> the "other" bucket label.
        private static string GradeName(string grade)
        {
            if (string.IsNullOrEmpty(grade)) return Loc.G("type_UNKNOWN");
            string k = "grade_" + grade.ToUpperInvariant(), s = Loc.G(k);
            return s == k ? grade : s;
        }

        private static string GradeColor(string grade)
        {
            switch ((grade ?? "").ToUpperInvariant())
            {
                case "COSMIC": return "FF4D6D";
                case "DIVINE": return "FFD24D";
                case "CELESTIAL": return "63E6E2";
                case "BEYOND": return "B197FC";
                case "ARCANA": return "F783AC";
                case "IMMORTAL": return "FF922B";
                case "LEGENDARY": return "FFB13B";
                case "RARE": return "4FA8FF";
                case "UNCOMMON": return "5FD07C";
                default: return "CDD5DF";
            }
        }
    }
}
