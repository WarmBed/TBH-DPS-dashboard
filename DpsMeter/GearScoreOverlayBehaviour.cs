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

        private const int Slot = 9;          // InputCompat panel slot
        private const float Pad = 10f;
        private Rect _rect = new Rect(60, 60, 360, 0);
        private bool _visible;
        private bool _detailed;               // 詳細/簡易 toggle
        private float _opacity = 0.9f;
        private bool _placed;
        private float _wantX, _wantY, _scale = 1f;
        private Vector2 _dragOffset; private bool _dragging;
        private Texture2D _white, _bgTex;
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
                InputCompat.SetPanel(Slot, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.GearScoreToggleKey)) { _visible = !_visible; if (_visible) Refresh(); }
                if (_visible && Time.realtimeSinceStartup >= _nextRefresh) { _nextRefresh = Time.realtimeSinceStartup + 1.5f; Refresh(); }
                if (_visible) HandlePointer(); else if (_dragging) _dragging = false;
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

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(Slot); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(Slot, m, ref rw, ref dh, 300f, Mathf.Max(300f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 360f; Plugin.GearScorePanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.GearScorePanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                if (_modeRect.Contains(m)) { _detailed = !_detailed; return; }
                if (_rect.Contains(m) && InputCompat.ClaimDrag(Slot)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(Slot)) { _dragging = false; return; }
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
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
                float x = _rect.x, w = _rect.width, ix = x + Pad, iw = w - Pad * 2;
                float rowH = lh * 1.5f;

                // measure
                float h = Pad + lh;   // header
                foreach (var snap in _party)
                {
                    h += lh;          // character name + total
                    foreach (var g in snap.Equipment) { h += rowH; if (_detailed) h += lh * 0.9f * Mathf.Max(1, GearScore.ScoreItem(g).Parts.Count); }
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

                if (_party.Count == 0)
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), Loc.G("gearscore_empty"), _dim);
                    _resize.DrawGrip(_white, _rect);
                    return;
                }

                foreach (var snap in _party)
                {
                    var cs = GearScore.ScoreCharacter(snap);
                    string nm = string.IsNullOrEmpty(snap.CharacterName) ? snap.Character : snap.CharacterName;
                    GUI.Label(new Rect(ix, cy, iw, lh),
                        $"<b><color=#FFC857>{nm}</color></b>  <color=#7FB2FF>{cs.Total:0}</color>" +
                        $"  <size=11><color=#ef6a5a>⚔{cs.Attack:0}</color> <color=#5fd07c>⛨{cs.Defense:0}</color></size>", _label);
                    cy += lh;
                    foreach (var g in snap.Equipment)
                    {
                        var sc = GearScore.ScoreItem(g);
                        var iconRect = new Rect(ix, cy, rowH - 4, rowH - 4);
                        var tex = g.Icon as Texture;
                        if (tex != null) GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);
                        else { var prev = GUI.color; GUI.color = new Color(1, 1, 1, 0.12f); GUI.DrawTexture(iconRect, _white); GUI.color = prev; }
                        float tx = ix + rowH;
                        string lvl = g.Level > 0 ? $" <size=10><color=#8a93a0>Lv{g.Level}</color></size>" : "";
                        // applied-socket badge (裝飾/雕刻/銘文): ◆ per filled socket
                        int sk = g.DecoCount + g.EngraveCount + g.InscribeCount;
                        string socks = sk > 0 ? $" <size=10><color=#67d6c3>{new string('◆', Mathf.Min(sk, 6))}{(sk > 6 ? "+" : "")}</color></size>" : "";
                        GUI.Label(new Rect(tx, cy, iw - rowH - 64, rowH), $"<color=#{GradeColor(g.Grade)}>{g.Name}</color>{lvl}{socks}", _tiny);
                        GUI.Label(new Rect(x + w - Pad - 60, cy, 56, rowH), $"<color=#7FB2FF>{sc.Total:0}</color>", _label);
                        cy += rowH;
                        if (_detailed)
                            foreach (var p in sc.Parts)
                            {
                                GUI.Label(new Rect(tx + 8, cy, iw - rowH - 64, lh * 0.9f), $"<size=10><color=#9aa3af>{Loc.G(p.Label)}</color></size>", _tiny);
                                GUI.Label(new Rect(x + w - Pad - 60, cy, 56, lh * 0.9f), $"<size=10><color=#7c93ad>{p.Points:0.#}</color></size>", _tiny);
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
