using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (F5): the treasure-box log. Lists each box pickup (time · stage · box name)
    /// captured by <see cref="BoxTracker"/>, plus a session summary (total, per-stage, boxes/hour).
    /// Read-only; resize-safe and auto-hides with the other panels while a game menu is open.</summary>
    public class BoxOverlayBehaviour : MonoBehaviour
    {
        public BoxOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const float Pad = 10f;
        private Rect _rect = new Rect(80, 80, 420, 0);
        private bool _visible, _placed;
        private float _wantX, _wantY;
        private Vector2 _dragOffset; private bool _dragging;

        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box, _col;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;   // font sizes the styles were last built with (live-rebuild on change)

        private Rect _closeRect, _handleRect, _clearRect, _gearRect, _muteRect;
        private Rect _soundRect, _volRect, _testRect, _pickRect, _clearSndRect;
        private bool _volDrag, _settingsOpen;
        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();
        private float _scrollY;   // log-list scroll offset (snapped to whole rows)
        private float _listH;     // resizable log-list viewport height (px), from BoxPanelHeight

        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        void Awake()
        {
            _rect.width = Mathf.Max(380, Plugin.BoxPanelWidth.Value);
            _listH = Mathf.Max(60f, Plugin.BoxPanelHeight.Value);
            _visible = Plugin.BoxStartVisible.Value;
            PanelRegistry.Register("box", 4, "▣", () => Loc.G("box_title"), Plugin.BoxToggleKey, () => _visible, v => _visible = v);
            try
            {
                var src = gameObject.GetComponent<AudioSource>();
                if (src == null) src = gameObject.AddComponent<AudioSource>();
                BoxSound.Init(src);
            }
            catch { }
        }
        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.BoxPosX.Value, py = Plugin.BoxPosY.Value;
            if (px < 0 || py < 0) { _rect.x = Mathf.Max(24, (Screen.width - _rect.width) * 0.5f); _rect.y = 80f; }
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y; _placed = true;
        }

        void Update()
        {
            try
            {
                // a file picked on the dialog thread is applied here (AudioClip.Create must run on the main thread)
                if (BoxSound.PendingCustomPath != null)
                {
                    string p = BoxSound.PendingCustomPath; BoxSound.PendingCustomPath = null;
                    if (p.Length > 0) { Plugin.BoxSoundFile.Value = p; BoxSound.ReloadCustom(); BoxSound.Play(); }
                }
                InputCompat.SetPanel(4, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.BoxToggleKey)) _visible = !_visible;
                if (_visible)
                {
                    float wd = InputCompat.WheelDelta(4);
                    if (wd != 0f) { float lh = Plugin.FontSize.Value + 6; _scrollY -= (wd / 120f) * 3f * lh; }
                    HandlePointer();
                }
                else if (_dragging) _dragging = false;
            }
            catch { }
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(4); } if (_volDrag) _volDrag = false; return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            // resize grip (bottom-right): width + scrollable-list height
            float glh = Plugin.FontSize.Value + 6;
            float rw = _rect.width;
            var rr = _resize.Handle(4, m, ref rw, ref _listH,
                320f, Mathf.Max(320f, Screen.width * 0.9f), glh * 3f, Screen.height * 0.85f, true);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 420f; _listH = 180f; SaveBoxSize(); return; }
            if (rr == PanelResize.Result.Committed) { SaveBoxSize(); return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                if (_muteRect.Contains(m)) { Plugin.BoxSoundEnabled.Value = !Plugin.BoxSoundEnabled.Value; return; }
                if (_gearRect.Contains(m)) { _settingsOpen = !_settingsOpen; return; }
                if (_clearRect.Contains(m)) { BoxTracker.Events.Clear(); BoxStore.Clear(); BoxTracker.Version++; _scrollY = 0; return; }
                if (_settingsOpen && _soundRect.Contains(m)) { Plugin.BoxSoundEnabled.Value = !Plugin.BoxSoundEnabled.Value; return; }
                if (_settingsOpen && _testRect.Contains(m)) { BoxSound.Play(); return; }
                if (_settingsOpen && _pickRect.Contains(m))
                {
                    // native Open-File dialog is modal — run off the game thread; applied in Update()
                    System.Threading.Tasks.Task.Run(() => { var p = FileDialog.PickWav(); if (!string.IsNullOrEmpty(p)) BoxSound.PendingCustomPath = p; });
                    return;
                }
                if (_settingsOpen && _clearSndRect.Contains(m)) { Plugin.BoxSoundFile.Value = ""; BoxSound.ReloadCustom(); return; }
                if (_settingsOpen && _volRect.Contains(m)) { _volDrag = true; ApplyVolume(m.x); return; }
                if (_rect.Contains(m) && InputCompat.ClaimDrag(4)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_volDrag)
            {
                if (InputCompat.MouseHeld()) ApplyVolume(m.x);
                if (InputCompat.MouseReleased()) _volDrag = false;
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(4)) { _dragging = false; return; }   // a panel on top stole the press
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased()) { _dragging = false; _wantX = _rect.x; _wantY = _rect.y; Plugin.BoxPosX.Value = _rect.x; Plugin.BoxPosY.Value = _rect.y; }
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
            _btn = new GUIStyle(GUI.skin.button) { fontSize = fsm, fontStyle = FontStyle.Bold, richText = true };
            _box = new GUIStyle(); _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _label, _dim, _tiny, _col, _btn);
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

                var ev = BoxTracker.Events;
                // session stats
                var perStage = new Dictionary<string, int>();
                int bossCount = 0;
                foreach (var e in ev) { string s = string.IsNullOrEmpty(e.Stage) ? "?" : e.Stage; perStage.TryGetValue(s, out int c); perStage[s] = c + 1; if (IsBoss(e.Type)) bossCount++; }
                // per-hour reflects THIS session only (events since launch), so the persisted history
                // doesn't dilute the live rate to ~0.
                int sessCount = 0; DateTime sFirst = default, sLast = default;
                foreach (var e in ev)
                {
                    if (e.Time < Plugin.SessionStart) continue;
                    if (sessCount == 0) sFirst = e.Time;
                    sLast = e.Time; sessCount++;
                }
                double hours = sessCount >= 2 ? (sLast - sFirst).TotalHours : 0;
                double perHr = hours > 0.0003 ? sessCount / hours : 0;
                int statRows = Mathf.Min(perStage.Count, 6);

                int n = ev.Count;
                int visible = Mathf.Max(1, Mathf.FloorToInt(_listH / lh));
                float listAreaH = visible * lh;

                float h = Pad + lh /*title*/ + lh /*summary*/ + (_settingsOpen ? lh * 2 : 0) /*sound + file rows*/ + (statRows > 0 ? lh * 0.4f + lh * statRows : 0)
                    + lh /*log header*/ + listAreaH /*scrollable list*/ + Pad;
                _rect.height = h;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                if (!_dragging) { _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale)); _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale)); }
                x = _rect.x; ix = x + Pad;
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                float cy = _rect.y + Pad;
                _handleRect = new Rect(x, _rect.y, w, lh);
                float clearW = Mathf.Max(60f, _btn.CalcSize(new GUIContent(Loc.G("reset_all"))).x + 12f);
                GUI.Label(new Rect(ix, cy, w - Pad - clearW - 82 - ix + x, lh), $"{Loc.G("box_title")}  <size=11><color=#9fb4cc>{ev.Count}</color></size>", _title);
                // one-click mute toggle (♪ with a red bar when muted); stays in sync with the ⚙ on/off
                bool sndOn = Plugin.BoxSoundEnabled.Value;
                _muteRect = new Rect(x + w - 28 - clearW - 52, cy - 1, 22, lh);
                GUI.Button(_muteRect, sndOn ? "<color=#bfe3ff>♪</color>" : "<color=#ff8a8a>♪</color>", _btn);
                if (!sndOn) DrawRect(_muteRect.x + 3, _muteRect.y + lh * 0.5f - 1, _muteRect.width - 6, 2, new Color(1f, 0.45f, 0.45f, 0.95f));
                _gearRect = new Rect(x + w - 28 - clearW - 26, cy - 1, 22, lh); GUI.Button(_gearRect, _settingsOpen ? "<color=#ffd95a>⚙</color>" : "⚙", _btn);
                _clearRect = new Rect(x + w - 28 - clearW, cy - 1, clearW, lh); GUI.Button(_clearRect, Loc.G("reset_all"), _btn);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh); GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                // summary
                GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#aeb6c2>{Loc.G("box_total")} <color=#eaf3ee>{ev.Count}</color>　{Loc.G("box_boss")} <color=#7FB2FF>{bossCount}</color>　{Loc.G("box_per_hr")} <color=#eaf3ee>{perHr:0.#}</color></color>", _label);
                cy += lh;

                // sound row (only when the ⚙ settings panel is open): 音效 [開/關]  音量 [====O----] 60%  [▶ 試聽]
                if (!_settingsOpen) { _soundRect = _volRect = _testRect = _pickRect = _clearSndRect = new Rect(); }
                else
                {
                    bool on = Plugin.BoxSoundEnabled.Value;
                    float vol = Mathf.Clamp01(Plugin.BoxSoundVolume.Value);
                    float sx = ix;
                    GUI.Label(new Rect(sx, cy, 40, lh), $"<size=12><color=#9fb4cc>{Loc.G("box_sound")}</color></size>", _dim); sx += 42;
                    _soundRect = new Rect(sx, cy, 36, lh - 2); GUI.Button(_soundRect, on ? Loc.G("snd_on") : Loc.G("snd_off"), _btn); sx += 42;
                    GUI.Label(new Rect(sx, cy, 34, lh), $"<size=12><color=#9fb4cc>{Loc.G("box_vol")}</color></size>", _dim); sx += 36;
                    float trackW = 120f;
                    _volRect = new Rect(sx, cy, trackW, lh - 2);
                    float ty = cy + (lh - 2) * 0.5f;
                    DrawRect(sx, ty, trackW, 2, new Color(1, 1, 1, 0.18f));
                    DrawRect(sx, ty, trackW * vol, 2, on ? new Color(0.5f, 0.7f, 1f, 0.9f) : new Color(0.6f, 0.6f, 0.6f, 0.7f));
                    DrawRect(sx + Mathf.Clamp(trackW * vol, 0f, trackW) - 2, ty - 4, 4, 10, on ? new Color(0.75f, 0.88f, 1f) : new Color(0.6f, 0.6f, 0.6f));
                    sx += trackW + 6;
                    GUI.Label(new Rect(sx, cy, 40, lh), $"<size=11><color=#cdd5df>{(vol * 100):0}%</color></size>", _dim); sx += 42;
                    _testRect = new Rect(sx, cy, 56, lh - 2); GUI.Button(_testRect, "▶ " + Loc.G("box_test"), _btn);
                    cy += lh;

                    // file row: 音效檔  <name>                         [選擇…] [✕]
                    string cur = Plugin.BoxSoundFile.Value;
                    bool hasCustom = !string.IsNullOrEmpty(cur);
                    GUI.Label(new Rect(ix, cy, 52, lh), $"<size=12><color=#9fb4cc>{Loc.G("snd_file")}</color></size>", _dim);
                    float pickW = _btn.CalcSize(new GUIContent(Loc.G("snd_pick"))).x + 12f;
                    float clrW = hasCustom ? 24f : 0f;
                    _pickRect = new Rect(ix + iw - pickW - (hasCustom ? clrW + 4f : 0f), cy, pickW, lh - 2);
                    GUI.Button(_pickRect, Loc.G("snd_pick"), _btn);
                    if (hasCustom) { _clearSndRect = new Rect(ix + iw - clrW, cy, clrW, lh - 2); GUI.Button(_clearSndRect, "✕", _btn); }
                    else _clearSndRect = new Rect();
                    string nm = hasCustom ? System.IO.Path.GetFileName(cur) : Loc.G("snd_builtin");
                    float nameW = Mathf.Max(40f, _pickRect.x - (ix + 54f) - 6f);
                    GUI.Label(new Rect(ix + 54, cy, nameW, lh), $"<size=11><color=#cdd5df>{nm}</color></size>", _tiny);
                    cy += lh;
                }
                // per-stage counts
                if (statRows > 0)
                {
                    cy += lh * 0.4f;
                    var keys = new List<string>(perStage.Keys); keys.Sort((a, b) => perStage[b].CompareTo(perStage[a]));
                    for (int i = 0; i < statRows; i++)
                    {
                        GUI.Label(new Rect(ix, cy, iw * 0.7f, lh), $"<color=#9aa3b0>{LocalizeStage(keys[i])}</color>", _tiny);
                        GUI.Label(new Rect(ix + iw * 0.7f, cy, iw * 0.3f, lh), $"<color=#cdd5df>{perStage[keys[i]]}</color>", _col);
                        cy += lh;
                    }
                }

                // log header
                DrawRect(ix, cy + lh - 1, iw, 1, new Color(1, 1, 1, 0.12f));
                GUI.Label(new Rect(ix, cy, iw * 0.24f, lh), $"<size=11><color=#9fb4cc>{Loc.G("time_col")}</color></size>", _dim);
                GUI.Label(new Rect(ix + iw * 0.24f, cy, iw * 0.30f, lh), $"<size=11><color=#9fb4cc>{Loc.G("stage_col")}</color></size>", _dim);
                GUI.Label(new Rect(ix + iw * 0.54f, cy, iw * 0.46f, lh), $"<size=11><color=#9fb4cc>{Loc.G("boxes")}</color></size>", _dim);
                cy += lh;

                // log rows (newest first), scrollable within the resizable list viewport
                float listTop = cy;
                int maxFirst = Mathf.Max(0, n - visible);
                int first = Mathf.Clamp(Mathf.RoundToInt(_scrollY / lh), 0, maxFirst);
                _scrollY = first * lh;
                for (int r = 0; r < visible && (first + r) < n; r++)
                {
                    int i = first + r;
                    float ry = listTop + r * lh;
                    var e = ev[n - 1 - i];
                    if ((i & 1) == 1) DrawRect(ix, ry, iw, lh, new Color(1, 1, 1, 0.03f));
                    GUI.Label(new Rect(ix, ry, iw * 0.24f, lh), $"<color=#aeb6c2>{e.Time:HH:mm:ss}</color>", _tiny);
                    GUI.Label(new Rect(ix + iw * 0.24f, ry, iw * 0.30f, lh), $"<color=#c8a24a>{LocalizeStage(e.Stage)}</color>", _tiny);
                    string nameColor = IsBoss(e.Type) ? "#7FB2FF" : "#eaf3ee";   // boss boxes in blue
                    GUI.Label(new Rect(ix + iw * 0.54f, ry, iw * 0.46f, lh), $"<color={nameColor}>{e.Type}</color>", _tiny);
                }
                if (n == 0) GUI.Label(new Rect(ix, listTop, iw, lh), $"<color=#8a93a0>{Loc.G("box_empty")}</color>", _tiny);
                // scrollbar when more rows than fit
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

        private void SaveBoxSize() { Plugin.BoxPanelWidth.Value = _rect.width; Plugin.BoxPanelHeight.Value = _listH; }

        private void ApplyVolume(float mouseX)
        {
            float t = Mathf.Clamp01((mouseX - _volRect.x) / Mathf.Max(1f, _volRect.width));
            if (Mathf.Abs(t - Plugin.BoxSoundVolume.Value) > 0.005f) Plugin.BoxSoundVolume.Value = t;
        }

        private static bool IsBoss(string name) =>
            !string.IsNullOrEmpty(name) && name.IndexOf("Boss", StringComparison.OrdinalIgnoreCase) >= 0;

        private static string LocalizeStage(string stage)
        {
            if (string.IsNullOrEmpty(stage)) return "?";
            int sp = stage.LastIndexOf(' ');
            if (sp > 0) { string loc = Loc.G(stage.Substring(sp + 1)); if (loc != stage.Substring(sp + 1)) return stage.Substring(0, sp) + " " + loc; }
            return stage;
        }
    }
}
