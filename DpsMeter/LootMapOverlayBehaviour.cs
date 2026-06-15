using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>IMGUI overlay (F-key, hub slot 6): the 掉寶熱力圖 / Loot Heatmap. Visualizes existing
    /// trackers over time — two stacked day × 24-hour grids: top = F5 box pickups (BoxTracker.Events,
    /// blue), bottom = ALL F4 opens (BoxOpenTracker.Stats.Log, gold-green) with a per-cell detail hover
    /// listing each open — a summary row, and a clear-time trend chart that follows F11's selected stage.
    /// Read-only; resize-safe; drag from anywhere; auto-hides with the other panels while a game menu
    /// is open.</summary>
    public class LootMapOverlayBehaviour : MonoBehaviour
    {
        public LootMapOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const float Pad = 10f;
        private const int MaxDayRows = 3;        // two grids → keep compact (most recent 3 days)
        private const float ChartH = 100f;

        private Rect _rect = new Rect(24, 120, 560, 0);
        private bool _visible, _placed;
        private float _wantX, _wantY;
        private Vector2 _dragOffset; private bool _dragging;

        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box, _tip, _tipMulti;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;   // font sizes the styles were last built with (live-rebuild on change)

        private Rect _closeRect;
        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();

        // hover tooltip target (local-space cell rect + text), filled during the grid draw
        private bool _hasTip; private Rect _tipCell; private string _tipText;

        // per-run clear seconds for the trend chart (F11's currently-selected stage), rebuilt when
        // RunStore.Version OR the resolved stage changes
        private readonly List<float> _trend = new List<float>();
        private int _trendVersion = -1;
        private string _trendStage;
        private string _liveStage = "";      // throttled CharacterReader.CurrentStageId() cache
        private float _nextStageProbe;

        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        void Awake()
        {
            _rect.width = Mathf.Max(560, Plugin.LootMapPanelWidth.Value);
            _visible = Plugin.LootMapStartVisible.Value;
            PanelRegistry.Register("lootmap", 6, "▦", () => Loc.G("lootmap_title"), Plugin.LootMapToggleKey, () => _visible, v => _visible = v);
        }
        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.LootMapPosX.Value, py = Plugin.LootMapPosY.Value;
            if (px < 0 || py < 0) { _rect.x = 24f; _rect.y = 120f; }
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y; _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.SetPanel(7, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.KeyPressed(Plugin.LootMapToggleKey)) _visible = !_visible;
                if (_visible) HandlePointer();
                else if (_dragging) _dragging = false;
            }
            catch { }
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(7); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            // resize grip (bottom-right): width only
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(7, m, ref rw, ref dh, 480f, Mathf.Max(480f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 560f; Plugin.LootMapPanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.LootMapPanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                // drag from anywhere on the panel except the buttons (handled/returned above)
                if (_rect.Contains(m) && InputCompat.ClaimDrag(7)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(7)) { _dragging = false; return; }
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased()) { _dragging = false; InputCompat.ReleaseDrag(7); _wantX = _rect.x; _wantY = _rect.y; Plugin.LootMapPosX.Value = _rect.x; Plugin.LootMapPosY.Value = _rect.y; }
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
            _btn = new GUIStyle(GUI.skin.button) { fontSize = fsm, fontStyle = FontStyle.Bold, richText = true };
            _tip = new GUIStyle { fontSize = fsm, richText = true, alignment = TextAnchor.MiddleCenter }; _tip.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
            _tipMulti = new GUIStyle { fontSize = fsm, richText = true, wordWrap = false, alignment = TextAnchor.UpperLeft }; _tipMulti.normal.textColor = new Color(0.95f, 0.95f, 0.95f);
            _box = new GUIStyle(); _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _label, _dim, _tiny, _btn, _tip, _tipMulti);
            _stylesReady = true;
        }

        private void DrawRect(float x, float y, float w, float h, Color c) { var p = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(x, y, w, h), _white); GUI.color = p; }

        // dark slate -> bright ramp by normalized intensity. metric 0 = opens (blue), 1 = loot (gold-green).
        private static Color RampColor(float t, int metric)
        {
            t = Mathf.Clamp01(t);
            var lo = new Color(0.10f, 0.13f, 0.18f);
            var hi = metric == 1 ? new Color(0.95f, 0.82f, 0.30f) : new Color(0.30f, 0.62f, 1.00f);
            return Color.Lerp(lo, hi, t);
        }

        // Per-run clear seconds for the trend chart. Stage choice: F11's selection while that panel is
        // OPEN (its ActiveStageId goes stale once closed), otherwise the stage the player is currently
        // on, falling back to the most-recent run's stage when the live id can't be read.
        // Chronological (oldest → newest), capped to the last ~40 runs of that stage.
        // Rebuilt only when RunStore.Version OR the resolved stage changes.
        private void RebuildTrend()
        {
            try
            {
                var runs = RunStore.LoadAll();           // oldest → newest

                string stage = CompareOverlayBehaviour.PanelOpen ? CompareOverlayBehaviour.ActiveStageId : null;
                if (string.IsNullOrEmpty(stage))
                {
                    // live stage id is a reflection read and this runs per OnGUI — refresh at most 1/s
                    if (Time.unscaledTime >= _nextStageProbe)
                    {
                        _nextStageProbe = Time.unscaledTime + 1f;
                        try { _liveStage = CharacterReader.CurrentStageId(); } catch { _liveStage = ""; }
                    }
                    stage = _liveStage;
                }
                if (string.IsNullOrEmpty(stage) && runs.Count > 0) stage = runs[runs.Count - 1].StageId;

                if (RunStore.Version == _trendVersion && stage == _trendStage) return;
                _trendVersion = RunStore.Version;
                _trendStage = stage;
                _trend.Clear();
                if (string.IsNullOrEmpty(stage)) return;

                for (int i = 0; i < runs.Count; i++)
                    if (runs[i] != null && runs[i].StageId == stage) _trend.Add(runs[i].Duration);
                if (_trend.Count > 40) _trend.RemoveRange(0, _trend.Count - 40);
            }
            catch { }
        }

        void OnGUI()
        {
            if (!_visible || GameUiState.MenuOpen()) return;
            GUI.depth = -8;
            var prevM = GUI.matrix;
            try
            {
                EnsureAssets();
                if (!_placed) PlaceDefault();
                RebuildTrend();

                // ---- TOP grid source: F5 box pickups (BoxTracker.Events) bucketed by (day, hour) ----
                var pickupByDay = new Dictionary<string, long[]>();
                var pickupFirst = new Dictionary<string, DateTime>(); var pickupLast = new Dictionary<string, DateTime>();
                long totalPickups = 0;
                try
                {
                    var ev = BoxTracker.Events;
                    for (int i = 0; i < ev.Count; i++)
                    {
                        var e = ev[i];
                        if (e == null) continue;
                        string day = e.Time.ToString("yyyy-MM-dd");
                        int hh = e.Time.Hour; if (hh < 0) hh = 0; else if (hh > 23) hh = 23;
                        if (!pickupByDay.TryGetValue(day, out var pArr)) { pArr = new long[24]; pickupByDay[day] = pArr; }
                        pArr[hh]++; totalPickups++;
                        if (!pickupFirst.TryGetValue(day, out var f0) || e.Time < f0) pickupFirst[day] = e.Time;
                        if (!pickupLast.TryGetValue(day, out var l0) || e.Time > l0) pickupLast[day] = e.Time;
                    }
                }
                catch { }

                // ---- BOTTOM grid source: ALL F4 opens (BoxOpenTracker.Stats.Log) by (day, hour) ----
                var goodByDay = new Dictionary<string, long[]>();
                var openFirst = new Dictionary<string, DateTime>(); var openLast = new Dictionary<string, DateTime>();
                var openEvByCell = new Dictionary<string, List<BoxOpenEvent>>();   // "day|hour" -> events
                long totalOpen = 0;
                try
                {
                    var log = BoxOpenTracker.Stats.Log;
                    for (int i = 0; i < log.Count; i++)
                    {
                        var e = log[i];
                        if (e == null) continue;
                        if (e.Grade < 3) continue;   // 開箱紀錄: legendary (傳說) and above only
                        string day = e.Time.ToString("yyyy-MM-dd");
                        int hh = e.Time.Hour; if (hh < 0) hh = 0; else if (hh > 23) hh = 23;
                        if (!goodByDay.TryGetValue(day, out var gArr)) { gArr = new long[24]; goodByDay[day] = gArr; }
                        gArr[hh]++; totalOpen++;
                        if (!openFirst.TryGetValue(day, out var f1) || e.Time < f1) openFirst[day] = e.Time;
                        if (!openLast.TryGetValue(day, out var l1) || e.Time > l1) openLast[day] = e.Time;
                        string ckey = day + "|" + hh;
                        if (!openEvByCell.TryGetValue(ckey, out var lst)) { lst = new List<BoxOpenEvent>(); openEvByCell[ckey] = lst; }
                        lst.Add(e);
                    }
                }
                catch { }

                // sorted distinct days across BOTH grids, newest first, capped at MaxDayRows
                var daySet = new HashSet<string>(pickupByDay.Keys);
                foreach (var k in goodByDay.Keys) daySet.Add(k);
                var days = new List<string>(daySet);
                days.Sort(StringComparer.Ordinal);
                days.Reverse();                                   // newest first
                if (days.Count > MaxDayRows) days = days.GetRange(0, MaxDayRows);
                int dayRows = days.Count;

                int fs = Plugin.FontSize.Value; float lh = fs + 6;
                bool hasChart = _trend.Count > 0;

                float cellH = lh + 2f;
                float oneGridH = dayRows > 0 ? lh /*grid header*/ + dayRows * cellH + lh /*hour axis*/ : lh /*grid header*/ + lh /*empty*/;
                float gridsH = oneGridH * 2f + lh * 0.4f /*gap between grids*/;

                // height = title + summary + two grids + (separator + chart header + chart) + padding
                float h = Pad + lh /*title*/ + lh /*summary*/ + gridsH;
                if (hasChart) h += lh * 0.6f /*sep*/ + lh /*chart header*/ + ChartH + lh;
                h += Pad;
                _rect.height = h;

                _scale = UiScale.Fit(_rect.width, _rect.height);
                if (!_dragging) { _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale)); _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale)); }
                float x = _rect.x, ix = x + Pad, w = _rect.width, iw = w - Pad * 2;
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                float cy = _rect.y + Pad;

                // ---- title bar ----
                GUI.Label(new Rect(ix, cy, iw - 26, lh), Loc.G("lootmap_title"), _title);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh); GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                // ---- summary row: total pickups (F5) / total opens (F4) ----
                GUI.Label(new Rect(ix, cy, iw, lh),
                    $"<color=#aeb6c2>{Loc.G("metric_pickup")} <color=#eaf3ee>{totalPickups}</color>　{Loc.G("metric_openlog")} <color=#eaf3ee>{totalOpen}</color></color>", _label);
                cy += lh;

                // ---- two heatmap grids (opens, then loot) ----
                _hasTip = false;
                Vector2 mLocal = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);

                // grid max over visible cells (own normalization per grid)
                long maxPickup = 1, maxOpen = 1;
                for (int r = 0; r < dayRows; r++)
                {
                    pickupByDay.TryGetValue(days[r], out var pArr);
                    goodByDay.TryGetValue(days[r], out var gArr);
                    for (int hh = 0; hh < 24; hh++)
                    {
                        if (pArr != null && pArr[hh] > maxPickup) maxPickup = pArr[hh];
                        if (gArr != null && gArr[hh] > maxOpen) maxOpen = gArr[hh];
                    }
                }

                var pickupStats = DayStats(days, pickupByDay, pickupFirst, pickupLast);
                var openStats = DayStats(days, goodByDay, openFirst, openLast);
                cy = DrawGrid(ix, cy, iw, lh, cellH, mLocal, days, dayRows, pickupByDay, Loc.G("metric_pickup"), maxPickup, 0, null, pickupStats);
                cy += lh * 0.4f;
                cy = DrawGrid(ix, cy, iw, lh, cellH, mLocal, days, dayRows, goodByDay, Loc.G("metric_openlog"), maxOpen, 1, openEvByCell, openStats);

                // ---- separator + clear-time trend chart ----
                if (hasChart)
                {
                    DrawRect(ix, cy + lh * 0.3f, iw, 1, new Color(1, 1, 1, 0.12f));
                    cy += lh * 0.6f;
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>{Loc.G("trend")} · {_trendStage}</color>", _dim);
                    cy += lh;
                    TrendChart.Draw(new Rect(ix, cy, iw, ChartH), _rect.x, _trend, -1, -1, _white, _tiny, null, null, "s");
                    cy += ChartH + lh;
                }

                // ---- hover tooltip (drawn last, on top); may be multi-line (split on \n) ----
                if (_hasTip)
                {
                    int lineCount = 1; for (int i = 0; i < _tipText.Length; i++) if (_tipText[i] == '\n') lineCount++;
                    bool multi = lineCount > 1;
                    float tlh = fs + 4f;
                    float th = multi ? lineCount * tlh + 6f : lh;
                    float tw = multi ? Mathf.Min(360f, iw) : _tip.CalcSize(new GUIContent(_tipText)).x + 12f;
                    float tx = Mathf.Clamp(_tipCell.x + _tipCell.width * 0.5f - tw * 0.5f, ix, ix + iw - tw);
                    float ty = _tipCell.y - th - 2f; if (ty < _rect.y) ty = _tipCell.y + _tipCell.height + 2f;
                    ty = Mathf.Clamp(ty, _rect.y, _rect.y + _rect.height - th);
                    DrawRect(tx, ty, tw, th, new Color(0.05f, 0.06f, 0.09f, 0.96f));
                    DrawRect(tx, ty, tw, 1, new Color(1, 1, 1, 0.18f));
                    if (multi)
                        GUI.Label(new Rect(tx + 6f, ty + 3f, tw - 8f, th - 6f), $"<color=#eaf3ee>{_tipText}</color>", _tipMulti);
                    else
                        GUI.Label(new Rect(tx, ty, tw, th), $"<color=#eaf3ee>{_tipText}</color>", _tip);
                }
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        // "N · ⌀gap" per day: total events + average gap between consecutive events (last-first)/(n-1)
        private static Dictionary<string, string> DayStats(List<string> days, Dictionary<string, long[]> byDay,
            Dictionary<string, DateTime> first, Dictionary<string, DateTime> last)
        {
            var res = new Dictionary<string, string>();
            foreach (var day in days)
            {
                long n = 0;
                if (byDay.TryGetValue(day, out var arr)) for (int i = 0; i < 24; i++) n += arr[i];
                string s = n.ToString();
                if (n >= 2 && first.TryGetValue(day, out var f) && last.TryGetValue(day, out var l))
                {
                    double gap = (l - f).TotalSeconds / (n - 1);
                    if (gap > 0) s += " · ⌀" + FmtGap(gap);
                }
                res[day] = s;
            }
            return res;
        }

        private static string FmtGap(double s)
        {
            if (s < 90) return ((int)s) + "s";
            if (s < 5400) return (s / 60.0).ToString("0.#") + "m";
            return (s / 3600.0).ToString("0.#") + "h";
        }

        // Draws one labeled heatmap grid (header + day gutter + 24 hour columns + per-day stats column
        // + hour-axis ticks). Returns the y just below the grid. Records a hover tooltip into _hasTip/….
        private float DrawGrid(float ix, float cy, float iw, float lh, float cellH, Vector2 mLocal,
            List<string> days, int dayRows, Dictionary<string, long[]> data, string header, long maxVisible, int metric,
            Dictionary<string, List<BoxOpenEvent>> evByCell, Dictionary<string, string> dayStats)
        {
            float statW = Mathf.Max(78f, iw * 0.17f);
            GUI.Label(new Rect(ix, cy, iw - statW, lh), $"<color=#9fb4cc>{header}</color>", _dim);
            GUI.Label(new Rect(ix + iw - statW, cy + 2, statW, lh), $"<size=9><color=#7a8390>{Loc.G("day_stats")}</color></size>", _tiny);
            cy += lh;

            if (dayRows == 0)
            {
                GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#8a93a0>{Loc.G("box_empty")}</color>", _tiny);
                return cy + lh;
            }

            float gutterW = Mathf.Max(40f, iw * 0.11f);
            float gridX = ix + gutterW;
            float gridW = iw - gutterW - statW;
            float cellW = gridW / 24f;

            float gy = cy;
            for (int r = 0; r < dayRows; r++)
            {
                string day = days[r];
                string shortDay = day.Length >= 10 ? day.Substring(5) : day;  // MM-DD
                GUI.Label(new Rect(ix, gy, gutterW - 2, cellH), $"<color=#aeb6c2>{shortDay}</color>", _tiny);
                if (dayStats != null && dayStats.TryGetValue(day, out var st))
                    GUI.Label(new Rect(gridX + gridW + 6, gy, statW - 6, cellH), $"<color=#9fb4cc>{st}</color>", _tiny);
                data.TryGetValue(day, out var arr);
                for (int hh = 0; hh < 24; hh++)
                {
                    long v = arr != null ? arr[hh] : 0;
                    var cr = new Rect(gridX + hh * cellW, gy, cellW - 1f, cellH - 1f);
                    DrawRect(cr.x, cr.y, cr.width, cr.height, RampColor(v / (float)maxVisible, metric));
                    if (cr.Contains(mLocal))
                    {
                        _hasTip = true; _tipCell = cr;
                        if (evByCell != null) _tipText = BuildOpenTip(evByCell, day, hh, shortDay);
                        else _tipText = $"{shortDay} {hh:00}:00 · {v}";
                    }
                }
                gy += cellH;
            }

            // hour axis (0,6,12,18)
            int[] ticks = { 0, 6, 12, 18 };
            foreach (int t in ticks)
                GUI.Label(new Rect(gridX + t * cellW, gy, cellW * 3, lh), $"<size=9><color=#7a8390>{t}</color></size>", _tiny);
            return gy + lh;
        }

        private static readonly string[] kindKeys = { "box_kind_normal", "box_kind_boss", "box_kind_actboss", "box_kind_unknown" };

        // Detailed multi-line tooltip for a bottom-grid (開箱紀錄) cell: header "MM-DD HH:00" then up to
        // 8 newest-first detail lines "HH:mm:ss   品質   箱種   物品", plus a "… +N more" overflow line.
        private static string BuildOpenTip(Dictionary<string, List<BoxOpenEvent>> evByCell, string day, int hh, string shortDay)
        {
            var sb = new StringBuilder();
            sb.Append(shortDay).Append(' ').Append(hh.ToString("00")).Append(":00");
            if (evByCell.TryGetValue(day + "|" + hh, out var lst) && lst != null && lst.Count > 0)
            {
                int n = lst.Count;
                int shown = n > 8 ? 8 : n;
                for (int i = 0; i < shown; i++)
                {
                    var e = lst[n - 1 - i];   // newest first
                    int k = (e.Kind >= 0 && e.Kind < 4) ? e.Kind : 3;
                    sb.Append('\n')
                      .Append(e.Time.ToString("HH:mm:ss")).Append("   ")
                      .Append("<color=").Append(BoxOpenOverlayBehaviour.GradeHex(e.Grade)).Append('>')
                      .Append(Loc.G("grade_" + BoxGrade.KeyOf(e.Grade))).Append("</color>   ")
                      .Append(Loc.G(kindKeys[k])).Append("   ")
                      .Append(ResolveItem(e.Name));
                }
                if (n > shown) sb.Append("\n… +").Append(n - shown).Append(" more");
            }
            return sb.ToString();
        }

        private static string ResolveItem(string key)
        {
            if (string.IsNullOrEmpty(key)) return "";
            int us = key.LastIndexOf('_');
            string digits = us >= 0 ? key.Substring(us + 1) : key;
            if (int.TryParse(digits, out int id)) { string nm = ItemNameStore.Get(id); if (!string.IsNullOrEmpty(nm)) return nm; }
            return key;
        }
    }
}
