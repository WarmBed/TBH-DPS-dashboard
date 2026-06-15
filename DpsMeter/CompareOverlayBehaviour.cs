using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (F11) that compares saved runs of the SAME stage against a baseline
    /// (fastest clear by default, or a manually pinned run). Two-column baseline | current view plus
    /// damage distribution, per-wave times, and gear/skill diffs. Read-only over RunStore data.</summary>
    public class CompareOverlayBehaviour : MonoBehaviour
    {
        public CompareOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const float Pad = 10f;

        private Rect _rect = new Rect(40, 40, 380, 0);
        private bool _visible;
        private float _opacity = 0.9f;
        private bool _placed;
        private float _wantX, _wantY;   // intended position; clamped non-destructively (resize-safe)

        private Vector2 _dragOffset;
        private bool _dragging;

        private Texture2D _white, _bgTex;
        private float _bgAlphaBaked = -1f;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box, _col;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;   // font sizes the styles were last built with (live-rebuild on change)

        private Rect _stagePrev, _stageNext, _runPrev, _runNext, _pinRect, _delRect, _closeRect, _handleRect, _resetAllRect;
        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();
        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);
        private bool _confirmReset;
        private readonly List<Rect> _charTabs = new List<Rect>();
        private int _charIndex;

        // chart points (clear-time trend): click a point to select that run for the detail below
        private readonly List<Rect> _pointRects = new List<Rect>();
        private readonly List<int> _pointRun = new List<int>();

        private List<RunRecord> _runs = new List<RunRecord>();
        private List<string> _stages = new List<string>();
        private readonly Dictionary<string, string> _pinned = new Dictionary<string, string>();
        private int _stageIndex;
        /// <summary>Stage id F11 is currently focused on; the loot-heatmap panel mirrors this so its
        /// clear-time trend follows F11's stage selection. Null until F11 has loaded runs.</summary>
        public static string ActiveStageId;
        /// <summary>True while the F11 panel is open. ActiveStageId is only a *live* selection then —
        /// when the panel is closed it goes stale, and followers should track the current stage instead.</summary>
        public static bool PanelOpen;
        private int _runIndex;
        private bool _loaded;
        private int _seenVersion = -1;

        void Awake()
        {
            _opacity = Mathf.Clamp01(Plugin.Opacity.Value + 0.15f);
            _rect.width = Mathf.Max(520, Plugin.ComparePanelWidth.Value);   // two-column layout needs width
            _visible = Plugin.CompareStartVisible.Value;
            PanelRegistry.Register("compare", 2, "⇄", () => Loc.G("compare_title"), Plugin.CompareToggleKey, () => _visible, v => _visible = v);
        }

        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.ComparePosX.Value, py = Plugin.ComparePosY.Value;
            if (px < 0 || py < 0) { _rect.x = Mathf.Max(24, (Screen.width - _rect.width) * 0.5f); _rect.y = 60f; }
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y;
            _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.Poll();
                InputCompat.SetPanel(2, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.CompareToggleKey))
                {
                    _visible = !_visible;
                    if (_visible) Reload();
                }
                // auto-refresh when a run is saved/deleted while the panel is open
                if (_visible && RunStore.Version != _seenVersion) Reload();
                PanelOpen = _visible;
                if (_visible) HandlePointer();
                else if (_dragging) _dragging = false;
            }
            catch { }
        }

        private void Reload()
        {
            _seenVersion = RunStore.Version;
            _runs = RunStore.LoadAll();
            var groups = StageCompare.GroupByStage(_runs);
            // order stages by the recency of their newest run (newest stage first)
            _stages = new List<string>(groups.Keys);
            _stages.Sort((a, b) => LastIndexOfStage(b).CompareTo(LastIndexOfStage(a)));
            _loaded = true;
            // default to the stage the player is CURRENTLY on (if we have runs for it);
            // otherwise fall back to the stage of the most recent run.
            _stageIndex = 0;
            string cur = "";
            try { cur = CharacterReader.CurrentStageId(); } catch { }
            if (!string.IsNullOrEmpty(cur))
            {
                int idx = _stages.IndexOf(cur);
                if (idx >= 0) _stageIndex = idx;
            }
            var g = CurrentGroup();
            _runIndex = g.Count - 1;
        }

        private int LastIndexOfStage(string stage)
        {
            for (int i = _runs.Count - 1; i >= 0; i--)
                if ((_runs[i].StageId ?? "") == stage) return i;
            return -1;
        }

        private List<RunRecord> CurrentGroup()
        {
            if (_stages.Count == 0) return new List<RunRecord>();
            _stageIndex = Mathf.Clamp(_stageIndex, 0, _stages.Count - 1);
            var g = new List<RunRecord>();
            string key = _stages[_stageIndex];
            ActiveStageId = key;
            foreach (var r in _runs) if ((r.StageId ?? "") == key) g.Add(r);
            return g;
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(2); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            // resize grip (bottom-right): width only
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(2, m, ref rw, ref dh, 360f, Mathf.Max(360f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 380f; Plugin.ComparePanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.ComparePanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; _confirmReset = false; return; }
                if (_resetAllRect.Contains(m))
                {
                    if (_confirmReset) { RunStore.DeleteAll(); _confirmReset = false; _stageIndex = 0; _runIndex = 0; _charIndex = 0; Reload(); }
                    else _confirmReset = true;
                    return;
                }
                _confirmReset = false;   // any other click cancels the pending reset
                if (_stagePrev.Contains(m)) { _stageIndex--; if (_stageIndex < 0) _stageIndex = Mathf.Max(0, _stages.Count - 1); _runIndex = CurrentGroup().Count - 1; return; }
                if (_stageNext.Contains(m)) { _stageIndex++; if (_stageIndex >= _stages.Count) _stageIndex = 0; _runIndex = CurrentGroup().Count - 1; return; }
                // chart points are always shown on top: clicking one selects that run for the detail below
                for (int i = 0; i < _pointRects.Count; i++)
                    if (_pointRects[i].Contains(m)) { _runIndex = _pointRun[i]; return; }
                if (_runPrev.Contains(m)) { _runIndex = Mathf.Max(0, _runIndex - 1); return; }
                if (_runNext.Contains(m)) { _runIndex = Mathf.Min(CurrentGroup().Count - 1, _runIndex + 1); return; }
                if (_pinRect.Contains(m)) { TogglePin(); return; }
                if (_delRect.Contains(m)) { DeleteCurrentRun(); return; }
                for (int i = 0; i < _charTabs.Count; i++)
                    if (_charTabs[i].Contains(m)) { _charIndex = i; return; }
                if (_rect.Contains(m) && InputCompat.ClaimDrag(2)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(2)) { _dragging = false; return; }   // a panel on top stole the press
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased())
                {
                    _dragging = false; InputCompat.ReleaseDrag(2);
                    _wantX = _rect.x; _wantY = _rect.y;
                    Plugin.ComparePosX.Value = _rect.x;
                    Plugin.ComparePosY.Value = _rect.y;
                }
            }
        }

        /// <summary>Localize the difficulty suffix of a stage id ("2-2 HELL" -> "2-2 地獄").</summary>
        private static string LocalizeStage(string stage)
        {
            if (string.IsNullOrEmpty(stage)) return Loc.G("uncategorized");
            int sp = stage.LastIndexOf(' ');
            if (sp > 0)
            {
                string diff = stage.Substring(sp + 1);
                string loc = Loc.G(diff);
                if (loc != diff) return stage.Substring(0, sp) + " " + loc;
            }
            return stage;
        }

        /// <summary>Display name for a character id, looked up from either run's party.</summary>
        private static string CharName(RunRecord baseline, RunRecord current, string id)
        {
            // prefer the current run's (newer) name so a fresh localized capture wins over an
            // older run that may still hold English/legacy names
            foreach (var r in new[] { current, baseline })
                if (r != null)
                    foreach (var s in r.Party)
                        if (s != null && s.Character == id && !string.IsNullOrEmpty(s.CharacterName))
                            return s.CharacterName;
            return string.IsNullOrEmpty(id) ? "?" : id;
        }

        private void TogglePin()
        {
            var g = CurrentGroup();
            if (g.Count == 0) return;
            _runIndex = Mathf.Clamp(_runIndex, 0, g.Count - 1);
            string stage = _stages[_stageIndex];
            string cur = g[_runIndex].Title;
            if (_pinned.TryGetValue(stage, out var p) && p == cur) _pinned.Remove(stage);
            else _pinned[stage] = cur;
        }

        /// <summary>Delete just the currently-selected run's record (e.g. a merged/AFK outlier).</summary>
        private void DeleteCurrentRun()
        {
            var g = CurrentGroup();
            if (g.Count == 0) return;
            _runIndex = Mathf.Clamp(_runIndex, 0, g.Count - 1);
            if (RunStore.Delete(g[_runIndex]))
            {
                if (_runIndex >= g.Count - 1) _runIndex = Mathf.Max(0, _runIndex - 1);
                Reload();
            }
        }

        // ---------------- rendering ----------------

        private void EnsureAssets()
        {
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_bgTex == null || !Mathf.Approximately(_bgAlphaBaked, _opacity))
            {
                if (_bgTex == null) _bgTex = new Texture2D(1, 1);
                _bgTex.SetPixel(0, 0, new Color(0f, 0f, 0f, 1f));   // solid black, no transparency
                _bgTex.Apply();
                _bgAlphaBaked = _opacity;
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
            _tiny = new GUIStyle { fontSize = Mathf.Max(9, fsm - 2), richText = true, wordWrap = true };
            _tiny.normal.textColor = new Color(0.7f, 0.75f, 0.85f);
            _col = new GUIStyle { fontSize = fs, richText = true };
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

        void OnGUI()
        {
            if (!_visible || GameUiState.MenuOpen()) return;   // hide while a game menu is open
            GUI.depth = -10;   // lower depth renders on top of the F9/F10 panels
            var prevM = GUI.matrix;
            try
            {
                EnsureAssets();
                if (!_placed) PlaceDefault();
                if (!_loaded) Reload();

                int fs = Plugin.FontSize.Value;
                float lh = fs + 7;
                float x = _rect.x, w = _rect.width;
                float ix = x + Pad, iw = w - Pad * 2;

                var group = CurrentGroup();
                if (group.Count == 0)
                {
                    _scale = UiScale.Fit(_rect.width, Pad + lh * 2 + Pad);
                    GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                    DrawEmpty(ix, iw, lh, fs);
                    return;
                }

                _runIndex = Mathf.Clamp(_runIndex, 0, group.Count - 1);
                string stage = _stages[_stageIndex];
                _pinned.TryGetValue(stage, out var pinTitle);
                var baseline = StageCompare.PickBaseline(group, pinTitle);
                var current = group[_runIndex];
                var chars = StageCompare.PartyCharacters(baseline, current);
                if (chars.Count == 0) chars.Add("");
                _charIndex = Mathf.Clamp(_charIndex, 0, chars.Count - 1);
                var cmp = StageCompare.Compare(baseline, current, chars[_charIndex]);
                bool curIsBase = ReferenceEquals(baseline, current);
                bool curPinned = pinTitle != null && current.Title == pinTitle;
                bool hasTabs = chars.Count > 1;

                // ---- measure ----
                int statRows = cmp.Stats.Count;
                int waveRows = Mathf.Min(cmp.Waves.Count, 8);
                int gearRows = cmp.Gear.Count;
                int skillRows = cmp.Skills.Count;
                float coreRows = 6f;
                var dist = MergedShares(baseline, current);
                int distRows = Mathf.Min(dist.Count, 4);
                float chartH = 120f;
                bool hasRewards = cmp.Current.GoldGained != 0 || cmp.Current.ExpGained != 0 || cmp.Current.Boxes.Count > 0
                    || cmp.Baseline.GoldGained != 0 || cmp.Baseline.ExpGained != 0;
                float rewardRows = hasRewards ? 6f + (cmp.Current.Boxes.Count > 0 ? 1 : 0) : 0f;
                float leftH = lh + lh * coreRows
                    + (statRows > 0 ? lh * 0.5f + lh * statRows : 0)
                    + lh + 14 + 14 + lh * distRows
                    + lh + lh * waveRows;
                int curSkills = cmp.CurrentSnap != null ? cmp.CurrentSnap.Skills.Count : 0;
                int curGear = cmp.CurrentSnap != null ? cmp.CurrentSnap.Equipment.Count : 0;
                int rmSkills = 0, rmGear = 0;
                foreach (var sc in cmp.Skills) if (sc.Kind == StageCompare.ChangeKind.Removed) rmSkills++;
                foreach (var gc in cmp.Gear) if (gc.Kind == StageCompare.ChangeKind.Removed) rmGear++;
                float rightH = lh + lh * Mathf.Max(curSkills + rmSkills, 1)
                    + lh * 0.5f + lh + lh * 1.4f * Mathf.Max(curGear + rmGear, 1) + lh
                    + (hasRewards ? lh * 0.5f + lh + lh * rewardRows : 0);   // rewards now live under gear
                float detailH = Mathf.Max(leftH, rightH);
                float h = Pad + lh /*header*/ + lh /*stage nav*/ + chartH + 18 + lh /*run nav*/ + (hasTabs ? lh : 0) + detailH + Pad;
                _rect.height = h;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                // keep the WHOLE panel on-screen, but clamp from the INTENDED position so a transient
                // window resize (opening/closing a game menu) can't permanently shove it.
                if (!_dragging)
                {
                    _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale));
                    _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale));
                }
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                float cy = _rect.y + Pad;
                _handleRect = new Rect(x, _rect.y, w, lh);

                // header: title + reset-all + close
                string sid = LocalizeStage(stage);
                GUI.Label(new Rect(ix, cy, iw - 120, lh), $"{Loc.G("compare_title")} <color=#7FB2FF>{sid}</color>", _title);
                _resetAllRect = new Rect(x + w - 116, cy - 1, 86, lh);
                GUI.Button(_resetAllRect, _confirmReset ? Loc.G("confirm_reset") : Loc.G("reset_all"), _btn);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh);
                GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                // stage nav + chart hint
                _stagePrev = new Rect(ix, cy, 26, lh - 2);
                _stageNext = new Rect(ix + 30, cy, 26, lh - 2);
                GUI.Button(_stagePrev, "≪", _btn);
                GUI.Button(_stageNext, "≫", _btn);
                GUI.Label(new Rect(ix + 62, cy, iw - 62, lh), $"<size=11>{_stageIndex + 1}/{_stages.Count}　{group.Count} {Loc.G("runs")}　<color=#FFC857>◆</color>{Loc.G("baseline")}　{Loc.G("chart_hint")}</size>", _dim);
                cy += lh;

                // ---- chart (always on top) ----
                DrawChartSection(group, baseline, ix, cy, iw, chartH);
                cy += chartH + 18;

                // run nav + pin
                _runPrev = new Rect(ix, cy, 26, lh - 2);
                _runNext = new Rect(ix + 30, cy, 26, lh - 2);
                GUI.Button(_runPrev, "◀", _btn);
                GUI.Button(_runNext, "▶", _btn);
                GUI.Label(new Rect(ix + 62, cy, iw - 200, lh), $"<size=11>{current.Title}  {_runIndex + 1}/{group.Count}</size>", _dim);
                _pinRect = new Rect(x + w - Pad - 64, cy - 1, 64, lh);
                GUI.Button(_pinRect, curPinned ? "📌" + Loc.G("pinned") : Loc.G("set_baseline"), _btn);
                // delete just this run (e.g. an AFK/merged record); left of the pin button
                _delRect = new Rect(x + w - Pad - 64 - 30, cy - 1, 26, lh);
                GUI.Button(_delRect, "🗑", _btn);
                cy += lh;

                // character tabs
                _charTabs.Clear();
                if (hasTabs)
                {
                    float tx = ix, tw = iw / Mathf.Min(chars.Count, 5);
                    for (int i = 0; i < chars.Count && i < 5; i++)
                    {
                        var tr = new Rect(tx, cy, tw - 2, lh - 1);
                        _charTabs.Add(tr);
                        string label = CharName(baseline, current, chars[i]);
                        bool sel = i == _charIndex;
                        GUI.Label(tr, sel ? $"<b><color=#FFC857>[{label}]</color></b>" : $"<color=#9fb4cc>{label}</color>", _dim);
                        tx += tw;
                    }
                    cy += lh;
                }

                // ---- two big columns: LEFT = numbers, RIGHT = gear/skill ----
                float gap = 12f;
                float leftColW = iw * 0.54f;
                float rightColX = ix + leftColW + gap;
                float rightColW = iw - leftColW - gap;
                DrawRect(rightColX - gap * 0.5f, cy, 1, detailH, new Color(1, 1, 1, 0.10f));

                // LEFT column
                float ly = cy, lx = ix;
                float subW = leftColW / 2f;
                float rxc = ix + subW;
                GUI.Label(new Rect(lx, ly, subW, lh), $"<size=11><color=#9fb4cc>📌 {Loc.G("baseline")}</color></size>", _dim);
                GUI.Label(new Rect(rxc + 4, ly, subW, lh), curIsBase ? $"<size=11><color=#9fb4cc>{Loc.G("baseline")}</color></size>" : $"<size=11><color=#9fb4cc>{Loc.G("this_run")}</color></size>", _dim);
                ly += lh;
                ly = CoreRow(ly, subW, lx, rxc, "total_time", cmp, "duration", false, true);
                ly = CoreRow(ly, subW, lx, rxc, "active_time", cmp, "active", true, true);
                ly = CoreRow(ly, subW, lx, rxc, "idle_time", cmp, "idle", false, true);
                ly = CoreRow(ly, subW, lx, rxc, "avg", cmp, "avg", true, false);
                ly = CoreRow(ly, subW, lx, rxc, "peak", cmp, "peak", true, false);
                ly = CoreRow(ly, subW, lx, rxc, "crit", cmp, "crit", true, false, pct: true);

                // rewards (gold/exp) moved to the RIGHT column under gear — see DrawLoadout.

                if (statRows > 0)
                {
                    ly += lh * 0.5f;
                    foreach (var st in cmp.Stats)
                        ly = Row2(ly, subW, lx, rxc, Loc.G(st.Key), FmtStat(st.Baseline), FmtStat(st.Current), DeltaColor(st.Baseline, st.Current, true));
                }
                GUI.Label(new Rect(lx, ly, leftColW, lh), Loc.G("dmg_dist"), _dim); ly += lh;
                DrawDist(lx + 34, ly, leftColW - 34, 11, baseline);
                GUI.Label(new Rect(lx, ly - 1, 32, 12), $"<size=10>{Loc.G("baseline")}</size>", _tiny);
                ly += 14;
                DrawDist(lx + 34, ly, leftColW - 34, 11, current);
                GUI.Label(new Rect(lx, ly - 1, 32, 12), $"<size=10>{Loc.G("this_run")}</size>", _tiny);
                ly += 14;
                for (int i = 0; i < distRows; i++)
                {
                    var d = dist[i];
                    string col = Mathf.Abs(d.cur - d.bas) < 0.001f ? "#ffffff" : (d.cur > d.bas ? "#5fd07c" : "#ef6a5a");
                    string nm = Loc.Name(DpsTracker.DecodeName(d.flag));
                    // aligned baseline | this columns (consistent with the other sections)
                    GUI.Label(new Rect(lx, ly, subW, lh), $"<color=#{ColorHex(d.flag)}>■</color> <color=#aeb6c2>{nm}</color> {d.bas * 100f:0.#}%", _label);
                    GUI.Label(new Rect(rxc + 4, ly, subW, lh), $"<color={col}>{d.cur * 100f:0.#}%</color>", _col);
                    ly += lh;
                }
                GUI.Label(new Rect(lx, ly, leftColW, lh), $"{Loc.G("per_wave")}", _dim); ly += lh;
                for (int i = 0; i < waveRows; i++)
                {
                    var wd = cmp.Waves[i];
                    ly = Row2(ly, subW, lx, rxc, $"{Loc.G("wave_short")}{wd.Wave}", $"{wd.Baseline:0.0}s", $"{wd.Current:0.0}s", DeltaColor(wd.Baseline, wd.Current, false));
                }

                // RIGHT column: FULL loadout of the selected character, with change markers vs baseline
                float ry = cy;
                ry = DrawLoadout(cmp, rightColX, ry, rightColW, lh);
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        /// <summary>Dashboard: clear-time trend line for the stage. X = attempt (oldest→newest),
        /// Y = clear seconds. Click a point to open the detailed compare for that run.</summary>
        /// <summary>Draw the clear-time trend plot within [x,y,w,plotH]; registers clickable point rects.
        /// Highlights the baseline point (gold) and the currently-selected run (white ring).</summary>
        private void DrawChartSection(List<RunRecord> group, RunRecord baseline, float ix, float y, float iw, float plotH)
        {
            // Build the Y series (clear seconds) and find the baseline index, then defer to the
            // shared TrendChart so the F11 chart and the loot-heatmap panel render identically.
            var vals = new List<float>(group.Count);
            int baseIdx = -1;
            for (int i = 0; i < group.Count; i++) { vals.Add(group[i].Duration); if (ReferenceEquals(group[i], baseline)) baseIdx = i; }
            TrendChart.Draw(new Rect(ix, y, iw, plotH), _rect.x, vals, baseIdx, _runIndex, _white, _tiny, _pointRects, _pointRun);
        }

        private void DrawLine(Vector2 a, Vector2 b, Color c)
        {
            float len = Vector2.Distance(a, b);
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / 3f));
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(a, b, i / (float)steps);
                DrawRect(p.x - 1, p.y - 1, 2, 2, c);
            }
        }

        /// <summary>One aligned row: "label baseStr" in the baseline column, colored curStr in the "this" column.</summary>
        private float Row2(float y, float subW, float lx, float rxc, string label, string baseStr, string curStr, string curColor)
        {
            int fs = Plugin.FontSize.Value; float lh = fs + 7;
            GUI.Label(new Rect(lx, y, subW, lh), $"<color=#aeb6c2>{label}</color> {baseStr}", _label);
            GUI.Label(new Rect(rxc + 4, y, subW, lh), $"<color={curColor}>{curStr}</color>", _col);
            return y + lh;
        }

        private static string DeltaColor(double b, double c, bool higherBetter)
        {
            if (System.Math.Abs(c - b) < 1e-6) return "#ffffff";
            bool better = higherBetter ? c > b : c < b;
            return better ? "#5fd07c" : "#ef6a5a";
        }

        private float CoreRow(float cy, float colW, float lx, float rx, string labelKey, StageCompare.CompareResult cmp, string metricKey, bool higherBetter, bool seconds, bool pct = false)
        {
            int fs = Plugin.FontSize.Value; float lh = fs + 7;
            var md = cmp.Metrics.Find(mm => mm.Key == metricKey);
            string bv = seconds ? $"{md.Baseline:0.0}s" : (pct ? $"{md.Baseline * 100f:0.#}%" : Fmt(md.Baseline));
            string cvRaw = seconds ? $"{md.Current:0.0}s" : (pct ? $"{md.Current * 100f:0.#}%" : Fmt(md.Current));
            bool better = higherBetter ? md.Current >= md.Baseline : md.Current <= md.Baseline;
            string col = Mathf.Approximately((float)md.Delta, 0f) ? "#ffffff" : (better ? "#5fd07c" : "#ef6a5a");
            GUI.Label(new Rect(lx, cy, colW, lh), $"<color=#aeb6c2>{Loc.G(labelKey)}</color> {bv}", _label);
            GUI.Label(new Rect(rx + 6, cy, colW, lh), $"<color={col}>{cvRaw}</color>", _col);
            return cy + lh;
        }

        private void DrawEmpty(float ix, float iw, float lh, int fs)
        {
            float h = Pad + lh * 2 + Pad;
            _rect.height = h;
            GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);
            float cy = _rect.y + Pad;
            GUI.Label(new Rect(ix, cy, iw - 28, lh), Loc.G("compare_title"), _title);
            _closeRect = new Rect(_rect.x + _rect.width - 26, cy - 2, 22, lh);
            GUI.Button(_closeRect, "✕", _btn);
            cy += lh;
            GUI.Label(new Rect(ix, cy, iw, lh), Loc.G("no_runs"), _dim);
            _handleRect = new Rect(_rect.x, _rect.y, _rect.width, lh);
        }

        /// <summary>Merge baseline+current damage-type shares by flag, sorted by the larger share.</summary>
        private static List<(int flag, float bas, float cur)> MergedShares(RunRecord b, RunRecord c)
        {
            var bm = ShareMap(b); var cm = ShareMap(c);
            var keys = new HashSet<int>(); var order = new List<int>();
            foreach (var k in bm.Keys) if (keys.Add(k)) order.Add(k);
            foreach (var k in cm.Keys) if (keys.Add(k)) order.Add(k);
            var list = new List<(int, float, float)>();
            foreach (var k in order)
            {
                bm.TryGetValue(k, out var bv); cm.TryGetValue(k, out var cv);
                list.Add((k, bv, cv));
            }
            list.Sort((a, z) => Mathf.Max(z.Item2, z.Item3).CompareTo(Mathf.Max(a.Item2, a.Item3)));
            return list;
        }

        private static Dictionary<int, float> ShareMap(RunRecord r)
        {
            var m = new Dictionary<int, float>();
            if (r == null || r.Total <= 0) return m;
            for (int i = 0; i < r.TypeFlags.Count && i < r.TypeAmounts.Count; i++)
                if (r.TypeAmounts[i] > 0) m[r.TypeFlags[i]] = (float)(r.TypeAmounts[i] / r.Total);
            return m;
        }

        private static string ColorHex(int flag)
        {
            var c = ColorForFlag(flag);
            return $"{(int)(c.r * 255):X2}{(int)(c.g * 255):X2}{(int)(c.b * 255):X2}";
        }

        private void DrawDist(float x, float y, float w, float h, RunRecord r)
        {
            DrawRect(x, y, w, h, new Color(0f, 0f, 0f, 1f));
            if (r == null || r.Total <= 0) return;
            var parts = new List<(int flag, float share)>();
            for (int i = 0; i < r.TypeFlags.Count && i < r.TypeAmounts.Count; i++)
                if (r.TypeAmounts[i] > 0) parts.Add((r.TypeFlags[i], (float)(r.TypeAmounts[i] / r.Total)));
            parts.Sort((a, b) => b.share.CompareTo(a.share));
            float cx = x;
            foreach (var p in parts) { float seg = p.share * w; DrawRect(cx, y, seg, h, ColorForFlag(p.flag)); cx += seg; }
        }

        /// <summary>Right column: the selected character's full skill + gear list, colouring entries
        /// that were added (green) or changed (yellow) vs the baseline, plus removed ones (red).</summary>
        private float DrawLoadout(StageCompare.CompareResult cmp, float x, float y, float w, float lh)
        {
            var cur = cmp.CurrentSnap;
            var bas = cmp.BaselineSnap;
            float nameW = w * 0.52f;
            float valW = (w - nameW) / 2f;
            float baseX = x + nameW, curX = x + nameW + valW;

            // ---- skills (name | 基準Lv | 這場Lv) ----
            GUI.Label(new Rect(x, y, nameW, lh), Loc.G("skill_changes"), _dim);
            GUI.Label(new Rect(baseX, y, valW, lh), $"<size=10><color=#9fb4cc>{Loc.G("baseline")}</color></size>", _dim);
            GUI.Label(new Rect(curX, y, valW, lh), $"<size=10><color=#9fb4cc>{Loc.G("this_run")}</color></size>", _dim);
            y += lh;
            var baseSkill = new Dictionary<string, SkillEntry>();
            if (bas != null) foreach (var s in bas.Skills) baseSkill[s.Key != 0 ? "k" + s.Key : s.Name] = s;
            var ids = new List<string>(); var seen = new HashSet<string>();
            if (cur != null) foreach (var s in cur.Skills) if (seen.Add(s.Key != 0 ? "k" + s.Key : s.Name)) ids.Add(s.Key != 0 ? "k" + s.Key : s.Name);
            if (bas != null) foreach (var s in bas.Skills) { string id = s.Key != 0 ? "k" + s.Key : s.Name; if (seen.Add(id)) ids.Add(id); }
            if (ids.Count == 0) { GUI.Label(new Rect(x, y, w, lh), "<color=#8a93a0>—</color>", _tiny); y += lh; }
            foreach (var id in ids)
            {
                bool hb = baseSkill.TryGetValue(id, out var b);
                SkillEntry c = default; bool hc = false;
                if (cur != null) foreach (var s in cur.Skills) if ((s.Key != 0 ? "k" + s.Key : s.Name) == id) { c = s; hc = true; break; }
                string name = hc ? c.Name : b.Name;
                string baseStr = hb ? Loc.G("lv") + b.Level : "—";
                string curStr = hc ? Loc.G("lv") + c.Level : "—";
                string col = !hb ? "#5fd07c" : (!hc ? "#ef6a5a" : (b.Level != c.Level ? "#e7c25a" : "#dfe4ec"));
                GUI.Label(new Rect(x, y, nameW, lh), $"<color=#cdd5df>{name}</color>", _tiny);
                GUI.Label(new Rect(baseX, y, valW, lh), $"<color=#8a93a0>{baseStr}</color>", _tiny);
                GUI.Label(new Rect(curX, y, valW, lh), $"<color={col}>{curStr}</color>", _tiny);
                y += lh;
            }

            y += lh * 0.5f;
            // ---- gear (current item per slot; colour vs baseline) ----
            GUI.Label(new Rect(x, y, w, lh), Loc.G("gear_changes"), _dim); y += lh;
            var baseGear = new Dictionary<string, GearItem>();
            if (bas != null) foreach (var g in bas.Equipment) baseGear[string.IsNullOrEmpty(g.Slot) ? g.Name : g.Slot] = g;
            bool any = false;
            if (cur != null) foreach (var g in cur.Equipment)
            {
                any = true;
                string id = string.IsNullOrEmpty(g.Slot) ? g.Name : g.Slot;
                bool hb = baseGear.TryGetValue(id, out var b);
                string col = !hb ? "#5fd07c" : (!GearSame(b, g) ? "#e7c25a" : "#cdd5df");
                string was = (hb && !GearSame(b, g)) ? $"  <size=10><color=#8a93a0>({Loc.G("baseline")}: {b.Name})</color></size>" : "";
                GUI.Label(new Rect(x, y, w, lh * 1.4f), $"<color={col}>{GearStr(g)}</color>{was}", _tiny); y += lh * 1.4f;
            }
            // removed gear
            if (cur != null) { var curIds = new HashSet<string>(); foreach (var g in cur.Equipment) curIds.Add(string.IsNullOrEmpty(g.Slot) ? g.Name : g.Slot);
                if (bas != null) foreach (var g in bas.Equipment) if (!curIds.Contains(string.IsNullOrEmpty(g.Slot) ? g.Name : g.Slot)) { any = true; GUI.Label(new Rect(x, y, w, lh * 1.4f), $"<color=#ef6a5a>− {GearStr(g)}</color>", _tiny); y += lh * 1.4f; } }
            if (!any) { GUI.Label(new Rect(x, y, w, lh), "<color=#8a93a0>—</color>", _tiny); y += lh; }

            // ---- rewards: gold/exp gained + efficiency (moved here so the left table isn't too tall) ----
            var bR = cmp.Baseline; var cR = cmp.Current;
            bool hasRewards = cR != null && bR != null &&
                (cR.GoldGained != 0 || cR.ExpGained != 0 || cR.Boxes.Count > 0 || bR.GoldGained != 0 || bR.ExpGained != 0);
            if (hasRewards)
            {
                y += lh * 0.5f;
                GUI.Label(new Rect(x, y, nameW, lh), Loc.G("rewards"), _dim);
                GUI.Label(new Rect(baseX, y, valW, lh), $"<size=10><color=#9fb4cc>{Loc.G("baseline")}</color></size>", _dim);
                GUI.Label(new Rect(curX, y, valW, lh), $"<size=10><color=#9fb4cc>{Loc.G("this_run")}</color></size>", _dim);
                y += lh;
                string ps = Loc.G("per_s"), pa = "/" + Loc.G("active_time");
                y = RewardRow(x, y, nameW, baseX, curX, valW, lh, Loc.G("gold"), Fmt(bR.GoldGained), Fmt(cR.GoldGained), DeltaColor(bR.GoldGained, cR.GoldGained, true));
                y = RewardRow(x, y, nameW, baseX, curX, valW, lh, Loc.G("exp"), Fmt(bR.ExpGained), Fmt(cR.ExpGained), DeltaColor(bR.ExpGained, cR.ExpGained, true));
                y = RewardRow(x, y, nameW, baseX, curX, valW, lh, Loc.G("gold") + ps, Fmt(Rate(bR.GoldGained, bR.Duration)), Fmt(Rate(cR.GoldGained, cR.Duration)), DeltaColor(Rate(bR.GoldGained, bR.Duration), Rate(cR.GoldGained, cR.Duration), true));
                y = RewardRow(x, y, nameW, baseX, curX, valW, lh, Loc.G("exp") + ps, Fmt(Rate(bR.ExpGained, bR.Duration)), Fmt(Rate(cR.ExpGained, cR.Duration)), DeltaColor(Rate(bR.ExpGained, bR.Duration), Rate(cR.ExpGained, cR.Duration), true));
                y = RewardRow(x, y, nameW, baseX, curX, valW, lh, Loc.G("gold") + pa, Fmt(Rate(bR.GoldGained, bR.ActiveSeconds)), Fmt(Rate(cR.GoldGained, cR.ActiveSeconds)), DeltaColor(Rate(bR.GoldGained, bR.ActiveSeconds), Rate(cR.GoldGained, cR.ActiveSeconds), true));
                y = RewardRow(x, y, nameW, baseX, curX, valW, lh, Loc.G("exp") + pa, Fmt(Rate(bR.ExpGained, bR.ActiveSeconds)), Fmt(Rate(cR.ExpGained, cR.ActiveSeconds)), DeltaColor(Rate(bR.ExpGained, bR.ActiveSeconds), Rate(cR.ExpGained, cR.ActiveSeconds), true));
                if (cR.Boxes.Count > 0)
                {
                    var sb = new System.Text.StringBuilder($"<color=#aeb6c2>{Loc.G("boxes")}</color> ");
                    foreach (var b in cR.Boxes) sb.Append($"{Loc.Name(b.Type)}×{b.Count} ");
                    GUI.Label(new Rect(x, y, w, lh), sb.ToString(), _tiny); y += lh;
                }
            }
            return y;
        }

        private float RewardRow(float x, float y, float nameW, float baseX, float curX, float valW, float lh,
            string label, string baseStr, string curStr, string curColor)
        {
            GUI.Label(new Rect(x, y, nameW, lh), $"<color=#aeb6c2>{label}</color>", _tiny);
            GUI.Label(new Rect(baseX, y, valW, lh), $"<color=#8a93a0>{baseStr}</color>", _tiny);
            GUI.Label(new Rect(curX, y, valW, lh), $"<color={curColor}>{curStr}</color>", _tiny);
            return y + lh;
        }

        private static bool GearSame(GearItem a, GearItem b)
        {
            if (a == null || b == null) return false;
            if (a.Name != b.Name || a.Affixes.Count != b.Affixes.Count) return false;
            for (int i = 0; i < a.Affixes.Count; i++)
                if (a.Affixes[i].Name != b.Affixes[i].Name || Mathf.Abs((float)(a.Affixes[i].Value - b.Affixes[i].Value)) > 1e-4f) return false;
            return true;
        }

        private void DrawGearChange(float x, float y, float w, float lh, StageCompare.GearChange gc)
        {
            string s;
            switch (gc.Kind)
            {
                case StageCompare.ChangeKind.Added: s = $"<color=#5fd07c>＋ {GearStr(gc.Current)}</color>"; break;
                case StageCompare.ChangeKind.Removed: s = $"<color=#ef6a5a>− {GearStr(gc.Baseline)}</color>"; break;
                default: s = $"<color=#e7c25a>~ {GearStr(gc.Baseline)} → {GearStr(gc.Current)}</color>"; break;
            }
            GUI.Label(new Rect(x, y, w, lh), s, _tiny);
        }

        private void DrawSkillChange(float x, float y, float w, float lh, StageCompare.SkillChange sc)
        {
            string s;
            switch (sc.Kind)
            {
                case StageCompare.ChangeKind.Added: s = $"<color=#5fd07c>＋ {sc.Name} {Loc.G("lv")}{sc.CurrentLevel}</color>"; break;
                case StageCompare.ChangeKind.Removed: s = $"<color=#ef6a5a>− {sc.Name} {Loc.G("lv")}{sc.BaselineLevel}</color>"; break;
                default: s = $"<color=#e7c25a>~ {sc.Name} {Loc.G("lv")}{sc.BaselineLevel}→{sc.CurrentLevel}</color>"; break;
            }
            GUI.Label(new Rect(x, y, w, lh), s, _tiny);
        }

        private static string GearStr(GearItem g)
        {
            if (g == null) return "?";
            if (g.Affixes.Count == 0) return g.Name;
            var sb = new System.Text.StringBuilder(g.Name);
            sb.Append(" (");
            for (int i = 0; i < g.Affixes.Count; i++)
            {
                if (i > 0) sb.Append(", ");
                sb.Append(Loc.G(g.Affixes[i].Name)).Append('+').Append(g.Affixes[i].Value.ToString("0.##"));
            }
            sb.Append(')');
            return sb.ToString();
        }

        private static double Rate(double amount, float time) => time > 0.01f ? amount / time : 0;

        private static string FmtStat(double v) => Math.Abs(v) < 100 ? v.ToString("0.##") : Fmt(v);

        private static string Fmt(double v)
        {
            if (v >= 1e9) return (v / 1e9).ToString("0.##") + "B";
            if (v >= 1e6) return (v / 1e6).ToString("0.##") + "M";
            if (v >= 1e3) return (v / 1e3).ToString("0.##") + "K";
            return v.ToString("0");
        }

        private static Color ColorForFlag(int flag)
        {
            switch (flag)
            {
                case 1: return new Color(0.93f, 0.32f, 0.26f);
                case 2: return new Color(0.30f, 0.68f, 0.96f);
                case 4: return new Color(0.74f, 0.46f, 0.97f);
                case 8: return new Color(0.40f, 0.86f, 0.46f);
                case 16: return new Color(0.97f, 0.46f, 0.86f);
                case 32: return new Color(0.97f, 0.83f, 0.32f);
            }
            float r = 0, g = 0, b = 0; int cnt = 0;
            int[] bits = { 1, 2, 4, 8, 16, 32 };
            foreach (var bit in bits) if ((flag & bit) != 0) { var c = ColorForFlag(bit); r += c.r; g += c.g; b += c.b; cnt++; }
            return cnt == 0 ? new Color(0.6f, 0.6f, 0.6f) : new Color(r / cnt, g / cnt, b / cnt);
        }
    }
}
