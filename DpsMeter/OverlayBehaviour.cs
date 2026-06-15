using System;
using System.Collections.Generic;
using TaskbarHero;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>
    /// IMGUI overlay. Injected MonoBehaviour (note the IntPtr ctor required by
    /// Il2CppInterop). Shows headline numbers, a per-wave-tagged DPS curve with
    /// Y-axis scale and wave markers, and a colored damage-type distribution.
    /// Accumulates across a whole stage (resets only at the NONE stage boundary),
    /// auto-saves each finished stage, and lets you browse past runs with the arrows.
    /// </summary>
    public class OverlayBehaviour : MonoBehaviour
    {
        public OverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const float Pad = 8f;
        private const int GraphSeconds = 60;
        private const float SampleInterval = 0.5f;
        private const int GraphCapacity = (int)(GraphSeconds / SampleInterval);
        private const float HoldSeconds = 3f;   // freeze the meter after this much idle

        private float EffectiveNow(float now) => Mathf.Min(now, Plugin.Tracker.LastDamageTime + HoldSeconds);
        private bool IsFrozen(float now) => (now - Plugin.Tracker.LastDamageTime) > HoldSeconds;

        private Rect _rect = new Rect(24, 24, 320, 0);
        private bool _visible = true;
        private float _opacity = 0.78f;

        private Vector2 _dragOffset;
        private bool _dragging;
        private bool _placed;
        private float _wantX, _wantY;   // intended position; clamped non-destructively so window resizes don't move the panel

        // clickable regions (set during OnGUI, hit-tested in Update via InputCompat)
        private Rect _resetRect, _prevRect, _nextRect, _handleRect, _updRect;
        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();

        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        private Texture2D _white, _bgTex;
        private float _bgAlphaBaked = -1f;
        private GUIStyle _title, _big, _label, _dim, _tiny, _btn, _box;
        private bool _stylesReady;
        private int _builtFs = -1, _builtFsm = -1;   // font sizes the styles were last built with (live-rebuild on change)

        private readonly List<Sample> _history = new List<Sample>(GraphCapacity + 4);
        private readonly List<Sample> _takenHistory = new List<Sample>(GraphCapacity + 4);   // defense-side curve, saved with the run for review
        private float _nextSampleTime;
        private int _currentWave;

        // per-wave timing + active/idle accounting (stage-compare)
        private readonly List<float> _waveDurations = new List<float>();
        private float _waveStartTime = -1f;
        private float _activeSec, _idleSec;
        private float _prevDmgTime = -1f;

        // stage-boundary detection
        private StageManager _stage;
        private int _lastState = -999;
        // Deferred NONE handling: with a single-character party the game flickers through NONE
        // BETWEEN WAVES (multi-char uses REORGANIZATION), so NONE alone is not a run boundary.
        // We mark the NONE time and decide on the next MONSTERSPAWN: back within RoundGapSeconds
        // and same stage = inter-wave flicker (continue the run); otherwise = real round end.
        private const float RoundGapSeconds = 2f;
        private float _noneAt = -1f;
        private string _runStageId = "";
        private float _nextFindTime;
        private float _nextProbe;

        // review mode
        private List<RunRecord> _runs = new List<RunRecord>();
        private bool _runsLoaded;
        private int _reviewIndex = -1;   // -1 = live

        void Awake()
        {
            _opacity = Mathf.Clamp01(Plugin.Opacity.Value);
            _visible = Plugin.StartVisible.Value;
            _rect.width = Mathf.Max(280, Plugin.PanelWidth.Value);
            PanelRegistry.Register("dps", 0, "⚔", () => Loc.G("dps_title"), Plugin.ToggleKey, () => _visible, v => _visible = v);
        }

        void Start() => PlaceDefault();

        void OnDestroy() { try { InputCompat.UninstallMouseHook(); } catch { } }

        private void PlaceDefault()
        {
            float px = Plugin.PosX.Value, py = Plugin.PosY.Value;
            if (px < 0 || py < 0) { _rect.x = Mathf.Max(24, Screen.width - _rect.width - 24); _rect.y = Mathf.Max(24, Screen.height - 470f); } // bottom-right
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y;
            _placed = true;
        }

        void Update()
        {
            try
            {
                Loc.MaybeRefreshAuto();
                InputCompat.SetPanel(0, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (Plugin.DebugSnapshot != null && Plugin.DebugSnapshot.Value) GameUiState.Diag();
                PollStageState();
                CharacterReader.TickBoxes();   // catch transient box drops before they auto-open
                if (Plugin.PricePeekEnabled != null && Plugin.PricePeekEnabled.Value)
                    HeroProbe.PollHoveredItem();  // read the hovered item off the live ItemTooltip for the price box
                else HeroProbe.HoveredKey = 0;

                InputCompat.Poll();
                if (InputCompat.TogglePressed()) _visible = !_visible;
                if (_visible)
                {
                    bool ctrl = InputCompat.CtrlHeld();
                    if (InputCompat.OpacityUpPressed()) { if (ctrl) UiScale.Adjust(UiScale.Step); else SetOpacity(_opacity + 0.1f); }
                    if (InputCompat.OpacityDownPressed()) { if (ctrl) UiScale.Adjust(-UiScale.Step); else SetOpacity(_opacity - 0.1f); }
                    HandlePointer();
                }
                else if (_dragging) { _dragging = false; }

                if (Plugin.DebugDamage != null && Plugin.DebugDamage.Value)
                {
                    float ut = Time.unscaledTime;
                    if (ut >= _nextProbe)
                    {
                        _nextProbe = ut + 1f;
                        Plugin.Logger.LogInfo($"[input probe] {InputCompat.Probe()}  resetRect={_resetRect}");
                    }
                }

                // sample live DPS; freeze (stop growing) while idle/paused
                float now = Time.time;
                if (now >= _nextSampleTime)
                {
                    _nextSampleTime = now + SampleInterval;

                    // active vs idle: did any damage land since the previous tick?
                    // Idle deliberately includes "frozen" stretches — that downtime (running
                    // between packs) is exactly the "跑路" time we want; the _currentWave gate
                    // stops counting once the stage ends (NONE resets it to 0).
                    float dmgTime = Plugin.Tracker.LastDamageTime;
                    if (_currentWave >= 1 && _prevDmgTime >= 0f)
                    {
                        if (dmgTime > _prevDmgTime) _activeSec += SampleInterval;
                        else _idleSec += SampleInterval;
                    }
                    _prevDmgTime = dmgTime;

                    if (!IsFrozen(now))
                    {
                        float live = Plugin.Tracker.GetSnapshot(EffectiveNow(now)).LiveDps;
                        _history.Add(new Sample { Dps = live, Wave = _currentWave });
                        while (_history.Count > GraphCapacity) _history.RemoveAt(0);
                    }

                    // defense-side curve, sampled in lockstep so the saved run can replay it (its own freeze)
                    float tLast = Plugin.TakenTracker.LastDamageTime;
                    if ((now - tLast) <= HoldSeconds)
                    {
                        float tlive = Plugin.TakenTracker.GetSnapshot(Mathf.Min(now, tLast + HoldSeconds)).LiveDtps;
                        _takenHistory.Add(new Sample { Dps = tlive, Wave = _currentWave });
                        while (_takenHistory.Count > GraphCapacity) _takenHistory.RemoveAt(0);
                    }
                }
            }
            catch { }
        }

        // Pointer handling via polled input (IMGUI events don't fire under the new
        // Input System). Hit-tests the rects recorded by OnGUI.
        private void HandlePointer()
        {
            // while a game menu is open the panel is hidden — ignore the mouse so interacting with the
            // game's own UI can't accidentally grab/drag the (invisible) panel.
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(0); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            // resize grip (bottom-right): width only
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(0, m, ref rw, ref dh, 280f, Mathf.Max(280f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 300f; Plugin.PanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.PanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;

            if (InputCompat.MousePressed())
            {
                if (_resetRect.Contains(m)) { ResetMeter(); return; }
                if (_updRect.Contains(m) && Updater.State == Updater.St.Available) { Updater.DownloadAsync(); return; }
                if (_prevRect.Contains(m)) { NavOlder(); return; }
                if (_nextRect.Contains(m)) { NavNewer(); return; }
                // drag from anywhere on the panel (buttons handled above); claim ownership so
                // overlapping panels don't all drag at once
                if (_rect.Contains(m) && InputCompat.ClaimDrag(0)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }

            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(0)) { _dragging = false; }   // a panel on top stole the press
                else
                {
                if (Plugin.DebugDamage != null && Plugin.DebugDamage.Value)
                    Plugin.Logger.LogInfo($"[drag] m={m} rect=({_rect.x:0},{_rect.y:0}) {InputCompat.Probe()}");
                if (InputCompat.MouseHeld())
                {
                    _rect.x = m.x - _dragOffset.x;
                    _rect.y = m.y - _dragOffset.y;
                    UiScale.ClampToScreen(ref _rect, _scale);
                }
                if (InputCompat.MouseReleased())
                {
                    _dragging = false; InputCompat.ReleaseDrag(0);
                    _wantX = _rect.x; _wantY = _rect.y;
                    Plugin.PosX.Value = _rect.x;
                    Plugin.PosY.Value = _rect.y;
                }
                }
            }
        }

        private void ResetMeter()
        {
            Plugin.Tracker.StartEncounter(Time.time);
            _history.Clear();
            _takenHistory.Clear();
            _currentWave = 0;
            _reviewIndex = -1;
            _waveDurations.Clear();
            _waveStartTime = -1f;
            _activeSec = _idleSec = 0f;
            _prevDmgTime = -1f;
        }

        private void PollStageState()
        {
            if (_stage == null)
            {
                if (Time.time < _nextFindTime) return;
                _nextFindTime = Time.time + 1f;
                _stage = UnityEngine.Object.FindObjectOfType<StageManager>();
                if (_stage == null) return;
            }

            int state;
            try { state = (int)_stage.stageState; }
            catch { _stage = null; return; }

            BoxTracker.Tick(_stage);   // subscribe to OnGetBox on this stage instance (once)

            // pending-NONE timeout: no respawn within the gap window means the round really ended
            // (back to town / stopped farming) — finalize at the NONE moment, not now.
            if (_noneAt >= 0f && state == (int)EStageState.NONE && Time.time - _noneAt >= RoundGapSeconds)
                FinalizeRun(_noneAt);

            if (state == _lastState) return;
            _lastState = state;

            // Within a stage the state cycles MONSTERSPAWN->BATTLE->REORGANIZATION per wave
            // (multi-character), or MONSTERSPAWN->BATTLE->NONE (single character, NONE flicker).
            // A run therefore ends only when: NONE lingers past RoundGapSeconds, the next
            // MONSTERSPAWN arrives after that gap, or the stage id changes.
            var es = (EStageState)state;
            if (es == EStageState.NONE)
            {
                // close out the wave that was in progress, then wait — the next MONSTERSPAWN
                // (or the timeout above) decides whether this was a wave flicker or a round end.
                if (_currentWave >= 1 && _waveStartTime >= 0)
                {
                    float wd = Time.time - _waveStartTime;
                    if (wd > 0.05f) _waveDurations.Add(wd);
                    _waveStartTime = -1f;
                }
                _noneAt = Time.time;
            }
            else if (es == EStageState.MONSTERSPAWN)
            {
                string stageNow = "";
                try { stageNow = CharacterReader.CurrentStageId(); } catch { }
                bool stageChanged = _currentWave >= 1 && !string.IsNullOrEmpty(_runStageId)
                                    && !string.IsNullOrEmpty(stageNow) && stageNow != _runStageId;

                if (_noneAt >= 0f)
                {
                    float gap = Time.time - _noneAt;
                    _noneAt = -1f;
                    // long gap or different stage = the previous round really ended at the NONE moment
                    if (gap >= RoundGapSeconds || stageChanged) FinalizeRun(Time.time - gap);
                    // else: single-character inter-wave flicker — same run continues
                }
                else if (stageChanged)
                {
                    // stage switched mid-run without any NONE (jump via UI): close the old run now,
                    // stamped with the stage it was actually played on
                    FinalizeRun(Time.time);
                }

                // first wave of a run: snapshot rewards AND restart timing from the real stage start, so
                // the clear time excludes town/navigation time between stages (fixes the inflated first
                // run after switching stages). Subsequent stages re-zero here regardless of the prior NONE.
                if (_currentWave == 0)
                {
                    _runStageId = stageNow;
                    CharacterReader.CaptureRewardBaseline();
                    Plugin.Tracker.StartEncounter(Time.time);
                    Plugin.TakenTracker.StartEncounter(Time.time);
                    _history.Clear();
                    _takenHistory.Clear();
                    _activeSec = _idleSec = 0f;
                    _prevDmgTime = -1f;
                }
                // close the previous wave's timing, then start the next
                if (_currentWave >= 1 && _waveStartTime >= 0)
                {
                    float wd = Time.time - _waveStartTime;
                    if (wd > 0.05f) _waveDurations.Add(wd);
                }
                _currentWave++;
                _waveStartTime = Time.time;
                Plugin.CurrentWave = _currentWave;
            }
        }

        /// <summary>Close out the current run as of <paramref name="endTime"/> (save + reset). The run is
        /// stamped with the stage id captured at its start, so mid-run stage switches can't mislabel it.</summary>
        private void FinalizeRun(float endTime)
        {
            SaveCurrentRun(endTime);
            Plugin.Tracker.StartEncounter(Time.time);
            Plugin.TakenTracker.StartEncounter(Time.time);
            _history.Clear();
            _takenHistory.Clear();
            _currentWave = 0;
            Plugin.CurrentWave = 0;
            _waveDurations.Clear();
            _waveStartTime = -1f;
            _activeSec = _idleSec = 0f;
            _prevDmgTime = -1f;
            _noneAt = -1f;
            _runStageId = "";
        }

        private void SaveCurrentRun(float endTime)
        {
            try
            {
                var s = Plugin.Tracker.GetSnapshot(endTime);
                if (s.Hits <= 0 || s.Total <= 0) return;
                var r = new RunRecord
                {
                    Title = DateTime.Now.ToString("MM/dd HH:mm:ss"),
                    Total = s.Total,
                    Duration = s.DurationSeconds,
                    Peak = s.PeakDps,
                    Avg = s.AvgDps,
                    CritRate = s.CritRate,
                    CritShare = s.CritDamageShare,
                    Waves = _currentWave,
                    StageId = string.IsNullOrEmpty(_runStageId) ? CharacterReader.CurrentStageId() : _runStageId,
                    ActiveSeconds = _activeSec,
                    IdleSeconds = _idleSec,
                };
                foreach (var p in s.ByType) { r.TypeFlags.Add(p.Flag); r.TypeAmounts.Add(p.Amount); }
                foreach (var smp in _history) r.Samples.Add(smp);
                r.WaveDurations.AddRange(_waveDurations);
                r.Party.AddRange(CharacterReader.CaptureParty());
                CharacterReader.FillRewards(r);   // gold/exp/box deltas vs the run baseline

                // fold in the damage-taken (defense) side of the same encounter
                var ts = Plugin.TakenTracker.GetSnapshot(endTime);
                r.TakenTotal = ts.Total;
                r.TakenPeak = ts.PeakDtps;
                r.TakenAvg = ts.AvgDtps;
                r.TakenBiggestHit = ts.BiggestHit;
                r.TakenCritRate = ts.CritRate;
                r.TakenHits = ts.Hits;
                foreach (var p in ts.ByAttribute) { r.TakenAttrValues.Add(p.Key); r.TakenAttrAmounts.Add(p.Amount); }
                foreach (var p in ts.ByType) { r.TakenTypeFlags.Add(p.Key); r.TakenTypeAmounts.Add(p.Amount); }
                foreach (var smp in _takenHistory) r.TakenSamples.Add(smp);

                RunStore.Save(r);
                _runsLoaded = false;   // refresh list next time review opens
            }
            catch { }
        }

        private void SetOpacity(float v) { _opacity = Mathf.Clamp01(v); Plugin.Opacity.Value = _opacity; }

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
            _big = new GUIStyle { fontSize = fs + 9, fontStyle = FontStyle.Bold, richText = true };
            _big.normal.textColor = Color.white;
            _label = new GUIStyle { fontSize = fs, richText = true };
            _label.normal.textColor = new Color(0.93f, 0.93f, 0.93f);
            _dim = new GUIStyle { fontSize = fsm, richText = true };
            _dim.normal.textColor = new Color(0.78f, 0.84f, 0.95f);
            _tiny = new GUIStyle { fontSize = Mathf.Max(9, fsm - 2), richText = true };
            _tiny.normal.textColor = new Color(0.7f, 0.75f, 0.85f);
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
                if (reviewing && !_runsLoaded) { _runs = RunStore.LoadAll(); _runsLoaded = true; if (_reviewIndex >= _runs.Count) _reviewIndex = _runs.Count - 1; }
                if (reviewing && (_runs.Count == 0)) { _reviewIndex = -1; reviewing = false; }

                string title; float headline, peak, avg, crit, critShare, dur; double total; int waves;
                List<DpsTracker.TypePart> parts; List<Sample> samples;

                if (reviewing)
                {
                    var r = _runs[_reviewIndex];
                    title = $"<color=#7FB2FF>{Loc.G("review")}</color> {_reviewIndex + 1}/{_runs.Count}  {r.Title}";
                    headline = r.Avg; peak = r.Peak; avg = r.Avg; crit = r.CritRate; critShare = r.CritShare;
                    dur = r.Duration; total = r.Total; waves = r.Waves;
                    parts = BuildParts(r.TypeFlags, r.TypeAmounts, r.Total);
                    samples = r.Samples;
                }
                else
                {
                    var s = Plugin.Tracker.GetSnapshot(EffectiveNow(now));
                    string dot = !IsFrozen(now) ? "<color=#7CFC7C>●</color>" : "<color=#FFC857>‖</color>";
                    title = $"{dot} {Loc.G("dps_title")}  <size=11>{Loc.G("wave_short")}{_currentWave}</size>";
                    headline = s.LiveDps; peak = s.PeakDps; avg = s.AvgDps; crit = s.CritRate; critShare = s.CritDamageShare;
                    dur = s.DurationSeconds; total = s.Total; waves = _currentWave;
                    parts = s.ByType; samples = _history;
                }

                // ---- layout / sizing ----
                float graphH = 64f;
                float barH = 16f;
                int legendRows = Mathf.CeilToInt(Mathf.Min(parts.Count, 6) / 2f);
                bool showUpd = Updater.State == Updater.St.Available || Updater.State == Updater.St.Downloading
                    || Updater.State == Updater.St.Downloaded || Updater.State == Updater.St.Error;
                float height = Pad + lh /*header*/ + (showUpd ? lh : 0) /*update banner*/ + lh /*nav*/ + (fs + 12) /*big*/
                    + lh + lh /*peak/avg,total/dur*/ + lh /*crit*/
                    + 6 + graphH + 14 /*graph + x labels*/ + 6 + barH
                    + (legendRows > 0 ? lh * legendRows : 0) + Pad;
                _rect.height = height;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                // keep the panel on-screen, but clamp from the INTENDED position (not the live one) so a
                // transient window resize (e.g. opening/closing a game menu) can't permanently shove it.
                if (!_dragging)
                {
                    _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale));
                    _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale));
                }
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);

                float cy = _rect.y + Pad;

                // header row:  title  …  [−  UI NN%  +]  [Reset]   (clicks handled in Update via recorded rects)
                _handleRect = new Rect(x, _rect.y, w - 64, lh + Pad);
                _resetRect = new Rect(x + w - 56, cy - 1, 50, lh + 2);
                GUI.Button(_resetRect, Loc.G("reset"), _btn);
                GUI.Label(new Rect(ix, cy, Mathf.Max(40f, _resetRect.x - ix - 4), lh), title, _title);
                cy += lh;

                // update banner (auto-check). [download] button hit-tested in HandlePointer.
                _updRect = new Rect(0, 0, 0, 0);
                if (showUpd)
                {
                    switch (Updater.State)
                    {
                        case Updater.St.Available:
                            GUI.Label(new Rect(ix, cy, iw - 70, lh), $"<color=#FFC857>🔄 v{Updater.LatestVersion} {Loc.G("update_available")}</color>", _dim);
                            _updRect = new Rect(x + w - 64, cy - 1, 56, lh);
                            GUI.Button(_updRect, Loc.G("download"), _btn);
                            break;
                        case Updater.St.Downloading:
                            GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>⬇ {Loc.G("downloading")}</color>", _dim); break;
                        case Updater.St.Downloaded:
                            GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#5fd07c>✅ {Loc.G("restart_apply")}</color>", _dim); break;
                        case Updater.St.Error:
                            GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#ef6a5a>⚠ {Loc.G("update_error")}</color>", _dim); break;
                    }
                    cy += lh;
                }

                // nav row: ◀  (live/review)  ▶
                _prevRect = new Rect(ix, cy, 30, lh);
                _nextRect = new Rect(ix + 34, cy, 30, lh);
                GUI.Button(_prevRect, "◀", _btn);
                GUI.Button(_nextRect, "▶", _btn);
                GUI.Label(new Rect(ix + 70, cy, iw - 70, lh), reviewing ? Loc.G("review_hint") : Loc.G("live_hint"), _dim);
                cy += lh;

                // headline
                GUI.Label(new Rect(ix, cy, iw, fs + 12), Fmt(headline) + (reviewing ? "  <size=11>" + Loc.G("review_tag") + "</size>" : ""), _big);
                cy += fs + 12;

                GUI.Label(new Rect(ix, cy, iw, lh), $"{Loc.G("peak")} {Fmt(peak)}    {Loc.G("avg")} {Fmt(avg)}", _label); cy += lh;
                GUI.Label(new Rect(ix, cy, iw, lh), $"{Loc.G("total_dealt")} {Fmt(total)}    {Loc.G("duration")} {dur:0.0}s    {waves}{Loc.G("wave_short")}", _label); cy += lh;
                GUI.Label(new Rect(ix, cy, iw, lh), $"{Loc.G("crit")} {crit * 100f:0.#}%    {Loc.G("crit_share")} {critShare * 100f:0.#}%", _label); cy += lh;

                cy += 6;
                DrawGraph(ix, cy, iw, graphH, samples);
                cy += graphH + 14;

                cy += 0;
                DrawDistribution(ix, cy, iw, barH, parts, total, lh);
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        private void NavOlder()
        {
            if (!_runsLoaded) { _runs = RunStore.LoadAll(); _runsLoaded = true; }
            if (_runs.Count == 0) return;
            if (_reviewIndex < 0) _reviewIndex = _runs.Count - 1;   // jump to newest saved
            else if (_reviewIndex > 0) _reviewIndex--;
        }

        private void NavNewer()
        {
            if (_reviewIndex < 0) return;
            _reviewIndex++;
            if (_reviewIndex >= _runs.Count) _reviewIndex = -1;     // back to live
        }

        private static List<DpsTracker.TypePart> BuildParts(List<int> flags, List<double> amts, double total)
        {
            var parts = new List<DpsTracker.TypePart>();
            if (total <= 0) return parts;
            for (int i = 0; i < flags.Count && i < amts.Count; i++)
            {
                if (amts[i] <= 0) continue;
                parts.Add(new DpsTracker.TypePart { Flag = flags[i], Name = DpsTracker.DecodeName(flags[i]), Amount = amts[i], Share = (float)(amts[i] / total) });
            }
            parts.Sort((a, b) => b.Amount.CompareTo(a.Amount));
            return parts;
        }

        private void DrawGraph(float x, float y, float w, float h, List<Sample> samples)
        {
            DrawRect(x, y, w, h, new Color(0f, 0f, 0f, 1f));
            DrawRect(x, y + h - 1, w, 1, new Color(1f, 1f, 1f, 0.25f));   // baseline

            int n = samples != null ? samples.Count : 0;
            float max = 1f;
            for (int i = 0; i < n; i++) if (samples[i].Dps > max) max = samples[i].Dps;

            // Y-axis gridlines + labels (max, half)
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
                var col = new Color(0.25f + 0.7f * t, 0.6f - 0.35f * t, 0.95f - 0.55f * t, 0.9f);
                DrawRect(cx, y + h - bh - 1, Mathf.Max(1f, colW), bh, col);

                // wave divider + number on X axis
                if (samples[i].Wave != lastWave)
                {
                    lastWave = samples[i].Wave;
                    if (i > 0) DrawRect(cx, y, 1, h, new Color(1f, 1f, 1f, 0.22f));
                    GUI.Label(new Rect(cx + 1, y + h + 1, 28, 12), lastWave.ToString(), _tiny);
                }
            }
        }

        private void DrawDistribution(float x, float y, float w, float h, List<DpsTracker.TypePart> parts, double total, float lh)
        {
            DrawRect(x, y, w, h, new Color(0f, 0f, 0f, 1f));
            if (parts == null || parts.Count == 0 || total <= 0) return;

            float cx = x;
            foreach (var p in parts) { float segW = p.Share * w; DrawRect(cx, y, segW, h, ColorForFlag(p.Flag)); cx += segW; }

            float ly = y + h + 3;
            float colW = w / 2f;
            int shown = 0, col = 0;
            foreach (var p in parts)
            {
                if (shown >= 6) break;
                float lx = x + col * colW;
                DrawRect(lx, ly + 3, 10, 10, ColorForFlag(p.Flag));
                GUI.Label(new Rect(lx + 14, ly, colW - 14, lh), $"{Loc.Name(p.Name)} {p.Share * 100f:0.#}%", _dim);
                shown++; col++;
                if (col >= 2) { col = 0; ly += lh; }
            }
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

        private static string Fmt(double v)
        {
            if (v >= 1e9) return (v / 1e9).ToString("0.##") + "B";
            if (v >= 1e6) return (v / 1e6).ToString("0.##") + "M";
            if (v >= 1e3) return (v / 1e3).ToString("0.##") + "K";
            return v.ToString("0");
        }
    }
}
