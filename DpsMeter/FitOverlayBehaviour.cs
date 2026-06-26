using System;
using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>Loads the bundled gear/material DB (extracted from the game's CSVs) into GearDatabase once.</summary>
    internal static class FitDataStore
    {
        private static bool _tried;
        public static void Ensure()
        {
            if (_tried || GearDatabase.Loaded) return;
            _tried = true;
            try
            {
                var asm = typeof(FitDataStore).Assembly;
                GearDatabase.LoadGear(ReadRes(asm, "fit_gear.json"));
                GearDatabase.LoadMats(ReadRes(asm, "fit_mats.json"));
                Plugin.Logger?.LogInfo($"[fit] gear DB: {GearDatabase.Count} items loaded");
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("FitDataStore: " + e.Message); }
        }
        private static string ReadRes(System.Reflection.Assembly asm, string suffix)
        {
            foreach (var n in asm.GetManifestResourceNames())
                if (n.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                    using (var s = asm.GetManifestResourceStream(n))
                    using (var r = new System.IO.StreamReader(s))
                        return r.ReadToEnd();
            return "";
        }
    }

    /// <summary>IMGUI overlay (hub-only): the EVE-style fitting bench. Pick a hero, swap any item into any of
    /// the 10 gear slots from the full game item DB, and see the resulting stats + predicted DPS/clear-times
    /// live. DPS is anchored to the hero's measured DPS and scaled by the (currently placeholder) damage
    /// formula's ratio between the sandbox loadout and the real one.</summary>
    public class FitOverlayBehaviour : MonoBehaviour
    {
        public FitOverlayBehaviour(IntPtr ptr) : base(ptr) { }

        private const int Slot = 12;
        private const float Pad = 10f;
        // gear slot index -> (PARTS key in the DB, short label)
        private static readonly string[] SlotParts = { "MAIN_WEAPON", "SUB_WEAPON", "HELMET", "ARMOR", "GLOVES", "BOOTS", "AMULET", "EARING", "RING", "BRACER" };
        private static readonly string[] SlotLabel = { "主武", "副武", "頭盔", "鎧甲", "手套", "靴", "護符", "耳環", "戒指", "護腕" };

        private Rect _rect = new Rect(90, 90, 560, 0);
        private bool _visible, _placed;
        private float _wantX, _wantY;
        private Vector2 _dragOffset; private bool _dragging;

        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box, _col;
        private bool _stylesReady; private int _builtFs = -1, _builtFsm = -1;

        private int _seenVersion = -1; private bool _loaded;
        private readonly List<int> _heroes = new List<int>();
        private int _heroIdx;
        private readonly Dictionary<int, int[]> _orig = new Dictionary<int, int[]>();  // real equipped (anchor)
        private readonly Dictionary<int, int[]> _load = new Dictionary<int, int[]>();   // sandbox (editable)
        private readonly Dictionary<int, double> _measDps = new Dictionary<int, double>();
        private readonly Dictionary<int, List<int[]>> _heroMats = new Dictionary<int, List<int[]>>();  // hero -> [matKey,tier]
        private int _picker = -1;       // slot whose item list is open (-1 = main view)
        private bool _matPicker;        // material-list picker open
        private int _pickerPage;
        private string _pickGrade = "";  // active grade-filter chip in the picker ("" = all)
        private Rect _addMatRect;
        private readonly List<Rect> _matRmRects = new List<Rect>();
        private readonly List<Rect> _gradeRects = new List<Rect>();   // grade-chip hitboxes
        private readonly List<string> _gradeKeys = new List<string>();
        // TBH's 10-tier rarity ladder (ascending) + short CJK labels, for the picker's grade chips.
        private static readonly string[] GradeLadder = { "COMMON", "UNCOMMON", "RARE", "LEGENDARY", "IMMORTAL", "ARCANA", "BEYOND", "CELESTIAL", "DIVINE", "COSMIC" };
        private static readonly string[] GradeLabel = { "普通", "罕見", "稀有", "傳奇", "不朽", "至寶", "超凡", "天界", "神聖", "宇宙" };

        private Rect _closeRect, _resetRect, _backRect;
        private readonly List<Rect> _tabRects = new List<Rect>();
        private readonly List<Rect> _swapRects = new List<Rect>();
        private readonly List<Rect> _pickRects = new List<Rect>();
        private readonly List<int> _pickKeys = new List<int>();
        private Rect _ppPrev, _ppNext;

        private float _scale = 1f;
        private readonly PanelResize _resize = new PanelResize();
        private Rect ScaledRect() => new Rect(_rect.x, _rect.y, _rect.width * _scale, _rect.height * _scale);

        void Awake()
        {
            _rect.width = Mathf.Max(560, Plugin.FitPanelWidth.Value);
            _visible = Plugin.FitStartVisible.Value;
            PanelRegistry.Register("fit", 4, "⚒", () => Loc.G("fit_title"), KeyCode.None, () => _visible, v => _visible = v);
        }
        void Start() => PlaceDefault();
        private void PlaceDefault()
        {
            float px = Plugin.FitPosX.Value, py = Plugin.FitPosY.Value;
            if (px < 0 || py < 0) { _rect.x = Mathf.Max(24, (Screen.width - _rect.width) * 0.5f); _rect.y = 90f; }
            else { _rect.x = px; _rect.y = py; }
            _wantX = _rect.x; _wantY = _rect.y; _placed = true;
        }

        void Update()
        {
            try
            {
                InputCompat.SetPanel(Slot, _visible && !GameUiState.MenuOpen(), ScaledRect());
                if (_visible && RunStore.Version != _seenVersion) Reload();
                if (_visible) HandlePointer();
                else if (_dragging) _dragging = false;
            }
            catch { }
        }

        private void Reload()
        {
            _seenVersion = RunStore.Version;
            FitDataStore.Ensure();
            _heroes.Clear(); _orig.Clear(); _load.Clear(); _measDps.Clear();
            // current equipped gear per hero (ItemKey by slot0..slot9)
            try
            {
                var party = SaveGearReader.ReadParty();
                foreach (var kv in party)
                {
                    var arr = new int[SlotParts.Length];
                    foreach (var g in kv.Value)
                    {
                        if (g == null || string.IsNullOrEmpty(g.Slot) || !g.Slot.StartsWith("slot")) continue;
                        int si; if (!int.TryParse(g.Slot.Substring(4), out si)) continue;
                        if (si >= 0 && si < arr.Length) arr[si] = g.ItemKey;
                    }
                    _orig[kv.Key] = arr;
                    var copy = new int[arr.Length]; Array.Copy(arr, copy, arr.Length); _load[kv.Key] = copy;
                    _heroes.Add(kv.Key);
                }
            }
            catch { }
            // measured per-hero DPS (anchor) from the newest run that has it
            try
            {
                var runs = RunStore.LoadAll();
                for (int i = runs.Count - 1; i >= 0 && _measDps.Count == 0; i--)
                {
                    var r = runs[i]; if (r == null || r.Party == null) continue;
                    double active = r.ActiveSeconds > 0 ? r.ActiveSeconds : r.Duration; if (active <= 0) continue;
                    foreach (var snap in r.Party)
                    {
                        if (snap == null || snap.DamageDealt <= 0) continue;
                        int key; if (!int.TryParse(snap.Character, out key)) continue;
                        _measDps[key] = snap.DamageDealt / active;
                    }
                }
            }
            catch { }
            if (_heroIdx >= _heroes.Count) _heroIdx = 0;
            _picker = -1;
            _loaded = true;
        }

        private int CurHero => (_heroIdx >= 0 && _heroIdx < _heroes.Count) ? _heroes[_heroIdx] : 0;

        private List<int> KeysOf(Dictionary<int, int[]> src, int hero)
        {
            var l = new List<int>();
            if (src.TryGetValue(hero, out var arr)) foreach (var k in arr) if (k != 0) l.Add(k);
            return l;
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(Slot); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            float rw = _rect.width, dh = 0f;
            var rr = _resize.Handle(Slot, m, ref rw, ref dh, 460f, Mathf.Max(460f, Screen.width * 0.95f), 0f, 0f, false);
            _rect.width = rw;
            if (rr == PanelResize.Result.Reset) { _rect.width = 560f; Plugin.FitPanelWidth.Value = _rect.width; return; }
            if (rr == PanelResize.Result.Committed) { Plugin.FitPanelWidth.Value = _rect.width; return; }
            if (rr != PanelResize.Result.None) return;
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                if (_picker >= 0)
                {
                    if (_backRect.Contains(m)) { _picker = -1; return; }
                    for (int i = 0; i < _gradeRects.Count && i < _gradeKeys.Count; i++)
                        if (_gradeRects[i].Contains(m)) { _pickGrade = _gradeKeys[i]; _pickerPage = 0; return; }
                    if (_ppPrev.Contains(m)) { _pickerPage = Mathf.Max(0, _pickerPage - 1); return; }
                    if (_ppNext.Contains(m)) { _pickerPage++; return; }
                    for (int i = 0; i < _pickRects.Count && i < _pickKeys.Count; i++)
                        if (_pickRects[i].Contains(m)) { SetSlot(_picker, _pickKeys[i]); _picker = -1; return; }
                    return;
                }
                if (_matPicker)
                {
                    if (_backRect.Contains(m)) { _matPicker = false; return; }
                    if (_ppPrev.Contains(m)) { _pickerPage = Mathf.Max(0, _pickerPage - 1); return; }
                    if (_ppNext.Contains(m)) { _pickerPage++; return; }
                    for (int i = 0; i < _pickRects.Count && i < _pickKeys.Count; i++)
                        if (_pickRects[i].Contains(m)) { AddMat(_pickKeys[i]); _matPicker = false; return; }
                    return;
                }
                if (_resetRect.Contains(m)) { ResetLoadout(); return; }
                for (int i = 0; i < _tabRects.Count; i++)
                    if (_tabRects[i].Contains(m)) { _heroIdx = i; _picker = -1; return; }
                for (int i = 0; i < _swapRects.Count; i++)
                    if (_swapRects[i].Contains(m)) { _picker = i; _pickerPage = 0; _pickGrade = ""; return; }
                if (_addMatRect.Contains(m)) { _matPicker = true; _pickerPage = 0; return; }
                for (int i = 0; i < _matRmRects.Count; i++)
                    if (_matRmRects[i].Contains(m)) { RemoveMat(i); return; }
                if (_rect.Contains(m) && InputCompat.ClaimDrag(Slot)) { _dragging = true; _dragOffset = m - new Vector2(_rect.x, _rect.y); }
            }
            if (_dragging)
            {
                if (!InputCompat.OwnsDrag(Slot)) { _dragging = false; return; }
                if (InputCompat.MouseHeld()) { _rect.x = m.x - _dragOffset.x; _rect.y = m.y - _dragOffset.y; UiScale.ClampToScreen(ref _rect, _scale); }
                if (InputCompat.MouseReleased()) { _dragging = false; _wantX = _rect.x; _wantY = _rect.y; Plugin.FitPosX.Value = _rect.x; Plugin.FitPosY.Value = _rect.y; }
            }
        }

        private void SetSlot(int slot, int itemKey)
        {
            int h = CurHero; if (h == 0) return;
            if (!_load.TryGetValue(h, out var arr)) { arr = new int[SlotParts.Length]; _load[h] = arr; }
            if (slot >= 0 && slot < arr.Length) arr[slot] = itemKey;
        }
        private void ResetLoadout()
        {
            int h = CurHero;
            if (_orig.TryGetValue(h, out var o)) { var c = new int[o.Length]; Array.Copy(o, c, o.Length); _load[h] = c; }
            _heroMats.Remove(h);
        }
        private void AddMat(int matKey)
        {
            int h = CurHero; if (h == 0) return;
            if (!_heroMats.TryGetValue(h, out var l)) { l = new List<int[]>(); _heroMats[h] = l; }
            int maxT = 1; foreach (var t in GearDatabase.Material(matKey)) if (t.Tier > maxT) maxT = t.Tier;
            l.Add(new int[] { matKey, maxT });
        }
        private void RemoveMat(int idx)
        {
            int h = CurHero;
            if (_heroMats.TryGetValue(h, out var l) && idx >= 0 && idx < l.Count) l.RemoveAt(idx);
        }
        private static string MatEffect(int[] mt)
        {
            if (mt == null || mt.Length < 2) return "";
            foreach (var t in GearDatabase.Material(mt[0]))
                if (t.Tier == mt[1]) return $"{t.Stat} {(t.Mod == "FLAT" ? "+" : (t.Mod == "ADDITIVE" ? "%+" : "×"))}{t.Mid:0} <size=9>T{mt[1]}</size>";
            return "#" + mt[0];
        }

        // ---------------- rendering ----------------
        private void EnsureAssets()
        {
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_bgTex == null) { _bgTex = new Texture2D(1, 1); _bgTex.SetPixel(0, 0, new Color(0, 0, 0, 1f)); _bgTex.Apply(); if (_box != null) _box.normal.background = _bgTex; }
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
        private static Color ClassColor(int heroKey)
        {
            switch (heroKey / 100) { case 1: return new Color(0.90f, 0.78f, 0.35f); case 2: return new Color(0.40f, 0.86f, 0.46f); case 3: return new Color(0.55f, 0.60f, 0.97f); case 4: return new Color(0.95f, 0.62f, 0.40f); }
            return new Color(0.6f, 0.64f, 0.7f);
        }
        private static string Nm(int itemKey)
        {
            if (itemKey == 0) return "—";
            string n = ItemNameStore.Get(itemKey);
            if (!string.IsNullOrEmpty(n)) return n;
            var g = GearDatabase.ByKey(itemKey);
            return g != null && !string.IsNullOrEmpty(g.NameKey) ? g.NameKey : ("#" + itemKey);
        }
        private static string FmtNum(double v) { double a = Math.Abs(v); if (a >= 1e6) return (v / 1e6).ToString("0.#") + "M"; if (a >= 1e3) return (v / 1e3).ToString("0.#") + "K"; return v.ToString("0.#"); }
        private static double Sv(Dictionary<string, double> agg, string k) { double v = 0; if (agg != null) agg.TryGetValue(k, out v); return v; }
        // gear-rarity hex (no leading '#') — same palette as the gear-score panel, one source of truth.
        private static string GradeHex(string grade) => GearScoreOverlayBehaviour.GradeColor(grade);

        void OnGUI()
        {
            if (!_visible || GameUiState.MenuOpen()) return;
            GUI.depth = -12; var prevM = GUI.matrix;
            try
            {
                EnsureAssets(); if (!_placed) PlaceDefault(); if (!_loaded) Reload();
                int fs = Plugin.FontSize.Value; float lh = fs + 6;
                float x = _rect.x, ix = x + Pad, w = _rect.width, iw = w - Pad * 2;

                int matN = (_heroMats.TryGetValue(CurHero, out var m0) && m0 != null) ? m0.Count : 0;
                int rows = (_picker >= 0 || _matPicker) ? 18 : (3 + 1 + SlotParts.Length + 2 + matN);
                float bodyH = lh * (rows + 2);
                _rect.height = Pad + bodyH + Pad;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                if (!_dragging) { _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale)); _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale)); }
                x = _rect.x; ix = x + Pad;
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);
                float cy = _rect.y + Pad;

                // title + reset + close
                GUI.Label(new Rect(ix, cy, iw - 90, lh), $"{Loc.G("fit_title")} <size=10><color=#8a93a0>{GearDatabase.Count} items</color></size>", _title);
                _resetRect = new Rect(x + w - 28 - 56, cy - 1, 56, lh); GUI.Button(_resetRect, Loc.G("sim_reset"), _btn);
                _closeRect = new Rect(x + w - 26, cy - 2, 22, lh); GUI.Button(_closeRect, "✕", _btn);
                cy += lh;

                if (_heroes.Count == 0)
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#8a93a0>{Loc.G("fit_need")}</color>", _label);
                    _resize.DrawGrip(_white, _rect); return;
                }

                int hero = CurHero;

                if (_picker >= 0) { DrawPicker(ix, cy, iw, lh, hero); _resize.DrawGrip(_white, _rect); return; }
                if (_matPicker) { DrawMatPicker(ix, cy, iw, lh, hero); _resize.DrawGrip(_white, _rect); return; }

                // hero tabs
                _tabRects.Clear(); float tx = ix;
                for (int i = 0; i < _heroes.Count; i++)
                {
                    string nm = HeroProbe.HeroName(_heroes[i]); float tw = _btn.CalcSize(new GUIContent(nm)).x + 16;
                    var tr = new Rect(tx, cy, tw, lh - 2); bool sel = i == _heroIdx;
                    DrawRect(tr.x, tr.y, tr.width, tr.height, sel ? new Color(0.30f, 0.45f, 0.75f, 0.4f) : new Color(1, 1, 1, 0.05f));
                    DrawRect(tr.x, tr.y + tr.height - 2, tr.width, 2, ClassColor(_heroes[i]));
                    GUI.Label(new Rect(tx + 8, cy, tw, lh), sel ? $"<b>{nm}</b>" : $"<color=#9aa3b0>{nm}</color>", _label);
                    _tabRects.Add(tr); tx += tw + 4;
                }
                cy += lh + 2;

                // computed stats (sandbox) + anchored DPS
                var sbKeys = KeysOf(_load, hero);
                List<int[]> sbMats; _heroMats.TryGetValue(hero, out sbMats);
                var agg = FitCalc.LoadoutStats(sbKeys, sbMats);
                double sbDps = FitCalc.LoadoutDps(sbKeys, sbMats);
                double origDps = FitCalc.LoadoutDps(KeysOf(_orig, hero));
                double meas; _measDps.TryGetValue(hero, out meas);
                double ratio = origDps > 0 ? sbDps / origDps : 1.0;
                double shownDps = meas > 0 ? meas * ratio : sbDps;

                GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>攻擊</color> <color=#eaf3ee>{Sv(agg, "AttackDamage"):0}</color>   " +
                    $"<color=#9fb4cc>攻速</color> <color=#eaf3ee>{Sv(agg, "AttackSpeed"):0.##}</color>   " +
                    $"<color=#9fb4cc>暴擊</color> <color=#eaf3ee>{Sv(agg, "CriticalChance"):0.#}</color>   " +
                    $"<color=#9fb4cc>暴傷</color> <color=#eaf3ee>{Sv(agg, "CriticalDamage"):0.#}</color>", _dim); cy += lh;
                GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>範圍</color> <color=#eaf3ee>{Sv(agg, "AoE"):0}</color>   " +
                    $"<color=#9fb4cc>冷卻</color> <color=#eaf3ee>{Sv(agg, "CooldownReduction"):0}</color>   " +
                    $"<color=#9fb4cc>多重</color> <color=#eaf3ee>{Sv(agg, "Multistrike"):0}</color>   " +
                    $"<color=#9fb4cc>投射</color> <color=#eaf3ee>{Sv(agg, "ProjCount"):0}</color>", _dim); cy += lh;
                string rc = ratio > 1.001 ? "#7fffa0" : (ratio < 0.999 ? "#ff8a8a" : "#cdd5df");
                GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>預測 DPS</color> <color=#eaf3ee><b>{FmtNum(shownDps)}</b></color>  " +
                    $"<color={rc}>(×{ratio:0.000} vs 現況)</color>   <size=10><color=#8a93a0>公式佔位版,相對準</color></size>", _label); cy += lh;
                DrawRect(ix, cy, iw, 1, new Color(1, 1, 1, 0.12f)); cy += 3;

                // gear slots
                _swapRects.Clear();
                _load.TryGetValue(hero, out var arr);
                for (int s = 0; s < SlotParts.Length; s++)
                {
                    int key = (arr != null && s < arr.Length) ? arr[s] : 0;
                    bool changed = _orig.TryGetValue(hero, out var oa) && oa != null && s < oa.Length && oa[s] != key;
                    if ((s & 1) == 1) DrawRect(ix, cy, iw, lh, new Color(1, 1, 1, 0.03f));
                    GUI.Label(new Rect(ix, cy, 44, lh), $"<color=#9aa3b0>{SlotLabel[s]}</color>", _label);
                    var stex = GearIconCache.Get(key);
                    if (stex != null) GUI.DrawTexture(new Rect(ix + 46, cy + 1, lh - 2, lh - 2), stex, ScaleMode.ScaleToFit);
                    var gt = GearDatabase.ByKey(key);
                    string ghex = changed ? "7fffa0" : GradeHex(gt != null ? gt.Grade : "");
                    GUI.Label(new Rect(ix + 48 + lh, cy, iw - 48 - lh - 56, lh), $"<color=#{ghex}>{Nm(key)}</color>", _label);
                    var sr = new Rect(x + w - Pad - 52, cy + 1, 50, lh - 3); GUI.Button(sr, Loc.G("fit_swap"), _btn); _swapRects.Add(sr);
                    cy += lh;
                }

                // runes / materials (sandbox additions; their stats fold into the aggregation above)
                DrawRect(ix, cy, iw, 1, new Color(1, 1, 1, 0.12f)); cy += 3;
                GUI.Label(new Rect(ix, cy, iw - 70, lh), $"<color=#9fb4cc>{Loc.G("fit_runes")}</color>", _dim);
                _addMatRect = new Rect(x + w - Pad - 64, cy, 62, lh - 2); GUI.Button(_addMatRect, Loc.G("fit_addmat"), _btn);
                cy += lh;
                _matRmRects.Clear();
                if (sbMats != null)
                    for (int i = 0; i < sbMats.Count; i++)
                    {
                        GUI.Label(new Rect(ix + 12, cy, iw - 12 - 28, lh), $"<size=11><color=#bcd0ea>◆ {MatEffect(sbMats[i])}</color></size>", _label);
                        var rm = new Rect(x + w - Pad - 26, cy + 1, 24, lh - 3); GUI.Button(rm, "×", _btn); _matRmRects.Add(rm);
                        cy += lh;
                    }
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        private void DrawPicker(float ix, float cy, float iw, float lh, int hero)
        {
            float x = _rect.x, w = _rect.width;
            _backRect = new Rect(ix, cy, 60, lh - 2); GUI.Button(_backRect, "◀ 返回", _btn);
            GUI.Label(new Rect(ix + 70, cy, iw - 70, lh), $"<color=#9fb4cc>{SlotLabel[_picker]} — 選擇裝備</color>", _label);
            cy += lh;
            var list = GearDatabase.BySlot(SlotParts[_picker]);
            // weapon slots hold many gear TYPES (sword/bow/staff…); a class can only use its own type,
            // so restrict the list to the type the hero currently has equipped (e.g. ranger -> BOW only).
            if (_picker == 0 || _picker == 1)
            {
                int eq = (_orig.TryGetValue(hero, out var oa) && _picker < oa.Length) ? oa[_picker] : 0;
                var eg = GearDatabase.ByKey(eq);
                if (eg != null && !string.IsNullOrEmpty(eg.Type))
                {
                    var f = new List<GearTemplate>();
                    foreach (var g in list) if (g.Type == eg.Type) f.Add(g);
                    list = f;
                }
            }
            // --- grade-filter chips (so the user isn't paging through 200+ items) ---
            var present = new HashSet<string>();
            foreach (var g in list) present.Add(g.Grade);
            _gradeRects.Clear(); _gradeKeys.Clear();
            float chx = ix, chy = cy, chh = lh - 1, cw = 44, gap = 3;
            DrawRect(chx, chy, cw, chh, _pickGrade == "" ? new Color(0.30f, 0.45f, 0.75f, 0.50f) : new Color(1, 1, 1, 0.06f));
            GUI.Label(new Rect(chx + 6, chy, cw, chh), "<size=11>全部</size>", _label);
            _gradeRects.Add(new Rect(chx, chy, cw, chh)); _gradeKeys.Add(""); chx += cw + gap;
            for (int gi = 0; gi < GradeLadder.Length; gi++)
            {
                if (!present.Contains(GradeLadder[gi])) continue;
                if (chx + cw > ix + iw) { chx = ix; chy += chh + 2; }
                bool act = _pickGrade == GradeLadder[gi];
                DrawRect(chx, chy, cw, chh, act ? new Color(0.30f, 0.45f, 0.75f, 0.50f) : new Color(1, 1, 1, 0.06f));
                GUI.Label(new Rect(chx + 6, chy, cw, chh), $"<size=11><color=#{GradeHex(GradeLadder[gi])}>{GradeLabel[gi]}</color></size>", _label);
                _gradeRects.Add(new Rect(chx, chy, cw, chh)); _gradeKeys.Add(GradeLadder[gi]); chx += cw + gap;
            }
            cy = chy + chh + 3;
            if (_pickGrade != "")
            {
                var fg = new List<GearTemplate>();
                foreach (var g in list) if (g.Grade == _pickGrade) fg.Add(g);
                list = fg;
            }
            int per = 7; float rowH = lh * 1.7f;
            int pages = Mathf.Max(1, (list.Count + per - 1) / per);
            _pickerPage = Mathf.Clamp(_pickerPage, 0, pages - 1);
            int start = _pickerPage * per; int shown = Mathf.Min(per, list.Count - start);
            _pickRects.Clear(); _pickKeys.Clear();
            int curKey = (_load.TryGetValue(hero, out var arr) && _picker < arr.Length) ? arr[_picker] : 0;
            // the variant the hero actually owns in this slot (each item ships as 2 inherent-roll variants);
            // mark it so the user can tell theirs apart from its twin.
            int ownedKey = (_orig.TryGetValue(hero, out var oa2) && _picker < oa2.Length) ? oa2[_picker] : 0;
            for (int i = 0; i < shown; i++)
            {
                var g = list[start + i]; var r = new Rect(ix, cy, iw, rowH - 1);
                bool cur = g.Key == curKey;
                if (cur) DrawRect(ix, cy, iw, rowH, new Color(0.30f, 0.45f, 0.75f, 0.30f)); else if ((i & 1) == 1) DrawRect(ix, cy, iw, rowH, new Color(1, 1, 1, 0.03f));
                // icon (fetched lazily from the wiki by ItemKey; faint placeholder until it resolves)
                var iconR = new Rect(ix + 3, cy + 3, rowH - 6, rowH - 6);
                var tex = GearIconCache.Get(g.Key);
                if (tex != null) GUI.DrawTexture(iconR, tex, ScaleMode.ScaleToFit);
                else { var pc = GUI.color; GUI.color = new Color(1, 1, 1, 0.10f); GUI.DrawTexture(iconR, _white); GUI.color = pc; }
                float tx = ix + rowH + 2;
                // name in its grade colour at full size (✓ = the variant you own), stat summary beneath
                string own = g.Key == ownedKey ? "<color=#7fffa0>✓</color> " : "";
                GUI.Label(new Rect(tx, cy + 1, iw - rowH - 6, lh), $"{own}<color=#{GradeHex(g.Grade)}><b>{Nm(g.Key)}</b></color>", _label);
                string stats = "";
                foreach (var st in g.Stats) stats += $"{st.Stat.Substring(0, Math.Min(4, st.Stat.Length))}{(st.Mod == "FLAT" ? "+" : (st.Mod == "ADDITIVE" ? "%+" : "×"))}{st.Value:0}　";
                GUI.Label(new Rect(tx, cy + lh - 1, iw - rowH - 6, lh), $"<size=10><color=#8a93a0>{stats}</color></size>", _tiny);
                _pickRects.Add(r); _pickKeys.Add(g.Key); cy += rowH;
            }
            _ppPrev = new Rect(ix, cy, 26, lh - 2); _ppNext = new Rect(ix + 30, cy, 26, lh - 2);
            GUI.Button(_ppPrev, "◀", _btn); GUI.Button(_ppNext, "▶", _btn);
            GUI.Label(new Rect(ix + 64, cy, iw - 64, lh), $"<size=11><color=#9fb4cc>{_pickerPage + 1}/{pages}　{list.Count} 件</color></size>", _dim);
        }

        private void DrawMatPicker(float ix, float cy, float iw, float lh, int hero)
        {
            float x = _rect.x, w = _rect.width;
            _backRect = new Rect(ix, cy, 60, lh - 2); GUI.Button(_backRect, "◀ 返回", _btn);
            GUI.Label(new Rect(ix + 70, cy, iw - 70, lh), $"<color=#9fb4cc>{Loc.G("fit_runes")} — 選材料</color>", _label);
            cy += lh;
            var keys = GearDatabase.MaterialKeys;
            int per = 12; int pages = Mathf.Max(1, (keys.Count + per - 1) / per);
            _pickerPage = Mathf.Clamp(_pickerPage, 0, pages - 1);
            int start = _pickerPage * per; int shown = Mathf.Min(per, keys.Count - start);
            _pickRects.Clear(); _pickKeys.Clear();
            for (int i = 0; i < shown; i++)
            {
                int mk = keys[start + i];
                MatTier top = default; foreach (var t in GearDatabase.Material(mk)) if (t.Tier >= top.Tier) top = t;
                var r = new Rect(ix, cy, iw, lh - 1); if ((i & 1) == 1) DrawRect(ix, cy, iw, lh, new Color(1, 1, 1, 0.03f));
                string sym = top.Mod == "FLAT" ? "+" : (top.Mod == "ADDITIVE" ? "%+" : "×");
                GUI.Label(new Rect(ix + 4, cy, iw - 8, lh), $"<size=11><color=#eaf3ee>{top.Stat}</color> <color=#8a93a0>{sym}{top.Min:0}~{top.Max:0} (max T{top.Tier})</color></size>", _label);
                _pickRects.Add(r); _pickKeys.Add(mk); cy += lh;
            }
            _ppPrev = new Rect(ix, cy, 26, lh - 2); _ppNext = new Rect(ix + 30, cy, 26, lh - 2);
            GUI.Button(_ppPrev, "◀", _btn); GUI.Button(_ppNext, "▶", _btn);
            GUI.Label(new Rect(ix + 64, cy, iw - 64, lh), $"<size=11><color=#9fb4cc>{_pickerPage + 1}/{pages}　{keys.Count} 材料</color></size>", _dim);
        }
    }
}
