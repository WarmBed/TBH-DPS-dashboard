using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>Loads the wiki farm dataset embedded in the DLL, once.</summary>
    internal static class FarmDataStore
    {
        private static List<FarmStage> _stages;
        public static List<FarmStage> Stages
        {
            get
            {
                if (_stages != null) return _stages;
                _stages = new List<FarmStage>();
                try
                {
                    var asm = typeof(FarmDataStore).Assembly;
                    string name = null;
                    foreach (var n in asm.GetManifestResourceNames())
                        if (n.EndsWith("farm_stages.json", StringComparison.OrdinalIgnoreCase)) { name = n; break; }
                    if (name != null)
                        using (var s = asm.GetManifestResourceStream(name))
                        using (var r = new System.IO.StreamReader(s))
                            _stages = FarmDataLoader.Parse(r.ReadToEnd());
                    Plugin.Logger?.LogInfo($"[farm] loaded {_stages.Count} stages from embedded data");
                }
                catch (Exception e) { Plugin.Logger?.LogWarning("FarmDataStore: " + e.Message); }
                return _stages;
            }
        }
    }

    /// <summary>IMGUI overlay (F6): the farming-efficiency planner. Ranks every stage by gold/sec and
    /// per-hero exp/sec, using the player's own measured clears where available and the wiki baseline
    /// scaled by the player's learned personal multiplier elsewhere. Read-only over RunStore + wiki data.</summary>
    public class FarmOverlayBehaviour : MonoBehaviour
    {
        public FarmOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const float Pad = 10f;

        private Rect _rect = new Rect(60, 60, 480, 0);
        private bool _visible;
        private bool _placed;
        private float _wantX, _wantY;   // intended position; clamped non-destructively (resize-safe)
        private Vector2 _dragOffset;
        private bool _dragging;

        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box, _col;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;   // font sizes the styles were last built with (live-rebuild on change)

        private readonly List<EfficiencyRow> _rows = new List<EfficiencyRow>();
        private Calibration _calib = new Calibration();
        private int _seenVersion = -1;
        private bool _loaded;

        private FarmSortKey _sort = FarmSortKey.ExpPerSec;
        private string _diff = "";          // "" = all difficulties
        private int _page;

        private Rect _closeRect, _handleRect, _goldHdr, _expHdr, _clearHdr, _pagePrev, _pageNext, _resetRect;
        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();
        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);
        private bool _confirmReset;
        private readonly List<Rect> _chipRects = new List<Rect>();
        private readonly List<string> _chipKeys = new List<string>();
        private static readonly string[] Difficulties = { "", "NORMAL", "NIGHTMARE", "HELL", "TORMENT" };

        void Awake()
        {
            _rect.width = Mathf.Max(520, Plugin.FarmPanelWidth.Value);
            _visible = Plugin.FarmStartVisible.Value;
            PanelRegistry.Register("farm", 3, "⟳", () => Loc.G("farm_title"), Plugin.FarmToggleKey, () => _visible, v => _visible = v);
        }

        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.FarmPosX.Value, py = Plugin.FarmPosY.Value;
            if (px < 0 || py < 0) { _rect.x = Mathf.Max(24, (Screen.width - _rect.width) * 0.5f); _rect.y = 60f; }
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y;
            _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.SetPanel(3, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.FarmToggleKey))
                {
                    _visible = !_visible;
                    if (_visible) Reload();
                }
                if (_visible && RunStore.Version != _seenVersion) Reload();
                if (_visible) HandlePointer();
                else if (_dragging) _dragging = false;
            }
            catch { }
        }

        private void Reload()
        {
            _seenVersion = RunStore.Version;
            _rows.Clear();
            // read current hero levels from the save so retention works even before a fresh run
            int curLevel = 0;
            try { SaveGearReader.ReadParty(); foreach (var lv in SaveGearReader.LastHeroLevels.Values) if (lv > curLevel) curLevel = lv; } catch { }
            // Calibrate against the build of your MOST RECENT run (Rank's default when no sig is passed),
            // not a live capture — a live capture taken while you're reshuffling the party is unstable and
            // would strand the planner at ×1.00. Your last actually-cleared run reflects your real build,
            // so after any party/gear change it self-recovers as soon as you clear one stage.
            _rows.AddRange(FarmPlanner.Rank(FarmDataStore.Stages, RunStore.LoadAll(), out _calib, curLevel));
            FarmPlanner.Sort(_rows, _sort);
            _loaded = true;
            _page = 0;
        }

        private List<EfficiencyRow> Filtered()
        {
            if (string.IsNullOrEmpty(_diff)) return _rows;
            var l = new List<EfficiencyRow>();
            foreach (var r in _rows) if (r.Stage.Difficulty == _diff) l.Add(r);
            return l;
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(3); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            // resize grip (bottom-right): width only
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(3, m, ref rw, ref dh, 420f, Mathf.Max(420f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 520f; Plugin.FarmPanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.FarmPanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; _confirmReset = false; return; }
                if (_resetRect.Contains(m))
                {
                    if (_confirmReset) { RunStore.DeleteAll(); _confirmReset = false; _page = 0; Reload(); }
                    else _confirmReset = true;
                    return;
                }
                _confirmReset = false;   // any other click cancels a pending reset
                if (_goldHdr.Contains(m)) { SetSort(FarmSortKey.GoldPerSec); return; }
                if (_expHdr.Contains(m)) { SetSort(FarmSortKey.ExpPerSec); return; }
                if (_clearHdr.Contains(m)) { SetSort(FarmSortKey.ClearSec); return; }
                if (_pagePrev.Contains(m)) { _page = Mathf.Max(0, _page - 1); return; }
                if (_pageNext.Contains(m)) { _page++; return; }
                for (int i = 0; i < _chipRects.Count; i++)
                    if (_chipRects[i].Contains(m)) { _diff = _chipKeys[i]; _page = 0; return; }
                if (_rect.Contains(m) && InputCompat.ClaimDrag(3)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(3)) { _dragging = false; return; }   // a panel on top stole the press
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased())
                {
                    _dragging = false;
                    _wantX = _rect.x; _wantY = _rect.y;
                    Plugin.FarmPosX.Value = _rect.x;
                    Plugin.FarmPosY.Value = _rect.y;
                }
            }
        }

        private void SetSort(FarmSortKey k) { _sort = k; FarmPlanner.Sort(_rows, k); _page = 0; }

        // ---------------- rendering ----------------

        private void EnsureAssets()
        {
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f));   // solid black
                _bgTex.Apply();
                if (_box != null) _box.normal.background = _bgTex;
            }
            int fs = Plugin.FontSize.Value, fsm = Plugin.FontSizeSmall.Value;
            if (_stylesReady && _builtFs == fs && _builtFsm == fsm) return;
            _builtFs = fs; _builtFsm = fsm;
            _title = new GUIStyle { fontSize = fs, fontStyle = FontStyle.Bold, richText = true };
            _title.normal.textColor = new Color(1f, 0.86f, 0.35f);
            _label = new GUIStyle { fontSize = fs, richText = true };
            _label.normal.textColor = new Color(0.93f, 0.93f, 0.93f);
            _dim = new GUIStyle { fontSize = fsm, richText = true };
            _dim.normal.textColor = new Color(0.78f, 0.84f, 0.95f);
            _tiny = new GUIStyle { fontSize = Mathf.Max(9, fsm - 2), richText = true };
            _tiny.normal.textColor = new Color(0.7f, 0.75f, 0.85f);
            _col = new GUIStyle { fontSize = fs, richText = true, alignment = TextAnchor.MiddleRight };
            _col.normal.textColor = Color.white;
            _btn = new GUIStyle(GUI.skin.button) { fontSize = fsm, fontStyle = FontStyle.Bold, richText = true };
            _box = new GUIStyle(); _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _label, _dim, _tiny, _col, _btn);
            _stylesReady = true;
        }

        private void DrawRect(float x, float y, float w, float h, Color c)
        {
            var prev = GUI.color; GUI.color = c;
            GUI.DrawTexture(new Rect(x, y, w, h), _white);
            GUI.color = prev;
        }

        /// <summary>Horizontal value bar behind a number cell, length proportional to value/max.</summary>
        private void DrawBar(float x, float y, float w, float h, double value, double max, Color c)
        {
            if (max <= 0 || value <= 0) return;
            float frac = (float)Mathf.Clamp01((float)(value / max));
            DrawRect(x, y + 2, w * frac, h - 4, c);
        }

        void OnGUI()
        {
            if (!_visible || GameUiState.MenuOpen()) return;   // hide while a game menu is open
            GUI.depth = -12;   // on top of F9/F10/F11
            var prevM = GUI.matrix;
            try
            {
                EnsureAssets();
                if (!_placed) PlaceDefault();
                if (!_loaded) Reload();

                int fs = Plugin.FontSize.Value;
                float lh = fs + 6;
                float x = _rect.x, ix = x + Pad, w = _rect.width, iw = w - Pad * 2;

                var filtered = Filtered();
                // how many rows fit on screen
                int rowsPerPage = Mathf.Clamp((int)((Screen.height - 220) / lh), 6, 80);
                int pages = Mathf.Max(1, (filtered.Count + rowsPerPage - 1) / rowsPerPage);
                _page = Mathf.Clamp(_page, 0, pages - 1);
                int start = _page * rowsPerPage;
                int shown = Mathf.Min(rowsPerPage, filtered.Count - start);

                float bodyH = lh /*title*/ + lh /*note*/ + (_calib.HasData ? lh : 0) /*basis*/ + lh /*chips*/ + lh /*header*/ + lh * Mathf.Max(shown, 1) + lh /*footer*/;
                float h = Pad + bodyH + Pad;
                _rect.height = h;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                if (!_dragging)
                {
                    _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale));
                    _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale));
                }
                x = _rect.x; ix = x + Pad;
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                float cy = _rect.y + Pad;
                _handleRect = new Rect(x, _rect.y, w, lh);

                // title + your-multiplier + reset + close
                string mult = _calib.HasData ? $"  <size=11><color=#9fb4cc>{Loc.G("your_mult")} ×{_calib.MGold:0.00}</color></size>" : "";
                float resetW = Mathf.Max(60f, _btn.CalcSize(new GUIContent(_confirmReset ? Loc.G("confirm_reset") : Loc.G("reset_all"))).x + 12f);
                GUI.Label(new Rect(ix, cy, w - Pad - resetW - 30 - ix + x, lh), $"{Loc.G("farm_title")}{mult}", _title);
                _resetRect = new Rect(x + w - 28 - resetW, cy - 1, resetW, lh);
                GUI.Button(_resetRect, _confirmReset ? Loc.G("confirm_reset") : Loc.G("reset_all"), _btn);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh);
                GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                string note = _calib.Stale
                    ? $"<color=#e7c25a>⚠ {Loc.G("farm_stale")}</color>"
                    : $"<color=#8a93a0>{Loc.G("farm_note")}</color>";
                GUI.Label(new Rect(ix, cy, iw, lh), $"<size=10>{note}</size>", _tiny);
                cy += lh;

                // basis summary: what the current calibration is built on (runs / stages / level / build state)
                if (_calib.HasData)
                {
                    string buildState = _calib.Stale
                        ? $"<color=#e7c25a>{Loc.G("old_build")}</color>"
                        : $"<color=#5fd07c>{Loc.G("cur_build")}</color>";
                    string basis = $"<color=#9fb4cc>{Loc.G("basis")}:</color> " +
                        $"<color=#cdd5df>{_calib.TrustedRuns}</color> {Loc.G("runs")} · " +
                        $"<color=#cdd5df>{_calib.MeasuredStages}</color> {Loc.G("stage_col")} · " +
                        $"Lv<color=#cdd5df>{_calib.HeroLevel}</color> · {buildState}";
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<size=11>{basis}</size>", _tiny);
                    cy += lh;
                }

                // difficulty filter chips
                _chipRects.Clear(); _chipKeys.Clear();
                float chipX = ix;
                foreach (var d in Difficulties)
                {
                    string lbl = string.IsNullOrEmpty(d) ? "ALL" : Loc.G(d);
                    float cw = _btn.CalcSize(new GUIContent(lbl)).x + 10;
                    var cr = new Rect(chipX, cy, cw, lh - 2);
                    bool sel = _diff == d;
                    GUI.Label(cr, sel ? $"<b><color=#FFC857>[{lbl}]</color></b>" : $"<color=#9fb4cc>{lbl}</color>", _dim);
                    _chipRects.Add(cr); _chipKeys.Add(d);
                    chipX += cw + 6;
                }
                cy += lh;

                // column geometry (non-overlapping). gold/exp cells host an inline bar behind the number.
                // CG = inner gap so right-aligned numbers never touch the next column.
                const float CG = 8f;
                float wName = iw * 0.29f, wGold = iw * 0.19f, wExp = iw * 0.19f, wRet = iw * 0.11f, wClear = iw * 0.11f, wSrc = iw * 0.11f;
                float xName = ix;
                float xGold = xName + wName;
                float xExp = xGold + wGold;
                float xRet = xExp + wExp;
                float xClear = xRet + wRet;
                float xSrc = xClear + wClear;
                string arrow = " ▼";
                GUI.Label(new Rect(xName, cy, wName, lh), $"<color=#9fb4cc>{Loc.G("stage_col")}</color>", _dim);
                _goldHdr = new Rect(xGold, cy, wGold, lh);
                _expHdr = new Rect(xExp, cy, wExp, lh);
                _clearHdr = new Rect(xClear, cy, wClear, lh);
                GUI.Label(new Rect(xGold, cy, wGold - CG, lh), $"<color={(_sort == FarmSortKey.GoldPerSec ? "#FFC857" : "#9fb4cc")}>{Loc.G("gold")}/s{(_sort == FarmSortKey.GoldPerSec ? arrow : "")}</color>", _col);
                GUI.Label(new Rect(xExp, cy, wExp - CG, lh), $"<color={(_sort == FarmSortKey.ExpPerSec ? "#FFC857" : "#9fb4cc")}>{Loc.G("exp")}/s{(_sort == FarmSortKey.ExpPerSec ? arrow : "")}</color>", _col);
                GUI.Label(new Rect(xRet, cy, wRet - CG, lh), $"<size=11><color=#9fb4cc>{Loc.G("retention")}</color></size>", _col);
                GUI.Label(new Rect(xClear, cy, wClear - CG, lh), $"<color={(_sort == FarmSortKey.ClearSec ? "#FFC857" : "#9fb4cc")}>{Loc.G("clear_sec")}{(_sort == FarmSortKey.ClearSec ? arrow : "")}</color>", _col);
                GUI.Label(new Rect(xSrc + 2, cy, wSrc - 2, lh), $"<color=#9fb4cc>{Loc.G("source_col")}</color>", _dim);
                cy += lh;
                DrawRect(ix, cy - 1, iw, 1, new Color(1, 1, 1, 0.12f));

                string curStage = "";
                try { curStage = CharacterReader.CurrentStageId(); } catch { }

                // bar scale: max over the whole filtered set so bars stay comparable across pages
                double maxGold = 0, maxExp = 0;
                foreach (var r in filtered) { if (r.GoldPerSec > maxGold) maxGold = r.GoldPerSec; if (r.ExpPerSec > maxExp) maxExp = r.ExpPerSec; }

                string langCode = Loc.WikiLangCode();
                for (int i = 0; i < shown; i++)
                {
                    var r = filtered[start + i];
                    bool isCur = !string.IsNullOrEmpty(curStage) && r.Stage.StageId == curStage;
                    if (isCur) DrawRect(ix, cy, iw, lh, new Color(0.30f, 0.45f, 0.75f, 0.30f));
                    else if ((i & 1) == 1) DrawRect(ix, cy, iw, lh, new Color(1, 1, 1, 0.03f));

                    string diffLoc = Loc.G(r.Stage.Difficulty);
                    string lvTag = r.Stage.Level > 0 ? $" <size=9><color=#7f8a99>L{r.Stage.Level}</color></size>" : "";
                    string nameCol = $"<b>{r.Stage.Label}</b> <size=10><color=#c8a24a>{diffLoc}</color></size>{lvTag} <color=#9aa3b0><size=10>{r.Stage.LocalizedName(langCode)}</size></color>";
                    GUI.Label(new Rect(xName, cy, wName, lh), nameCol, _label);

                    // inline bars (gold = amber, exp = teal); brighter when measured
                    DrawBar(xGold, cy, wGold, lh, r.GoldPerSec, maxGold, r.Measured ? new Color(0.95f, 0.78f, 0.30f, 0.55f) : new Color(0.80f, 0.66f, 0.28f, 0.28f));
                    DrawBar(xExp, cy, wExp, lh, r.ExpPerSec, maxExp, r.Measured ? new Color(0.37f, 0.82f, 0.78f, 0.55f) : new Color(0.34f, 0.66f, 0.64f, 0.28f));

                    // bright = current-build measured; amber = measured but from old gear; dim = estimated
                    string mc = r.MeasuredFromOldBuild ? "#e7c25a" : (r.Measured ? "#eaf3ee" : "#cdd5df");
                    GUI.Label(new Rect(xGold, cy, wGold - CG, lh), $"<color={mc}>{FmtNum(r.GoldPerSec)}</color>", _col);
                    GUI.Label(new Rect(xExp, cy, wExp - CG, lh), $"<color={mc}>{FmtNum(r.ExpPerSec)}</color>", _col);
                    // exp retention %: only meaningful below 100%, colored amber<100% / red<50%
                    double pct = r.ExpRetention * 100.0;
                    string retStr = pct >= 99.5 ? "<color=#5a6675>100%</color>"
                        : $"<color={(pct < 50 ? "#ef6a5a" : "#e7c25a")}>{pct:0}%</color>";
                    GUI.Label(new Rect(xRet, cy, wRet - CG, lh), $"<size=11>{retStr}</size>", _col);
                    GUI.Label(new Rect(xClear, cy, wClear - CG, lh), $"<color=#aeb6c2>{(r.ClearSec > 0 ? FmtTime(r.ClearSec) : "—")}</color>", _col);
                    string src = r.MeasuredFromOldBuild ? $"<color=#e7c25a>{Loc.G("src_old")}</color>"
                        : (r.Measured ? $"<color=#5fd07c>{Loc.G("src_measured")}</color>" : $"<color=#8a93a0>{Loc.G("src_estimated")}</color>");
                    GUI.Label(new Rect(xSrc + 2, cy, wSrc - 2, lh), $"<size=10>{src}</size>", _tiny);
                    cy += lh;
                }

                // footer: page nav + counts
                _pagePrev = new Rect(ix, cy, 26, lh - 2);
                _pageNext = new Rect(ix + 30, cy, 26, lh - 2);
                GUI.Button(_pagePrev, "◀", _btn);
                GUI.Button(_pageNext, "▶", _btn);
                GUI.Label(new Rect(ix + 64, cy, iw - 64, lh), $"<size=11><color=#9fb4cc>{_page + 1}/{pages}　{filtered.Count} {Loc.G("stage_col")}</color></size>", _dim);
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        /// <summary>Seconds as m:ss (e.g. 307 -> 5:07), like the wiki.</summary>
        private static string FmtTime(double sec)
        {
            int s = (int)System.Math.Round(sec);
            return (s / 60).ToString() + ":" + (s % 60).ToString("00");
        }

        /// <summary>Compact number: 1.2K / 3.4M / 5.6B.</summary>
        private static string FmtNum(double v)
        {
            double a = Math.Abs(v);
            if (a >= 1e9) return (v / 1e9).ToString("0.##") + "B";
            if (a >= 1e6) return (v / 1e6).ToString("0.##") + "M";
            if (a >= 1e3) return (v / 1e3).ToString("0.##") + "K";
            if (a >= 1) return v.ToString("0.#");
            return v.ToString("0.###");
        }
    }
}
