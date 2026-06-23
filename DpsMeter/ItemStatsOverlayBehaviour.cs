using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (F8): counts of everything held in the backpack + warehouse (stash) +
    /// trading stash — NOT equipped gear. A single scrollable item list (icon + name + ×N) with two rows
    /// of filter chips above it: rarity (品階) and category (種類). Selecting a chip filters the list;
    /// clicking it again clears that filter. Chip counts cross-update with the other active filter. The
    /// body is a FIXED-height, mouse-wheel-scrolled viewport. Read-only; data comes from the decrypted
    /// save via <see cref="SaveGearReader.ReadInventory"/>.</summary>
    public class ItemStatsOverlayBehaviour : MonoBehaviour
    {
        public ItemStatsOverlayBehaviour(System.IntPtr ptr) : base(ptr) { }

        private const int Slot = 10;          // InputCompat panel slot (must be unique per panel)
        private const float Pad = 10f;

        private Rect _rect = new Rect(70, 70, 360, 0);
        private bool _visible;
        private string _gradeFilter = "";     // "" = all
        private string _typeFilter = "";      // "" = all
        private float _opacity = 0.9f;
        private bool _placed;
        private float _wantX, _wantY, _scale = 1f;
        private Vector2 _dragOffset; private bool _dragging;
        private float _scrollY;
        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _btn, _box; private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;
        private Rect _closeRect;
        private readonly PanelResize _resize = new PanelResize();
        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        private InventoryStats _stats = new InventoryStats();
        private float _nextRefresh;

        // per-frame scratch (reused to avoid GC)
        private readonly List<int> _filtered = new List<int>();               // item indices passing both filters
        private readonly List<Rect> _chipRects = new List<Rect>();            // chip hit-boxes (this frame)
        private readonly List<bool> _chipIsGrade = new List<bool>();          // true = grade chip, false = type chip
        private readonly List<string> _chipVals = new List<string>();        // chip filter value ("" = all)
        private readonly List<KeyValuePair<string, int>> _gradeChips = new List<KeyValuePair<string, int>>();
        private readonly List<KeyValuePair<string, int>> _typeChips = new List<KeyValuePair<string, int>>();

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
            if (rr == PanelResize.Result.Reset) { _rect.width = 468f; Plugin.ItemStatsPanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.ItemStatsPanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                for (int c = 0; c < _chipRects.Count; c++)
                    if (_chipRects[c].Contains(m))
                    {
                        // toggle: re-clicking an active filter clears it
                        if (_chipIsGrade[c]) _gradeFilter = _gradeFilter == _chipVals[c] ? "" : _chipVals[c];
                        else _typeFilter = _typeFilter == _chipVals[c] ? "" : _chipVals[c];
                        _scrollY = 0f;
                        return;
                    }
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

        // recompute the filtered item list and the per-dimension chip counts (each dimension counts under the
        // OTHER active filter, so the chip numbers stay consistent with what selecting them would show).
        private void Rebuild()
        {
            _filtered.Clear(); _gradeChips.Clear(); _typeChips.Clear();
            var gradeCount = new Dictionary<string, int>();
            var typeCount = new Dictionary<string, int>();
            int gradeTotal = 0, typeTotal = 0;
            for (int i = 0; i < _stats.Items.Count; i++)
            {
                var it = _stats.Items[i];
                bool gm = _gradeFilter == "" || it.Grade == _gradeFilter || (_gradeFilter == "?" && string.IsNullOrEmpty(it.Grade));
                bool tm = _typeFilter == "" || it.Type == _typeFilter;
                if (tm) { string k = string.IsNullOrEmpty(it.Grade) ? "?" : it.Grade; gradeCount.TryGetValue(k, out int g); gradeCount[k] = g + it.Count; gradeTotal += it.Count; }
                if (gm) { typeCount.TryGetValue(it.Type, out int t); typeCount[it.Type] = t + it.Count; typeTotal += it.Count; }
                if (gm && tm) _filtered.Add(i);
            }
            // ordered chip lists: grades by rarity (use ByGrade's order), types by count (ByType's order)
            foreach (var kv in _stats.ByGrade)
            {
                string k = string.IsNullOrEmpty(kv.Key) ? "?" : kv.Key;
                if (gradeCount.TryGetValue(k, out int n) && n > 0) _gradeChips.Add(new KeyValuePair<string, int>(k, n));
            }
            foreach (var kv in _stats.ByType)
                if (typeCount.TryGetValue(kv.Key, out int n) && n > 0) _typeChips.Add(new KeyValuePair<string, int>(kv.Key, n));
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
                Rebuild();
                int fs = Plugin.FontSize.Value; float lh = fs + 7;
                float w = _rect.width, iw = w - Pad * 2;
                float rowH = lh * 1.5f;       // icon list row
                float chipH = lh + 2f;        // chip row line height

                // ---- measure the two wrapping chip rows (width-only, so height matches the draw pass) ----
                float gradePfx = _dim.CalcSize(new GUIContent(Loc.G("items_by_grade"))).x + 6f;
                float typePfx = _dim.CalcSize(new GUIContent(Loc.G("items_by_type"))).x + 6f;
                float gradeH = ChipRow(true, gradePfx, 0f, iw, chipH, false, 0f);
                float typeH = ChipRow(false, typePfx, 0f, iw, chipH, false, 0f);

                int count = _filtered.Count;
                float contentH = Mathf.Max(count, 1) * rowH;

                float headerBlock = Pad + lh /*title*/ + lh /*summary*/ + gradeH + typeH;
                float maxPanelH = Screen.height * 0.88f / Mathf.Max(0.3f, UiScale.User);
                float maxBodyH = Mathf.Min(maxPanelH - headerBlock - Pad, rowH * 14f);
                maxBodyH = Mathf.Max(rowH * 4f, maxBodyH);
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

                // ---- filter chip rows (rarity, then category) ----
                _chipRects.Clear(); _chipIsGrade.Clear(); _chipVals.Clear();
                GUI.Label(new Rect(ix, cy + 1, gradePfx, chipH), Loc.G("items_by_grade"), _dim);
                ChipRow(true, gradePfx, cy, iw, chipH, true, ix);
                cy += gradeH;
                GUI.Label(new Rect(ix, cy + 1, typePfx, chipH), Loc.G("items_by_type"), _dim);
                ChipRow(false, typePfx, cy, iw, chipH, true, ix);
                cy += typeH;

                if (count == 0)
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), Loc.G("items_empty"), _dim);
                    _resize.DrawGrip(_white, _rect);
                    return;
                }

                // ---- body: fixed viewport, wheel-scrolled, whole-row windowed ----
                float bodyTop = cy;
                float maxScroll = Mathf.Max(0f, contentH - bodyH);
                _scrollY = Mathf.Clamp(_scrollY, 0f, maxScroll);
                int first = Mathf.Clamp(Mathf.FloorToInt(_scrollY / rowH), 0, count);
                _scrollY = first * rowH;
                float drawn = 0f;
                for (int r = first; r < count; r++)
                {
                    if (drawn + rowH > bodyH + 0.5f) break;
                    DrawItemRow(_filtered[r], bodyTop + drawn, x, w, ix, iw, lh, rowH);
                    drawn += rowH;
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

        // Lay out one wrapping chip row. Measure pass (draw=false): returns the row's total height, computed
        // from chip widths only so it matches the draw pass. Draw pass (draw=true): renders chips at the real
        // origin (ix/oyAbs) and records hit-boxes. Index -1 is the "all" reset chip; rarity chips are tinted
        // by grade colour. No local functions / optional params / struct args here — Il2Cpp type registration
        // rejects those signatures, which would fail the whole overlay-creation block.
        private float ChipRow(bool grade, float pfx, float oyAbs, float maxRelW, float chipH, bool draw, float ix)
        {
            var chips = grade ? _gradeChips : _typeChips;
            string curFilter = grade ? _gradeFilter : _typeFilter;
            float gap = 4f, relX = pfx, relY = 0f;
            int total = 0; foreach (var c in chips) total += c.Value;

            for (int idx = -1; idx < chips.Count; idx++)
            {
                string value, text; bool sel;
                if (idx < 0)
                {
                    value = ""; sel = curFilter == "";
                    text = $"{Loc.G("gearscore_all")} <color=#7f8794>{total}</color>";
                }
                else
                {
                    var c = chips[idx]; value = c.Key; sel = curFilter == c.Key;
                    string gk = c.Key == "?" ? "" : c.Key;
                    string name = grade ? $"<color=#{GradeColor(gk)}>{GradeName(gk)}</color>" : TypeLabel(c.Key);
                    text = sel ? $"<b>{name}</b> <color=#cdd5df>{c.Value}</color>" : $"{name} <color=#7f8794>{c.Value}</color>";
                }
                float bw = Mathf.Min(_btn.CalcSize(new GUIContent(text)).x + 10f, maxRelW);
                if (relX + bw > maxRelW && relX > pfx) { relX = pfx; relY += chipH; }
                if (draw)
                {
                    var r = new Rect(ix + relX, oyAbs + relY, bw, chipH - 2f);
                    GUI.Button(r, text, _btn);
                    if (sel) DrawRect(r.x + 3, r.y + chipH - 4f, r.width - 6, 2f, new Color(1f, 0.82f, 0.30f, 0.95f));
                    _chipRects.Add(r); _chipIsGrade.Add(grade); _chipVals.Add(value);
                }
                relX += bw + gap;
            }
            return relY + chipH;
        }

        // one list row: icon + grade-coloured name + ×N. Takes an index into _stats.Items (not the struct by
        // value — Il2Cpp registration rejects struct parameters).
        private void DrawItemRow(int itemIndex, float ry, float x, float w, float ix, float iw, float lh, float rowH)
        {
            var it = _stats.Items[itemIndex];
            var iconRect = new Rect(ix, ry + 1.5f, rowH - 4, rowH - 4);
            Texture tex = GearIconCache.Get(it.ItemKey);
            if (tex != null) GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
            else { var prev = GUI.color; GUI.color = new Color(1, 1, 1, 0.10f); GUI.DrawTexture(iconRect, _white); GUI.color = prev; }
            float tx = ix + rowH;
            float ty = ry + (rowH - lh) * 0.5f;
            GUI.Label(new Rect(tx, ty, iw - rowH - 56, lh), $"<color=#{GradeColor(it.Grade)}>{it.Name}</color>", _label);
            GUI.Label(new Rect(x + w - Pad - 56, ty, 52, lh), $"<color=#7FB2FF>×{it.Count}</color>", _label);
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
