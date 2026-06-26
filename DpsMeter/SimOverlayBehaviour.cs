using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (hub-only, no hotkey): the clear-time what-if simulator. Per-class DPS
    /// multipliers + a global speed multiplier re-scale every stage's clear time using FarmPlanner's
    /// two-part model (per-HP throughput vs per-wave overhead). Read-only over RunStore + wiki data +
    /// the live per-character damage shares. Toggled from the F1 control center.</summary>
    public class SimOverlayBehaviour : MonoBehaviour
    {
        public SimOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const int Slot = 11;          // InputCompat panel slot (unique; see MaxPanels)
        private const float Pad = 10f;
        private const double MultStep = 0.10;
        private const double MultMin = 1.0;
        private const double MultMax = 5.0;

        private Rect _rect = new Rect(80, 80, 520, 0);
        private bool _visible, _placed;
        private float _wantX, _wantY;
        private Vector2 _dragOffset;
        private bool _dragging;

        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box, _col;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;

        private readonly List<EfficiencyRow> _rows = new List<EfficiencyRow>();
        private Calibration _calib = new Calibration();
        private int _seenVersion = -1;
        private bool _loaded;

        // run data + per-stage measured timing (有效輸出/停輸出), precomputed on Reload
        private List<RunRecord> _runs = new List<RunRecord>();
        private string _buildSig;
        private readonly Dictionary<string, StageTiming> _timing = new Dictionary<string, StageTiming>();
        private double _avgActiveFrac = 1.0;   // global active-fraction, used to estimate un-cleared stages
        private int _measuredCount;            // stages with a real measured split (for the coverage hint)

        // per-class multiplier (heroKey -> ×); live damage shares (heroKey -> 0..1); display order (share desc)
        private readonly Dictionary<int, double> _mult = new Dictionary<int, double>();
        private readonly Dictionary<int, double> _shares = new Dictionary<int, double>();
        private readonly List<int> _order = new List<int>();
        private double _speed = 1.0;

        private string _diff = "NORMAL";
        private int _page;
        private int _sortPct;   // 0 = sort by saved seconds, 1 = by saved %

        private Rect _closeRect, _resetRect, _savedHdr, _savedPctHdr, _pagePrev, _pageNext, _speedMinus, _speedPlus;
        private readonly List<Rect> _heroMinus = new List<Rect>();
        private readonly List<Rect> _heroPlus = new List<Rect>();
        private readonly List<int> _heroKeyForRow = new List<int>();
        private readonly List<Rect> _chipRects = new List<Rect>();
        private readonly List<string> _chipKeys = new List<string>();
        private static readonly string[] Difficulties = { "NORMAL", "NIGHTMARE", "HELL", "TORMENT" };

        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();
        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        void Awake()
        {
            _rect.width = Mathf.Max(520, Plugin.SimPanelWidth.Value);
            _visible = Plugin.SimStartVisible.Value;
            PanelRegistry.Register("sim", 4, "⚡", () => Loc.G("sim_title"), KeyCode.None, () => _visible, v => _visible = v);
        }

        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.SimPosX.Value, py = Plugin.SimPosY.Value;
            if (px < 0 || py < 0) { _rect.x = Mathf.Max(24, (Screen.width - _rect.width) * 0.5f); _rect.y = 80f; }
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y;
            _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.SetPanel(Slot, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (_visible && RunStore.Version != _seenVersion) Reload();
                if (_visible) RefreshShares();
                if (_visible) HandlePointer();
                else if (_dragging) _dragging = false;
            }
            catch { }
        }

        /// <summary>Cache the latest non-empty per-class damage shares so the panel still works after the
        /// run ends / you return to town. Only heroes that dealt damage appear (benched heroes are absent).</summary>
        private void RefreshShares()
        {
            try
            {
                if (Plugin.Tracker == null) return;
                var snap = Plugin.Tracker.GetSnapshot(Time.time);
                if (snap.ByChar == null || snap.ByChar.Count == 0) return;
                _shares.Clear();
                _order.Clear();
                foreach (var c in snap.ByChar)
                {
                    _shares[c.CharKey] = c.Share;
                    _order.Add(c.CharKey);
                }
            }
            catch { }
        }

        private void Reload()
        {
            _seenVersion = RunStore.Version;
            _rows.Clear();
            int curLevel = 0;
            try { SaveGearReader.ReadParty(); foreach (var lv in SaveGearReader.LastHeroLevels.Values) if (lv > curLevel) curLevel = lv; } catch { }
            _runs = RunStore.LoadAll();
            _rows.AddRange(FarmPlanner.Rank(FarmDataStore.Stages, _runs, out _calib, curLevel));

            // current build = newest run with a loadout (mirrors FarmPlanner's current-build pick)
            _buildSig = null;
            foreach (var r in _runs) { var sg = FarmPlanner.BuildSignature(r); if (!string.IsNullOrEmpty(sg)) _buildSig = sg; }

            // precompute each stage's measured active/idle split for the current build; also a global
            // active-fraction used to estimate stages we haven't cleared on this build.
            _timing.Clear();
            _measuredCount = 0;
            double sumActive = 0, sumTotal = 0;
            foreach (var st in FarmDataStore.Stages)
            {
                var tt = ClearTimeSim.AggregateTiming(_runs, st.StageId, _buildSig);
                if (!tt.HasData) continue;
                _timing[st.StageId] = tt;
                _measuredCount++;
                sumActive += tt.ActiveSec;
                sumTotal += tt.ActiveSec + tt.IdleSec;
            }
            _avgActiveFrac = sumTotal > 0 ? sumActive / sumTotal : 1.0;
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

        private void AdjustMult(int key, double delta)
        {
            double cur;
            if (!_mult.TryGetValue(key, out cur) || cur <= 0) cur = MultMin;
            cur = Math.Round((cur + delta) * 100) / 100.0;
            if (cur < MultMin) cur = MultMin;
            if (cur > MultMax) cur = MultMax;
            _mult[key] = cur;
        }

        private double MultOf(int key)
        {
            double cur;
            if (!_mult.TryGetValue(key, out cur) || cur <= 0) return MultMin;
            return cur;
        }

        private void ResetMults()
        {
            _mult.Clear();
            _speed = 1.0;
        }

        private static double ClampSpeed(double v)
        {
            v = Math.Round(v * 100) / 100.0;
            if (v < MultMin) v = MultMin;
            if (v > MultMax) v = MultMax;
            return v;
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(Slot); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(Slot, m, ref rw, ref dh, 420f, Mathf.Max(420f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 520f; Plugin.SimPanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.SimPanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                if (_resetRect.Contains(m)) { ResetMults(); return; }
                if (_speedMinus.Contains(m)) { _speed = ClampSpeed(_speed - MultStep); return; }
                if (_speedPlus.Contains(m)) { _speed = ClampSpeed(_speed + MultStep); return; }
                for (int i = 0; i < _heroMinus.Count && i < _heroKeyForRow.Count; i++)
                    if (_heroMinus[i].Contains(m)) { AdjustMult(_heroKeyForRow[i], -MultStep); return; }
                for (int i = 0; i < _heroPlus.Count && i < _heroKeyForRow.Count; i++)
                    if (_heroPlus[i].Contains(m)) { AdjustMult(_heroKeyForRow[i], MultStep); return; }
                if (_savedHdr.Contains(m)) { _sortPct = 0; _page = 0; return; }
                if (_savedPctHdr.Contains(m)) { _sortPct = 1; _page = 0; return; }
                if (_pagePrev.Contains(m)) { _page = Mathf.Max(0, _page - 1); return; }
                if (_pageNext.Contains(m)) { _page++; return; }
                for (int i = 0; i < _chipRects.Count; i++)
                    if (_chipRects[i].Contains(m)) { _diff = _chipKeys[i]; _page = 0; return; }
                if (_rect.Contains(m) && InputCompat.ClaimDrag(Slot)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(Slot)) { _dragging = false; return; }
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased())
                {
                    _dragging = false;
                    _wantX = _rect.x; _wantY = _rect.y;
                    Plugin.SimPosX.Value = _rect.x;
                    Plugin.SimPosY.Value = _rect.y;
                }
            }
        }

        // ---------------- rendering ----------------

        private void EnsureAssets()
        {
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_bgTex == null)
            {
                _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f));
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

        private void DrawBar(float x, float y, float w, float h, double value, double max, Color c)
        {
            if (max <= 0 || value <= 0) return;
            float frac = Mathf.Clamp01((float)(value / max));
            DrawRect(x, y + 2, w * frac, h - 4, c);
        }

        // hero-class colour by class id (heroKey/100): Knight/Ranger/Sorcerer/Priest/…  Unknown -> grey.
        private static Color ClassColor(int heroKey)
        {
            switch (heroKey / 100)
            {
                case 1: return new Color(0.90f, 0.78f, 0.35f);
                case 2: return new Color(0.40f, 0.86f, 0.46f);
                case 3: return new Color(0.55f, 0.60f, 0.97f);
                case 4: return new Color(0.95f, 0.62f, 0.40f);
            }
            return new Color(0.6f, 0.64f, 0.7f);
        }

        void OnGUI()
        {
            if (!_visible || GameUiState.MenuOpen()) return;
            GUI.depth = -12;
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
                double F = ClearTimeSim.PartyDpsFactor(_shares, _mult);

                // simulate every filtered stage; build a sort order (saved sec or % desc, unknowns last)
                int nF = filtered.Count;
                var sims = new List<SimRow>(nF);
                var metric = new double[nF];
                var idx = new int[nF];
                for (int i = 0; i < nF; i++)
                {
                    var er = filtered[i];
                    StageTiming tt;
                    SimRow sr;
                    if (_timing.TryGetValue(er.Stage.StageId, out tt))
                        sr = ClearTimeSim.SimulateSplit(tt.ActiveSec, tt.IdleSec, F, _speed);  // measured 有效/停輸出
                    else
                        sr = ClearTimeSim.SimulateFallback(er.ClearSec, _avgActiveFrac, F, _speed);  // estimated
                    sims.Add(sr);
                    metric[i] = sr.BaseClear <= 0 ? -1e18 : (_sortPct == 1 ? sr.SavedPct : sr.SavedSec);
                    idx[i] = i;
                }
                SortDescByMetric(idx, metric);
                var summary = ClearTimeSim.Summarize(sims, F);

                int heroLines = _order.Count > 0 ? _order.Count : 1;
                bool noTiming = _measuredCount == 0;
                float headerStack = lh /*title*/ + lh /*note*/ + (noTiming ? lh : 0)
                    + heroLines * lh + lh /*speed*/ + lh /*summary*/ + lh /*chips*/ + lh /*tableHeader*/;
                int rowsPerPage = Mathf.Clamp((int)((Screen.height - 150 - headerStack) / lh), 4, 80);
                int pages = Mathf.Max(1, (nF + rowsPerPage - 1) / rowsPerPage);
                _page = Mathf.Clamp(_page, 0, pages - 1);
                int start = _page * rowsPerPage;
                int shown = Mathf.Min(rowsPerPage, nF - start);

                float bodyH = headerStack + lh * Mathf.Max(shown, 1) + lh /*footer*/;
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

                // title + reset + close
                float resetW = Mathf.Max(60f, _btn.CalcSize(new GUIContent(Loc.G("sim_reset"))).x + 12f);
                GUI.Label(new Rect(ix, cy, w - Pad - resetW - 30 - ix + x, lh), Loc.G("sim_title"), _title);
                _resetRect = new Rect(x + w - 28 - resetW, cy - 1, resetW, lh);
                GUI.Button(_resetRect, Loc.G("sim_reset"), _btn);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh);
                GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                // note (the honest caveat)
                GUI.Label(new Rect(ix, cy, iw, lh), $"<size=10><color=#8a93a0>{Loc.G("sim_note")}</color></size>", _tiny);
                cy += lh;

                // no measured timing yet -> everything is estimated
                if (noTiming)
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<size=11><color=#e7c25a>⚠ {Loc.G("sim_need_model")}</color></size>", _tiny);
                    cy += lh;
                }

                // per-class multiplier rows (or a prompt when no shares are known yet)
                _heroMinus.Clear(); _heroPlus.Clear(); _heroKeyForRow.Clear();
                float ctrlW = 26f, valW = 52f;
                float plusX = ix + iw - ctrlW;
                float valX = plusX - valW;
                float minusX = valX - ctrlW;
                if (_order.Count == 0)
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#8a93a0>{Loc.G("sim_need_party")}</color>", _label);
                    cy += lh;
                }
                else
                {
                    for (int i = 0; i < _order.Count; i++)
                    {
                        int key = _order[i];
                        double share = 0; _shares.TryGetValue(key, out share);
                        double mu = MultOf(key);
                        DrawRect(ix, cy + lh * 0.32f, 9, 9, ClassColor(key));
                        string nm = HeroProbe.HeroName(key);
                        GUI.Label(new Rect(ix + 14, cy, minusX - (ix + 14), lh),
                            $"{nm} <size=10><color=#9aa3b0>{share * 100:0.#}%</color></size>", _label);
                        var rMinus = new Rect(minusX, cy, ctrlW, lh - 2);
                        var rPlus = new Rect(plusX, cy, ctrlW, lh - 2);
                        GUI.Button(rMinus, "−", _btn);
                        GUI.Button(rPlus, "+", _btn);
                        string mc = mu > 1.0001 ? "#7fffa0" : "#cdd5df";
                        GUI.Label(new Rect(valX, cy, valW, lh), $"<color={mc}>×{mu:0.00}</color>", _col);
                        _heroMinus.Add(rMinus); _heroPlus.Add(rPlus); _heroKeyForRow.Add(key);
                        cy += lh;
                    }
                }

                // global speed row
                GUI.Label(new Rect(ix + 14, cy, minusX - (ix + 14), lh), $"<color=#bcd0ea>{Loc.G("sim_speed")}</color>", _label);
                _speedMinus = new Rect(minusX, cy, ctrlW, lh - 2);
                _speedPlus = new Rect(plusX, cy, ctrlW, lh - 2);
                GUI.Button(_speedMinus, "−", _btn);
                GUI.Button(_speedPlus, "+", _btn);
                string sc = _speed > 1.0001 ? "#7fffa0" : "#cdd5df";
                GUI.Label(new Rect(valX, cy, valW, lh), $"<color={sc}>×{_speed:0.00}</color>", _col);
                cy += lh;

                // summary: party DPS factor + average saved % + most-improved stage
                string best = "";
                if (summary.BestIndex >= 0 && summary.BestIndex < nF)
                    best = $"　<color=#9fb4cc>{Loc.G("sim_best")}</color> <color=#eaf3ee>{filtered[summary.BestIndex].Stage.Label}</color> <color=#7fffa0>{FmtPct(sims[summary.BestIndex].SavedPct)}</color>";
                string cover = $"　<size=10><color=#7f8a99>{Loc.G("src_measured")}{_measuredCount}</color></size>";
                GUI.Label(new Rect(ix, cy, iw, lh),
                    $"<color=#9fb4cc>{Loc.G("sim_party_dps")}</color> <color=#eaf3ee>×{F:0.00}</color>　" +
                    $"<color=#9fb4cc>{Loc.G("sim_avg_saved")}</color> <color=#7fffa0>{FmtPct(summary.AvgSavedPct)}</color>{best}{cover}", _label);
                cy += lh;

                // difficulty chips
                _chipRects.Clear(); _chipKeys.Clear();
                float chipX = ix;
                foreach (var d in Difficulties)
                {
                    string lbl = Loc.G(d);
                    float cw = _btn.CalcSize(new GUIContent(lbl)).x + 10;
                    var cr = new Rect(chipX, cy, cw, lh - 2);
                    bool sel = _diff == d;
                    GUI.Label(cr, sel ? $"<b><color=#FFC857>[{lbl}]</color></b>" : $"<color=#9fb4cc>{lbl}</color>", _dim);
                    _chipRects.Add(cr); _chipKeys.Add(d);
                    chipX += cw + 6;
                }
                cy += lh;

                // table header
                const float CG = 8f;
                float wName = iw * 0.30f, wBase = iw * 0.15f, wNew = iw * 0.15f, wSaved = iw * 0.16f, wPct = iw * 0.24f;
                float xName = ix;
                float xBase = xName + wName;
                float xNew = xBase + wBase;
                float xSaved = xNew + wNew;
                float xPct = xSaved + wSaved;
                string arrow = " ▼";
                GUI.Label(new Rect(xName, cy, wName, lh), $"<color=#9fb4cc>{Loc.G("stage_col")}</color>", _dim);
                GUI.Label(new Rect(xBase, cy, wBase - CG, lh), $"<color=#9fb4cc>{Loc.G("sim_base")}</color>", _col);
                GUI.Label(new Rect(xNew, cy, wNew - CG, lh), $"<color=#9fb4cc>{Loc.G("sim_new")}</color>", _col);
                _savedHdr = new Rect(xSaved, cy, wSaved, lh);
                _savedPctHdr = new Rect(xPct, cy, wPct, lh);
                GUI.Label(new Rect(xSaved, cy, wSaved - CG, lh), $"<color={(_sortPct == 0 ? "#FFC857" : "#9fb4cc")}>{Loc.G("sim_saved")}{(_sortPct == 0 ? arrow : "")}</color>", _col);
                GUI.Label(new Rect(xPct, cy, wPct - CG, lh), $"<color={(_sortPct == 1 ? "#FFC857" : "#9fb4cc")}>{Loc.G("sim_savedpct")}{(_sortPct == 1 ? arrow : "")}</color>", _col);
                cy += lh;
                DrawRect(ix, cy - 1, iw, 1, new Color(1, 1, 1, 0.12f));

                string langCode = Loc.WikiLangCode();
                string curStage = "";
                try { curStage = CharacterReader.CurrentStageId(); } catch { }

                // bar scale: max saved% across the filtered set so bars stay comparable across pages
                double maxPct = 0;
                for (int i = 0; i < nF; i++) if (sims[i].SavedPct > maxPct) maxPct = sims[i].SavedPct;

                for (int k = 0; k < shown; k++)
                {
                    int fi = idx[start + k];
                    var r = filtered[fi];
                    var sr = sims[fi];
                    bool isCur = !string.IsNullOrEmpty(curStage) && r.Stage.StageId == curStage;
                    if (isCur) DrawRect(ix, cy, iw, lh, new Color(0.30f, 0.45f, 0.75f, 0.30f));
                    else if ((k & 1) == 1) DrawRect(ix, cy, iw, lh, new Color(1, 1, 1, 0.03f));

                    string diffLoc = Loc.G(r.Stage.Difficulty);
                    string estTag = _timing.ContainsKey(r.Stage.StageId) ? "" : $" <size=9><color=#7f8a99>{Loc.G("src_estimated")}</color></size>";
                    string nameCol = $"<b>{r.Stage.Label}</b> <size=10><color=#c8a24a>{diffLoc}</color></size>{estTag} <color=#9aa3b0><size=10>{r.Stage.LocalizedName(langCode)}</size></color>";
                    GUI.Label(new Rect(xName, cy, wName, lh), nameCol, _label);

                    if (sr.BaseClear > 0)
                    {
                        GUI.Label(new Rect(xBase, cy, wBase - CG, lh), $"<color=#aeb6c2>{FmtTime(sr.BaseClear)}</color>", _col);
                        GUI.Label(new Rect(xNew, cy, wNew - CG, lh), $"<color=#eaf3ee>{FmtTime(sr.NewClear)}</color>", _col);
                        string svCol = sr.SavedSec > 0.5 ? "#7fffa0" : "#8a93a0";
                        GUI.Label(new Rect(xSaved, cy, wSaved - CG, lh), $"<color={svCol}>{FmtSaved(sr.SavedSec)}</color>", _col);
                        DrawBar(xPct, cy, wPct, lh, sr.SavedPct, maxPct, new Color(0.5f, 1f, 0.63f, 0.40f));
                        GUI.Label(new Rect(xPct, cy, wPct - CG, lh), $"<color={svCol}>{FmtPct(sr.SavedPct)}</color>", _col);
                    }
                    else
                    {
                        GUI.Label(new Rect(xBase, cy, wBase - CG, lh), "<color=#5a6675>—</color>", _col);
                        GUI.Label(new Rect(xNew, cy, wNew - CG, lh), "<color=#5a6675>—</color>", _col);
                        GUI.Label(new Rect(xSaved, cy, wSaved - CG, lh), "<color=#5a6675>—</color>", _col);
                        GUI.Label(new Rect(xPct, cy, wPct - CG, lh), "<color=#5a6675>—</color>", _col);
                    }
                    cy += lh;
                }

                // footer: page nav + counts
                _pagePrev = new Rect(ix, cy, 26, lh - 2);
                _pageNext = new Rect(ix + 30, cy, 26, lh - 2);
                GUI.Button(_pagePrev, "◀", _btn);
                GUI.Button(_pageNext, "▶", _btn);
                GUI.Label(new Rect(ix + 64, cy, iw - 64, lh), $"<size=11><color=#9fb4cc>{_page + 1}/{pages}　{nF} {Loc.G("stage_col")}</color></size>", _dim);
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        /// <summary>Insertion sort of an index array by a parallel metric, descending. n is tiny (≤27 per
        /// difficulty) so O(n²) is free; avoids a comparator lambda inside the injected MonoBehaviour.</summary>
        private static void SortDescByMetric(int[] idx, double[] metric)
        {
            for (int i = 1; i < idx.Length; i++)
            {
                int cur = idx[i];
                double mv = metric[cur];
                int j = i - 1;
                while (j >= 0 && metric[idx[j]] < mv) { idx[j + 1] = idx[j]; j--; }
                idx[j + 1] = cur;
            }
        }

        /// <summary>Seconds as m:ss (e.g. 307 -> 5:07), like the wiki / farm panel.</summary>
        private static string FmtTime(double sec)
        {
            int s = (int)Math.Round(sec);
            return (s / 60).ToString() + ":" + (s % 60).ToString("00");
        }

        /// <summary>Saved seconds as a signed compact string (e.g. -56s).</summary>
        private static string FmtSaved(double sec)
        {
            int s = (int)Math.Round(sec);
            return (s > 0 ? "-" : "") + s + "s";
        }

        private static string FmtPct(double frac)
        {
            return (frac * 100).ToString("0.#") + "%";
        }
    }
}
