using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>
    /// IMGUI overlay for damage the player's heroes TAKE. A live-only sibling of
    /// <see cref="OverlayBehaviour"/> (no saved-run review): headline DTPS, peak/avg,
    /// total/duration/biggest-hit, hits/incoming-crit, a DTPS curve, and two
    /// distribution bars (element attribute + damage type). Its own toggle key and
    /// draggable position. Wave numbers are read from the shared Plugin.CurrentWave.
    /// </summary>
    public class TakenOverlayBehaviour : MonoBehaviour
    {
        public TakenOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const float Pad = 8f;
        private const int GraphSeconds = 60;
        private const float SampleInterval = 0.5f;
        private const int GraphCapacity = (int)(GraphSeconds / SampleInterval);
        private const float HoldSeconds = 3f;

        private float EffectiveNow(float now) => Mathf.Min(now, Plugin.TakenTracker.LastDamageTime + HoldSeconds);
        private bool IsFrozen(float now) => (now - Plugin.TakenTracker.LastDamageTime) > HoldSeconds;

        private Rect _rect = new Rect(24, 24, 320, 0);
        private bool _visible = true;
        private float _opacity = 0.78f;

        private Vector2 _dragOffset;
        private bool _dragging;
        private bool _placed;
        private float _wantX, _wantY;   // intended position; clamped non-destructively (resize-safe)

        private Rect _resetRect, _prevRect, _nextRect, _handleRect;
        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();
        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        private Texture2D _white, _bgTex;
        private float _bgAlphaBaked = -1f;
        private GUIStyle _title, _big, _label, _dim, _tiny, _btn, _box;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;   // font sizes the styles were last built with (live-rebuild on change)

        private readonly List<Sample> _history = new List<Sample>(GraphCapacity + 4);
        private float _nextSampleTime;
        private int _lastSeenWave;

        // review mode (mirrors the DPS panel: ◀/▶ browse saved runs)
        private List<RunRecord> _runs = new List<RunRecord>();
        private int _reviewIndex = -1;   // -1 = live

        void Awake()
        {
            _opacity = Mathf.Clamp01(Plugin.Opacity.Value);
            _visible = Plugin.TakenStartVisible.Value;
            _rect.width = Mathf.Max(280, Plugin.TakenPanelWidth.Value);
            PanelRegistry.Register("taken", 1, "❤", () => Loc.G("taken_title"), Plugin.TakenToggleKey, () => _visible, v => _visible = v);
        }

        void Start() => PlaceDefault();

        private void PlaceDefault()
        {
            float px = Plugin.TakenPosX.Value, py = Plugin.TakenPosY.Value;
            // default: bottom-left (DPS panel defaults to bottom-right)
            if (px < 0 || py < 0) { _rect.x = 24; _rect.y = Mathf.Max(24, Screen.height - 470f); }
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y;
            _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.SetPanel(1, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (InputCompat.TogglePressed(Plugin.TakenToggleKey)) _visible = !_visible;
                if (_visible)
                {
                    // opacity shares the DPS panel's PageUp/PageDown control; just track the value
                    _opacity = Mathf.Clamp01(Plugin.Opacity.Value);
                    HandlePointer();
                }
                else if (_dragging) { _dragging = false; }

                // clear our graph history when the shared wave counter resets (new stage)
                int wave = Plugin.CurrentWave;
                if (wave < _lastSeenWave) _history.Clear();
                _lastSeenWave = wave;

                float now = Time.time;
                if (now >= _nextSampleTime)
                {
                    _nextSampleTime = now + SampleInterval;
                    if (!IsFrozen(now))
                    {
                        float live = Plugin.TakenTracker.GetSnapshot(EffectiveNow(now)).LiveDtps;
                        _history.Add(new Sample { Dps = live, Wave = wave });
                        while (_history.Count > GraphCapacity) _history.RemoveAt(0);
                    }
                }
            }
            catch { }
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(1); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            // resize grip (bottom-right): width only
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(1, m, ref rw, ref dh, 280f, Mathf.Max(280f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 300f; Plugin.TakenPanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.TakenPanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;

            if (InputCompat.MousePressed())
            {
                if (_resetRect.Contains(m)) { ResetMeter(); return; }
                if (_prevRect.Contains(m)) { NavOlder(); return; }
                if (_nextRect.Contains(m)) { NavNewer(); return; }
                if (_rect.Contains(m) && InputCompat.ClaimDrag(1)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }

            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(1)) { _dragging = false; }   // a panel on top stole the press
                else
                {
                    if (InputCompat.MouseHeld())
                    {
                        _rect.x = m.x - _dragOffset.x;
                        _rect.y = m.y - _dragOffset.y;
                        UiScale.ClampToScreen(ref _rect, _scale);
                    }
                    if (InputCompat.MouseReleased())
                    {
                        _dragging = false; InputCompat.ReleaseDrag(1);
                        _wantX = _rect.x; _wantY = _rect.y;
                        Plugin.TakenPosX.Value = _rect.x;
                        Plugin.TakenPosY.Value = _rect.y;
                    }
                }
            }
        }

        private void ResetMeter()
        {
            Plugin.TakenTracker.StartEncounter(Time.time);
            _history.Clear();
            _reviewIndex = -1;
        }

        // ◀ : step to an older saved run (entering review reloads the list so new runs show)
        private void NavOlder()
        {
            if (_reviewIndex < 0)
            {
                _runs = RunStore.LoadAll();
                if (_runs.Count == 0) return;
                _reviewIndex = _runs.Count - 1;   // jump to newest saved
                return;
            }
            if (_runs.Count == 0) return;
            if (_reviewIndex > 0) _reviewIndex--;
        }

        // ▶ : step to a newer run, wrapping past the newest back to live
        private void NavNewer()
        {
            if (_reviewIndex < 0) return;
            _reviewIndex++;
            if (_reviewIndex >= _runs.Count) _reviewIndex = -1;   // back to live
        }

        private static List<DamageTakenTracker.Part> BuildAttrParts(List<int> vals, List<double> amts, double total)
        {
            var parts = new List<DamageTakenTracker.Part>();
            if (total <= 0) return parts;
            for (int i = 0; i < vals.Count && i < amts.Count; i++)
            {
                if (amts[i] <= 0) continue;
                parts.Add(new DamageTakenTracker.Part
                {
                    Key = vals[i],
                    Name = DamageTakenTracker.DecodeAttribute(vals[i]),
                    Amount = amts[i],
                    Share = (float)(amts[i] / total),
                });
            }
            parts.Sort((a, b) => b.Amount.CompareTo(a.Amount));
            return parts;
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
            _title.normal.textColor = new Color(1f, 0.55f, 0.45f);
            _big = new GUIStyle { fontSize = fs + 9, fontStyle = FontStyle.Bold, richText = true };
            _big.normal.textColor = Color.white;
            _label = new GUIStyle { fontSize = fs, richText = true };
            _label.normal.textColor = new Color(0.93f, 0.93f, 0.93f);
            _dim = new GUIStyle { fontSize = fsm, richText = true };
            _dim.normal.textColor = new Color(0.95f, 0.84f, 0.78f);
            _tiny = new GUIStyle { fontSize = Mathf.Max(9, fsm - 2), richText = true };
            _tiny.normal.textColor = new Color(0.85f, 0.75f, 0.7f);
            _btn = new GUIStyle(GUI.skin.button) { fontSize = fsm, fontStyle = FontStyle.Bold, richText = true };
            _box = new GUIStyle();
            _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _big, _label, _dim, _tiny, _btn);
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
            GUI.depth = 10;   // below the F11 compare panel
            var prevM = GUI.matrix;
            try
            {
                EnsureAssets();
                if (!_placed) PlaceDefault();

                int fs = Plugin.FontSize.Value;
                float lh = fs + 7;
                float x = _rect.x, w = _rect.width;
                float ix = x + Pad, iw = w - Pad * 2;
                float now = Time.time;

                // ---- gather the view: live or a saved run ----
                bool reviewing = _reviewIndex >= 0;
                if (reviewing && _runs.Count == 0) { _reviewIndex = -1; reviewing = false; }

                string title; float headline, peak, avg, biggest, crit, dur; double total; long hits;
                List<DamageTakenTracker.Part> attrParts; List<Sample> samples;

                if (reviewing)
                {
                    var r = _runs[_reviewIndex];
                    title = $"<color=#FF9E7F>{Loc.G("review")}</color> {_reviewIndex + 1}/{_runs.Count}  {r.Title}";
                    headline = r.TakenAvg; peak = r.TakenPeak; avg = r.TakenAvg;
                    biggest = r.TakenBiggestHit; crit = r.TakenCritRate; dur = r.Duration;
                    total = r.TakenTotal; hits = r.TakenHits;
                    attrParts = BuildAttrParts(r.TakenAttrValues, r.TakenAttrAmounts, r.TakenTotal);
                    samples = r.TakenSamples;
                }
                else
                {
                    var s = Plugin.TakenTracker.GetSnapshot(EffectiveNow(now));
                    string dot = !IsFrozen(now) ? "<color=#FF6B5C>●</color>" : "<color=#FFC857>‖</color>";
                    title = $"{dot} {Loc.G("taken_title")}  <size=11>{Loc.G("wave_short")}{Plugin.CurrentWave}</size>";
                    headline = s.LiveDtps; peak = s.PeakDtps; avg = s.AvgDtps;
                    biggest = s.BiggestHit; crit = s.CritRate; dur = s.DurationSeconds;
                    total = s.Total; hits = s.Hits;
                    attrParts = s.ByAttribute; samples = _history;
                }

                // ---- layout / sizing ----
                float graphH = 64f;
                float barH = 14f;
                int attrRows = Mathf.CeilToInt(Mathf.Min(attrParts.Count, 6) / 2f);
                // NOTE: incoming (monster) damage carries no EDamageType (always None),
                // so the type bar is meaningless for the taken panel — only the element
                // attribute breakdown (Physical/Fire/Cold/...) is shown.
                float labelH = Mathf.Max(9, Plugin.FontSizeSmall.Value - 2) + 6;   // _tiny line height (don't clip at big fonts)
                float height = Pad + lh /*header*/ + lh /*nav*/ + (fs + 12) /*big*/
                    + lh + lh + lh /*peak/avg, total/dur, biggest/hits/crit*/
                    + 6 + graphH + 14 /*graph + x labels*/
                    + 6 + labelH /*attr label*/ + barH + (attrRows > 0 ? 3 + lh * attrRows : 0)
                    + Pad;
                _rect.height = height;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                // clamp from the intended position so a transient window resize can't permanently move it
                if (!_dragging)
                {
                    _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale));
                    _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale));
                }
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                float cy = _rect.y + Pad;

                // header + Reset
                _handleRect = new Rect(x, _rect.y, w - 64, lh + Pad);
                GUI.Label(new Rect(ix, cy, iw - 56, lh), title, _title);
                _resetRect = new Rect(x + w - 56, cy - 1, 50, lh + 2);
                GUI.Button(_resetRect, Loc.G("reset"), _btn);
                cy += lh;

                // nav row: ◀  (live/review)  ▶
                _prevRect = new Rect(ix, cy, 30, lh);
                _nextRect = new Rect(ix + 34, cy, 30, lh);
                GUI.Button(_prevRect, "◀", _btn);
                GUI.Button(_nextRect, "▶", _btn);
                GUI.Label(new Rect(ix + 70, cy, iw - 70, lh), reviewing ? Loc.G("review_hint") : Loc.G("live_hint"), _dim);
                cy += lh;

                // headline DTPS
                GUI.Label(new Rect(ix, cy, iw, fs + 12), Fmt(headline) + "  <size=11>" + (reviewing ? Loc.G("review_tag") : Loc.G("per_sec_taken")) + "</size>", _big);
                cy += fs + 12;

                GUI.Label(new Rect(ix, cy, iw, lh), $"{Loc.G("peak")} {Fmt(peak)}    {Loc.G("avg")} {Fmt(avg)}", _label); cy += lh;
                GUI.Label(new Rect(ix, cy, iw, lh), $"{Loc.G("total_taken")} {Fmt(total)}    {Loc.G("duration")} {dur:0.0}s", _label); cy += lh;
                GUI.Label(new Rect(ix, cy, iw, lh), $"{Loc.G("biggest_hit")} {Fmt(biggest)}    {Loc.G("hits")} {hits}    {Loc.G("incoming_crit")} {crit * 100f:0.#}%", _label); cy += lh;

                cy += 6;
                DrawGraph(ix, cy, iw, graphH, samples);
                cy += graphH + 14;

                // element attribute distribution (the meaningful breakdown for incoming damage)
                cy += 6;
                GUI.Label(new Rect(ix, cy, iw, labelH), Loc.G("element_dist"), _tiny); cy += labelH;
                cy = DrawDistribution(ix, cy, iw, barH, attrParts, total, lh, isAttribute: true);
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        private void DrawGraph(float x, float y, float w, float h, List<Sample> samples)
        {
            DrawRect(x, y, w, h, new Color(0f, 0f, 0f, 1f));
            DrawRect(x, y + h - 1, w, 1, new Color(1f, 1f, 1f, 0.25f));

            int n = samples != null ? samples.Count : 0;
            float max = 1f;
            for (int i = 0; i < n; i++) if (samples[i].Dps > max) max = samples[i].Dps;

            DrawRect(x, y + h * 0.5f, w, 1, new Color(1f, 1f, 1f, 0.10f));
            GUI.Label(new Rect(x + 2, y - 1, 70, 14), Fmt(max), _tiny);
            GUI.Label(new Rect(x + 2, y + h * 0.5f - 1, 70, 14), Fmt(max * 0.5f), _tiny);

            if (n < 1) return;
            float colW = w / GraphCapacity;
            int lastWave = int.MinValue;
            for (int i = 0; i < n; i++)
            {
                float val = samples[i].Dps;
                float t = Mathf.Clamp01(val / max);
                float bh = t * (h - 2);
                float cx = x + i * colW;
                // red-hot gradient: hotter = more damage taken
                var col = new Color(0.95f, 0.55f - 0.35f * t, 0.45f - 0.35f * t, 0.9f);
                DrawRect(cx, y + h - bh - 1, Mathf.Max(1f, colW), bh, col);

                if (samples[i].Wave != lastWave)
                {
                    lastWave = samples[i].Wave;
                    if (i > 0) DrawRect(cx, y, 1, h, new Color(1f, 1f, 1f, 0.22f));
                    GUI.Label(new Rect(cx + 1, y + h + 1, 28, 12), lastWave.ToString(), _tiny);
                }
            }
        }

        /// <summary>Draws a stacked bar + a 2-column legend; returns the new cursor Y.</summary>
        private float DrawDistribution(float x, float y, float w, float h, List<DamageTakenTracker.Part> parts, double total, float lh, bool isAttribute)
        {
            DrawRect(x, y, w, h, new Color(0f, 0f, 0f, 1f));
            if (parts == null || parts.Count == 0 || total <= 0) return y + h;

            float cx = x;
            foreach (var p in parts) { float segW = p.Share * w; DrawRect(cx, y, segW, h, ColorFor(p.Key, isAttribute)); cx += segW; }

            float ly = y + h + 3;
            float colW = w / 2f;
            int shown = 0, col = 0;
            foreach (var p in parts)
            {
                if (shown >= 6) break;
                float lx = x + col * colW;
                DrawRect(lx, ly + 3, 10, 10, ColorFor(p.Key, isAttribute));
                string label = Loc.Name(p.Name);
                GUI.Label(new Rect(lx + 14, ly, colW - 14, lh), $"{label} {p.Share * 100f:0.#}%", _dim);
                shown++; col++;
                if (col >= 2) { col = 0; ly += lh; }
            }
            int rows = Mathf.CeilToInt(Mathf.Min(parts.Count, 6) / 2f);
            return y + h + (rows > 0 ? 3 + lh * rows : 0);   // legend starts at y+h+3
        }

        private static Color ColorFor(int key, bool isAttribute)
            => isAttribute ? ColorForAttribute(key) : ColorForType(key);

        private static Color ColorForAttribute(int value)
        {
            switch (value)
            {
                case 0: return new Color(0.70f, 0.72f, 0.78f); // Physical - grey
                case 1: return new Color(0.95f, 0.42f, 0.24f); // Fire - orange-red
                case 2: return new Color(0.35f, 0.78f, 0.95f); // Cold - cyan
                case 3: return new Color(0.97f, 0.86f, 0.30f); // Lightning - yellow
                case 4: return new Color(0.66f, 0.40f, 0.90f); // Chaos - purple
                case 5: return new Color(0.85f, 0.85f, 0.95f); // AllElement
                default: return new Color(0.55f, 0.55f, 0.55f); // None / unknown
            }
        }

        private static Color ColorForType(int flag)
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
            foreach (var bit in bits) if ((flag & bit) != 0) { var c = ColorForType(bit); r += c.r; g += c.g; b += c.b; cnt++; }
            return cnt == 0 ? new Color(0.6f, 0.6f, 0.6f) : new Color(r / cnt, g / cnt, b / cnt);
        }

        private static string Fmt(double v)
        {
            if (v >= 1e9) return (v / 1e9).ToString("0.##") + "B";
            if (v >= 1e6) return (v / 1e6).ToString("0.##") + "M";
            if (v >= 1e3) return (v / 1e3).ToString("0.##") + "K";
            return v.ToString("0");
        }
    }
}
