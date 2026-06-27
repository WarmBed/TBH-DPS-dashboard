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
                MatCatalog.Load(ReadRes(asm, "fit_mats.json"));
                SocketDb.Load(ReadRes(asm, "fit_sockets.json"));
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
        private const float PickerW = 340f;   // width of the item/material side-column (expand-right)
        // gear slot index -> (PARTS key in the DB, short label)
        private static readonly string[] SlotParts = { "MAIN_WEAPON", "SUB_WEAPON", "HELMET", "ARMOR", "GLOVES", "BOOTS", "AMULET", "EARING", "RING", "BRACER" };
        private static readonly string[] SlotKey = { "slot_main", "slot_off", "slot_helm", "slot_body", "slot_glove", "slot_boot", "slot_amulet", "slot_ear", "slot_ring", "slot_bracer" };
        private static string SlotL(int s) => Loc.G(SlotKey[s]);

        private Rect _rect = new Rect(90, 90, 560, 0);
        private bool _visible, _placed;
        private float _wantX, _wantY;
        private Vector2 _dragOffset; private bool _dragging;

        private Texture2D _white, _bgTex;
        private GUIStyle _title, _label, _dim, _tiny, _btn, _box, _col, _wrap;
        private bool _stylesReady; private int _builtFs = -1, _builtFsm = -1;
        // this panel runs +2 over the global font sizes for readability (dense list + side column)
        private int Fs => Plugin.FontSize.Value + 2;
        private int Fsm => Plugin.FontSizeSmall.Value + 2;

        private int _seenVersion = -1; private bool _loaded;
        private readonly List<int> _heroes = new List<int>();
        private int _heroIdx;
        private readonly Dictionary<int, int[]> _orig = new Dictionary<int, int[]>();  // real equipped (anchor)
        private readonly Dictionary<int, int[]> _load = new Dictionary<int, int[]>();   // sandbox (editable)
        private readonly Dictionary<int, double> _measDps = new Dictionary<int, double>();
        // hero -> live computed character stats (short key -> value) from the newest run; the bench anchors
        // its displayed stats to these so the current config matches the game's 屬性 panel exactly.
        private readonly Dictionary<int, Dictionary<string, double>> _liveStats = new Dictionary<int, Dictionary<string, double>>();
        // hero -> slot -> material key per socket (deco sockets first, then engraving, then inscription)
        private readonly Dictionary<int, Dictionary<int, int[]>> _sockets = new Dictionary<int, Dictionary<int, int[]>>();
        private int _focus = 0;          // gear slot whose sockets are shown in the bench
        private int _sockSlot = -1, _sockPos = -1;  // socket being edited (side-column open when _sockSlot>=0)
        private char _sockType = 'D';    // socket type being edited (D/E/I)
        private readonly List<Rect> _sockRects = new List<Rect>();    // clickable socket cells (focused item)
        private readonly List<int> _sockPosList = new List<int>();    // parallel: socket position per cell
        private readonly List<Rect> _focusRects = new List<Rect>();   // clickable gear rows (set focus)
        private bool _fitList;          // load-fitting side panel open (temporarily overrides the always-on picker)
        private Rect _saveRect, _loadRect;
        private readonly List<Rect> _fitLoadRects = new List<Rect>();   // per saved-fit "load" hitboxes
        private readonly List<Rect> _fitDelRects = new List<Rect>();    // per saved-fit "delete" hitboxes
        private readonly List<int> _fitIdx = new List<int>();           // parallel: store index per shown row
        private readonly List<RunRecord> _clearStages = new List<RunRecord>();   // latest run per farmed stage (built on Reload), for the live clear-time block
        private float _savedFlash;      // frame counter for the "已儲存" toast
        private int _pickerPage;
        private string _pickGrade = "";  // active grade-filter chip in the picker ("" = all)
        private string _pickStat = "";   // active stat-filter chip in the picker ("" = all); item must carry this StatType
        private readonly List<Rect> _gradeRects = new List<Rect>();   // grade-chip hitboxes
        private readonly List<string> _gradeKeys = new List<string>();
        private readonly List<Rect> _statRects = new List<Rect>();    // stat-filter chip hitboxes
        private readonly List<string> _statKeys = new List<string>(); // parallel: stat enum name per chip
        // TBH's 10-tier rarity ladder (ascending) + short CJK labels, for the picker's grade chips.
        private static readonly string[] GradeLadder = { "COMMON", "UNCOMMON", "RARE", "LEGENDARY", "IMMORTAL", "ARCANA", "BEYOND", "CELESTIAL", "DIVINE", "COSMIC" };
        private static string GradeL(string g) => Loc.G("grade_" + g.ToLowerInvariant());
        // SaveGearReader emits affix stats as short Loc keys; map them to StatType enum names for aggregation/display
        private static readonly Dictionary<string, string> Short2Enum = new Dictionary<string, string>
        {
            { "attack", "AttackDamage" }, { "aspd", "AttackSpeed" }, { "critrate", "CriticalChance" }, { "critdmg", "CriticalDamage" },
            { "hp", "MaxHp" }, { "armor", "Armor" }, { "mspd", "MovementSpeed" }, { "AoE", "AreaOfEffect" }, { "cdr", "CooldownReduction" },
            { "Multistrike", "Multistrike" }, { "ProjCount", "ProjectileCount" }, { "HpLeech", "HpLeech" }, { "HpRegen", "HpRegenPerSec" },
            { "CastSpd", "CastSpeed" }, { "Phys%", "PhysicalDamagePercent" }, { "Fire%", "FireDamagePercent" }, { "Cold%", "ColdDamagePercent" },
            { "Light%", "LightningDamagePercent" }, { "Chaos%", "ChaosDamagePercent" }, { "FireRes", "FireResistance" }, { "ColdRes", "ColdResistance" },
            { "LightRes", "LightningResistance" }, { "ChaosRes", "ChaosResistance" }, { "Dodge", "DodgeChance" }, { "Block", "BlockChance" },
            { "ProjDmg", "IncreaseProjectileDamage" }, { "MeleeDmg", "IncreaseMeleeDamage" }, { "AoEDmg", "IncreaseAreaOfEffectDamage" }, { "SummonDmg", "IncreaseSummonDamage" },
        };
        private static string Short2EnumName(string s) => (s != null && Short2Enum.TryGetValue(s, out var e)) ? e : (s ?? "");

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
            Dictionary<int, List<GearItem>> party = null;
            try { party = SaveGearReader.ReadParty(); } catch { }
            // the save read can come back empty for a frame right after a clear — keep the last loadout
            // (and any in-progress edits) instead of blanking the bench.
            if ((party == null || party.Count == 0) && _heroes.Count > 0) return;
            _heroes.Clear(); _orig.Clear(); _load.Clear(); _measDps.Clear(); _sockets.Clear(); RealSockets.Clear(); _liveStats.Clear();
            // current equipped gear per hero (ItemKey by slot0..slot9) + the item's REAL applied sockets
            try
            {
                if (party != null)
                foreach (var kv in party)
                {
                    var arr = new int[SlotParts.Length];
                    foreach (var g in kv.Value)
                    {
                        if (g == null || string.IsNullOrEmpty(g.Slot) || !g.Slot.StartsWith("slot")) continue;
                        int si; if (!int.TryParse(g.Slot.Substring(4), out si)) continue;
                        if (si < 0 || si >= arr.Length) continue;
                        arr[si] = g.ItemKey;
                        // real applied socket effects (EnchantData), placed into the grade's socket cells in
                        // deco → engrave → inscribe order (the save lists them grouped, with applied counts)
                        var gt = GearDatabase.ByKey(g.ItemKey);
                        var cnt = SocketDb.Counts(gt != null ? gt.Grade : "");
                        int total = cnt[0] + cnt[1] + cnt[2];
                        if (total > 0 && g.Affixes.Count > 0)
                        {
                            var cells = new GearStat[total];
                            int dc = Math.Min(g.DecoCount, cnt[0]), ec = Math.Min(g.EngraveCount, cnt[1]), ic = Math.Min(g.InscribeCount, cnt[2]);
                            int ai = 0;
                            // EnchantData carries the exact stat, value AND mod — use them directly
                            for (int j = 0; j < cnt[0]; j++) if (j < dc && ai < g.Affixes.Count) { var af = g.Affixes[ai]; cells[j] = new GearStat(Short2EnumName(af.Name), af.Mod, af.Value); ai++; }
                            for (int j = 0; j < cnt[1]; j++) if (j < ec && ai < g.Affixes.Count) { var af = g.Affixes[ai]; cells[cnt[0] + j] = new GearStat(Short2EnumName(af.Name), af.Mod, af.Value); ai++; }
                            for (int j = 0; j < cnt[2]; j++) if (j < ic && ai < g.Affixes.Count) { var af = g.Affixes[ai]; cells[cnt[0] + cnt[1] + j] = new GearStat(Short2EnumName(af.Name), af.Mod, af.Value); ai++; }
                            RealSockets.Set(kv.Key, si, cells);
                        }
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
                for (int i = runs.Count - 1; i >= 0 && _liveStats.Count == 0; i--)
                {
                    var r = runs[i]; if (r == null || r.Party == null) continue;
                    double active = r.ActiveSeconds > 0 ? r.ActiveSeconds : r.Duration;
                    foreach (var snap in r.Party)
                    {
                        if (snap == null) continue;
                        int key; if (!int.TryParse(snap.Character, out key)) continue;
                        if (snap.DamageDealt > 0 && active > 0) _measDps[key] = snap.DamageDealt / active;
                        if (snap.Stats != null && snap.Stats.Count > 0)
                        {
                            var sm = new Dictionary<string, double>();
                            foreach (var se in snap.Stats) sm[se.Key] = se.Value;
                            _liveStats[key] = sm;
                        }
                    }
                }
                // latest run per farmed stage (with an active/idle split), for the live clear-time block
                _clearStages.Clear();
                var byStage = new Dictionary<string, RunRecord>();
                foreach (var r in runs) { if (r == null || string.IsNullOrEmpty(r.StageId) || r.ActiveSeconds <= 0) continue; byStage[r.StageId] = r; }
                var sids = new List<string>(byStage.Keys); sids.Sort(System.StringComparer.Ordinal);
                foreach (var sid in sids) _clearStages.Add(byStage[sid]);
            }
            catch { }
            if (_heroIdx >= _heroes.Count) _heroIdx = 0;
            _loaded = true;
        }

        private int CurHero => (_heroIdx >= 0 && _heroIdx < _heroes.Count) ? _heroes[_heroIdx] : 0;

        private Dictionary<int, int[]> SockOf(int hero)
        {
            if (!_sockets.TryGetValue(hero, out var d)) { d = new Dictionary<int, int[]>(); _sockets[hero] = d; }
            return d;
        }
        // [deco, engrave, inscribe] socket counts for the item currently in a slot (driven by its grade)
        private int[] SlotSockets(int hero, int slot)
        {
            var g = (_load.TryGetValue(hero, out var a) && slot >= 0 && slot < a.Length) ? GearDatabase.ByKey(a[slot]) : null;
            return g != null ? SocketDb.Counts(g.Grade) : new int[3];
        }
        private int GetSocket(int hero, int slot, int pos)
        {
            if (_sockets.TryGetValue(hero, out var d) && d.TryGetValue(slot, out var a) && a != null && pos >= 0 && pos < a.Length) return a[pos];
            return 0;
        }
        private void SetSocket(int slot, int pos, int matKey)
        {
            int h = CurHero; if (h == 0) return;
            var cc = SlotSockets(h, slot); int total = cc[0] + cc[1] + cc[2];
            if (total <= 0) return;
            var d = SockOf(h);
            if (!d.TryGetValue(slot, out var a) || a == null || a.Length != total)
            { a = new int[total]; for (int i = 0; i < total; i++) a[i] = -1; d[slot] = a; }   // -1 = unedited (use real)
            if (pos >= 0 && pos < a.Length) a[pos] = matKey;
        }
        // gear group (WEAPON/ARMOR/ACCESSORY) of the item in a slot — selects which socket effect applies
        private string SlotGroup(int hero, int slot)
        {
            var g = (_load.TryGetValue(hero, out var a) && slot >= 0 && slot < a.Length) ? GearDatabase.ByKey(a[slot]) : null;
            return g != null ? g.GearGroup : "";
        }
        // focus a gear slot: shows its sockets in the bench AND its items in the always-on picker.
        // Changing slot clears the picker's grade/stat filters (a stale filter could hide the new list).
        private void FocusSlot(int slot)
        {
            if (_focus != slot) { _pickGrade = ""; _pickStat = ""; _pickerPage = 0; }
            _focus = slot; _sockSlot = -1; _fitList = false;
        }
        // open the side-column material picker for a socket; its type (D/E/I) follows the position
        private void OpenSockPicker(int slot, int pos)
        {
            var cc = SlotSockets(CurHero, slot);
            _sockType = pos < cc[0] ? 'D' : (pos < cc[0] + cc[1] ? 'E' : 'I');
            _sockSlot = slot; _sockPos = pos; _fitList = false; _pickerPage = 0; _pickGrade = "";
        }

        private void HandlePointer()
        {
            if (GameUiState.MenuOpen()) { if (_dragging) { _dragging = false; InputCompat.ReleaseDrag(Slot); } return; }
            Vector2 m = UiScale.ToLocal(InputCompat.MouseGuiPos(), _rect.x, _rect.y, _scale);
            {
                // resize the BASE panel width (the always-on picker keeps its fixed width on the right)
                float rw = _rect.width - PickerW, dh = 0f;
                var rr = _resize.Handle(Slot, m, ref rw, ref dh, 460f, Mathf.Max(460f, Screen.width * 0.95f), 0f, 0f, false);
                if (rr != PanelResize.Result.None) { Plugin.FitPanelWidth.Value = (rr == PanelResize.Result.Reset) ? 560f : rw; return; }
            }
            if (InputCompat.MousePressed())
            {
                if (_closeRect.Contains(m)) { _visible = false; return; }
                if (_saveRect.Contains(m)) { SaveCurrentFit(); return; }
                if (_loadRect.Contains(m)) { _fitList = !_fitList; _sockSlot = -1; return; }
                // side-column hits. The picker is the always-on default; the fit-list and socket-material
                // pickers temporarily override it (their own back button restores the picker).
                if (_fitList)
                {
                    if (_backRect.Contains(m)) { _fitList = false; return; }
                    for (int i = 0; i < _fitLoadRects.Count && i < _fitIdx.Count; i++)
                        if (_fitLoadRects[i].Contains(m)) { LoadFit(_fitIdx[i]); return; }
                    for (int i = 0; i < _fitDelRects.Count && i < _fitIdx.Count; i++)
                        if (_fitDelRects[i].Contains(m)) { FitStore.RemoveAt(_fitIdx[i]); return; }
                }
                else if (_sockSlot >= 0)
                {
                    if (_backRect.Contains(m)) { _sockSlot = -1; return; }
                    for (int i = 0; i < _gradeRects.Count && i < _gradeKeys.Count; i++)
                        if (_gradeRects[i].Contains(m)) { _pickGrade = _gradeKeys[i]; _pickerPage = 0; return; }
                    if (_ppPrev.Contains(m)) { _pickerPage = Mathf.Max(0, _pickerPage - 1); return; }
                    if (_ppNext.Contains(m)) { _pickerPage++; return; }
                    for (int i = 0; i < _pickRects.Count && i < _pickKeys.Count; i++)
                        if (_pickRects[i].Contains(m)) { SetSocket(_sockSlot, _sockPos, _pickKeys[i]); _sockSlot = -1; return; }
                }
                else   // always-on gear picker for the focused slot
                {
                    for (int i = 0; i < _gradeRects.Count && i < _gradeKeys.Count; i++)
                        if (_gradeRects[i].Contains(m)) { _pickGrade = _gradeKeys[i]; _pickerPage = 0; return; }
                    for (int i = 0; i < _statRects.Count && i < _statKeys.Count; i++)
                        if (_statRects[i].Contains(m)) { _pickStat = _pickStat == _statKeys[i] ? "" : _statKeys[i]; _pickerPage = 0; return; }
                    if (_ppPrev.Contains(m)) { _pickerPage = Mathf.Max(0, _pickerPage - 1); return; }
                    if (_ppNext.Contains(m)) { _pickerPage++; return; }
                    for (int i = 0; i < _pickRects.Count && i < _pickKeys.Count; i++)
                        if (_pickRects[i].Contains(m)) { SetSlot(_focus, _pickKeys[i]); return; }   // keep list open after a swap
                }
                if (_resetRect.Contains(m)) { ResetLoadout(); return; }
                for (int i = 0; i < _tabRects.Count; i++)
                    if (_tabRects[i].Contains(m)) { _heroIdx = i; return; }   // keep the side-column open across heroes
                for (int i = 0; i < _swapRects.Count; i++)
                    if (_swapRects[i].Contains(m)) { FocusSlot(i); return; }
                for (int i = 0; i < _focusRects.Count; i++)
                    if (_focusRects[i].Contains(m)) { FocusSlot(i); return; }
                for (int i = 0; i < _sockRects.Count && i < _sockPosList.Count; i++)
                    if (_sockRects[i].Contains(m)) { OpenSockPicker(_focus, _sockPosList[i]); return; }
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
            if (slot >= 0 && slot < arr.Length && arr[slot] != itemKey) { arr[slot] = itemKey; SockOf(h).Remove(slot); }
        }
        private void ResetLoadout()
        {
            int h = CurHero;
            if (_orig.TryGetValue(h, out var o)) { var c = new int[o.Length]; Array.Copy(o, c, o.Length); _load[h] = c; }
            _sockets.Remove(h);
        }
        // ---- stat-line formatting (localized name + signed value) ----
        private static string StatL(string stat) => Loc.G(stat);   // StatType -> localized; falls back to the raw name
        // percent-type stats are stored x10 (e.g. CriticalDamage 1089 = 108.9%); a short list is flat integers.
        private static bool IsFlat(string stat, string mod)
        {
            if (mod != "FLAT") return false;
            switch (stat)
            {
                case "AttackDamage": case "MaxHp": case "Armor": case "AddHpPerHit": case "AddHpPerKill":
                case "ProjectileCount": case "Multistrike": case "DamageAbsorption": case "HpRegenPerSec":
                case "DamageAddition": case "PhysicalDamageAddition": case "FireDamageAddition":
                case "ColdDamageAddition": case "LightningDamageAddition": case "ChaosDamageAddition":
                case "AddAllSkillLevel": case "BaseAttackCountReduction": return true;
                default: return false;
            }
        }
        private static string StatVal(string stat, string mod, double v)
        {
            string sign = v >= 0 ? "+" : "";
            return IsFlat(stat, mod) ? $"{sign}{v:0}" : $"{sign}{v / 10.0:0.#}%";
        }
        // a material's roll RANGE (for the picker — candidates aren't rolled yet)
        private static string StatValRange(string stat, string mod, double min, double max)
        {
            if (min == max) return StatVal(stat, mod, min);
            return IsFlat(stat, mod) ? $"+{min:0}~{max:0}" : $"+{min / 10.0:0.#}~{max / 10.0:0.#}%";
        }
        // a socketed material's effect on a gear group, formatted ("空" when the socket is empty)
        private static string SockEffect(int matKey, string gearGroup)
        {
            if (matKey == 0) return $"<color=#67707d>{Loc.G("sock_empty")}</color>";
            var mm = MatCatalog.Get(matKey);
            if (mm == null || !mm.HasFor(gearGroup)) return Nm(matKey);
            var e = mm.Effect(gearGroup);
            return $"<color=#bcd0ea>{StatL(e.Stat)} {StatVal(e.Stat, e.Mod, e.Value)}</color> <size=9><color=#8a93a0>T{mm.TierFor(gearGroup)}</color></size>";
        }

        // ---------------- rendering ----------------
        private void EnsureAssets()
        {
            if (_white == null) { _white = new Texture2D(1, 1); _white.SetPixel(0, 0, Color.white); _white.Apply(); }
            if (_bgTex == null) { _bgTex = new Texture2D(1, 1); _bgTex.SetPixel(0, 0, new Color(0, 0, 0, 1f)); _bgTex.Apply(); if (_box != null) _box.normal.background = _bgTex; }
            int fs = Fs, fsm = Fsm;
            if (_stylesReady && _builtFs == fs && _builtFsm == fsm) return;
            _builtFs = fs; _builtFsm = fsm;
            _title = new GUIStyle { fontSize = fs, fontStyle = FontStyle.Bold, richText = true }; _title.normal.textColor = new Color(1f, 0.86f, 0.35f);
            _label = new GUIStyle { fontSize = fs, richText = true }; _label.normal.textColor = new Color(0.93f, 0.93f, 0.93f);
            _dim = new GUIStyle { fontSize = fsm, richText = true }; _dim.normal.textColor = new Color(0.78f, 0.84f, 0.95f);
            _tiny = new GUIStyle { fontSize = Mathf.Max(9, fsm - 2), richText = true }; _tiny.normal.textColor = new Color(0.7f, 0.75f, 0.85f);
            _wrap = new GUIStyle { fontSize = Mathf.Max(10, fsm - 1), richText = true, wordWrap = true }; _wrap.normal.textColor = new Color(0.72f, 0.77f, 0.86f);
            _col = new GUIStyle { fontSize = fs, richText = true, alignment = TextAnchor.MiddleRight }; _col.normal.textColor = Color.white;
            _btn = new GUIStyle(GUI.skin.button) { fontSize = fsm, fontStyle = FontStyle.Bold, richText = true };
            _box = new GUIStyle(); _box.normal.background = _bgTex;
            OverlayFonts.Apply(_title, _label, _dim, _tiny, _col, _btn, _wrap);
            _stylesReady = true;
        }
        private void DrawRect(float x, float y, float w, float h, Color c) { var p = GUI.color; GUI.color = c; GUI.DrawTexture(new Rect(x, y, w, h), _white); GUI.color = p; }
        // one stat-comparison row, TABLE-aligned: name | 原 | 新 | Δ% | bar (fixed columns so values line up).
        private float StatBarRow(float x, float y, float w, float lh, string label, double o, double n, string fmt, string suffix)
        {
            bool up = n > o + Math.Abs(o) * 5e-4 + 1e-9, down = n < o - Math.Abs(o) * 5e-4 - 1e-9;
            string nc = up ? "#7fffa0" : (down ? "#ff8a8a" : "#cdd5df");
            GUI.Label(new Rect(x, y, 66, lh), $"<color=#9fb4cc>{label}</color>", _dim);
            GUI.Label(new Rect(x + 66, y, 60, lh), $"<color=#8a93a0>{o.ToString(fmt)}{suffix}</color>", _dim);    // 原
            GUI.Label(new Rect(x + 128, y, 60, lh), $"<color={nc}>{n.ToString(fmt)}{suffix}</color>", _dim);      // 新
            string delta = (up || down) && o != 0 ? $"{((n - o) / Math.Abs(o) * 100 >= 0 ? "+" : "")}{(n - o) / Math.Abs(o) * 100:0.#}%" : "";
            GUI.Label(new Rect(x + 190, y, 46, lh), $"<size=10><color={nc}>{delta}</color></size>", _dim);        // Δ%
            float bx = x + 240, bw = w - 240, by = y + lh * 0.34f, bh = lh * 0.34f;
            if (bw > 24)
            {
                DrawRect(bx, by, bw, bh, new Color(1, 1, 1, 0.05f));   // track
                double maxS = Math.Max(Math.Max(Math.Abs(o), Math.Abs(n)), 1e-9);
                float oF = Mathf.Clamp01((float)(o / maxS)), nF = Mathf.Clamp01((float)(n / maxS));
                DrawRect(bx, by, bw * Math.Min(oF, nF), bh, new Color(0.45f, 0.50f, 0.58f, 0.6f));   // shared base
                if (up) DrawRect(bx + bw * oF, by, bw * (nF - oF), bh, new Color(0.40f, 0.85f, 0.50f, 0.92f));   // gain
                else if (down) DrawRect(bx + bw * nF, by, bw * (oF - nF), bh, new Color(0.90f, 0.42f, 0.42f, 0.92f)); // loss
                DrawRect(bx + bw * oF - 1f, y + lh * 0.24f, 1.5f, lh * 0.54f, new Color(1, 1, 1, 0.5f));   // original tick
            }
            return y + lh;
        }
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
        // anchor a live character stat to the gear-aggregate ratio: unchanged gear -> the live value (matches
        // the game's 屬性 panel); edits scale it proportionally (additive when gear contributes 0 to that stat).
        private static double Shown(double live, double origA, double sbA)
        {
            if (live == 0) return sbA;   // no live anchor (no recent run) -> fall back to the gear aggregate
            return origA > 0.0001 ? live * (sbA / origA) : live + (sbA - origA);
        }
        // the NEW display value: oDisp is the original in display units; scale gear edits in.
        // gear with a prior contribution -> ratio (units cancel); gear added from zero -> additive, with the
        // aggregate converted to display units (aggScale: 10 for ×10-stored percents, 1 for flat values).
        private static double DispN(double oDisp, double origA, double newA, double aggScale)
        {
            if (origA > 1e-6) return oDisp * (newA / origA);
            return oDisp + (newA - origA) / aggScale;
        }
        // gear-rarity hex (no leading '#') — same palette as the gear-score panel, one source of truth.
        private static string GradeHex(string grade) => GearScoreOverlayBehaviour.GradeColor(grade);

        void OnGUI()
        {
            if (!_visible || GameUiState.MenuOpen()) return;
            GUI.depth = -12; var prevM = GUI.matrix;
            try
            {
                EnsureAssets(); if (!_placed) PlaceDefault(); if (!_loaded) Reload();
                int fs = Fs; float lh = fs + 6;
                // main column + always-on item/material side-column to the RIGHT (expand, don't replace the page)
                const bool sideOpen = true;   // the gear picker is now persistent (sock/fit-list temporarily override it)
                float baseW = Mathf.Max(560f, Plugin.FitPanelWidth.Value);
                _rect.width = baseW + PickerW;
                float x = _rect.x, ix = x + Pad, w = _rect.width, iw = baseW - Pad * 2;

                // socket section height = focused item's header + one row per (non-empty type label + each socket)
                int[] sc0 = SlotSockets(CurHero, _focus);
                int typesShown = (sc0[0] > 0 ? 1 : 0) + (sc0[1] > 0 ? 1 : 0) + (sc0[2] > 0 ? 1 : 0);
                int sockRows = Mathf.Max(2, 1 + typesShown + sc0[0] + sc0[1] + sc0[2]);
                int clearRows = (_clearStages.Count > 0 ? _clearStages.Count + 2 : 2);   // title + per-stage + average (or "no data")
                int mainRows = 10 + clearRows + 1 + SlotParts.Length + 1 + sockRows;   // DPS + 8 stats + clear-time + gear + sockets
                int rows = sideOpen ? Mathf.Max(mainRows, 20) : mainRows;
                float bodyH = lh * (rows + 2);
                _rect.height = Pad + bodyH + Pad;
                _scale = UiScale.Fit(_rect.width, _rect.height);
                if (!_dragging) { _rect.x = Mathf.Clamp(_wantX, 0f, Mathf.Max(0f, Screen.width - _rect.width * _scale)); _rect.y = Mathf.Clamp(_wantY, 0f, Mathf.Max(0f, Screen.height - _rect.height * _scale)); }
                x = _rect.x; ix = x + Pad;
                GUI.matrix = UiScale.Matrix(_rect.x, _rect.y, _scale);
                GUI.Box(_rect, GUIContent.none, _box); PanelBorder.Draw(_rect);
                float cy = _rect.y + Pad;

                // title + save / load / reset / close
                GUI.Label(new Rect(ix, cy, iw - 200, lh), $"{Loc.G("fit_title")} <size=10><color=#8a93a0>{GearDatabase.Count} {Loc.G("fit_count")}</color></size>", _title);
                _saveRect = new Rect(x + baseW - 28 - 56 - 4 - 38 - 4 - 38, cy - 1, 38, lh); GUI.Button(_saveRect, "💾" + Loc.G("fit_save"), _btn);
                _loadRect = new Rect(x + baseW - 28 - 56 - 4 - 38, cy - 1, 38, lh); GUI.Button(_loadRect, "📂" + Loc.G("fit_load"), _btn);
                _resetRect = new Rect(x + baseW - 28 - 56, cy - 1, 56, lh); GUI.Button(_resetRect, Loc.G("sim_reset"), _btn);
                _closeRect = new Rect(x + baseW - 26, cy - 2, 22, lh); GUI.Button(_closeRect, "✕", _btn);
                if (_savedFlash > 0f) { _savedFlash -= 1f; GUI.Label(new Rect(ix + 200, cy, 120, lh), $"<color=#7fffa0>✓ {Loc.G("fit_saved")}</color>", _label); }
                cy += lh;

                if (_heroes.Count == 0)
                {
                    GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#8a93a0>{Loc.G("fit_need")}</color>", _label);
                    _resize.DrawGrip(_white, _rect); return;
                }

                int hero = CurHero;

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

                // computed stats (sandbox gear + effective sockets) + anchored DPS.
                // baseline (orig) = real items + their REAL applied sockets; sandbox = edited cells, else real.
                _load.TryGetValue(hero, out var gearArr);
                _orig.TryGetValue(hero, out var origArr);
                _sockets.TryGetValue(hero, out var hsock);
                var sbLines = new Dictionary<int, List<GearStat>>();
                var origLines = new Dictionary<int, List<GearStat>>();
                for (int s = 0; s < SlotParts.Length; s++)
                {
                    var realO = RealSockets.Get(hero, s);
                    if (realO != null)
                    {
                        var lo = new List<GearStat>();
                        foreach (var c in realO) if (!string.IsNullOrEmpty(c.Stat)) lo.Add(c);
                        if (lo.Count > 0) origLines[s] = lo;
                    }
                    bool unchanged = origArr != null && gearArr != null && s < origArr.Length && s < gearArr.Length && origArr[s] == gearArr[s];
                    int[] edited = (hsock != null && hsock.TryGetValue(s, out var ea)) ? ea : null;
                    string gg = SlotGroup(hero, s);
                    var scc = SlotSockets(hero, s); int n = scc[0] + scc[1] + scc[2];
                    if (n > 0)
                    {
                        var ls = new List<GearStat>();
                        for (int p = 0; p < n; p++) { var e = FitCalc.EffectiveCell(realO, edited, p, gg, unchanged); if (!string.IsNullOrEmpty(e.Stat)) ls.Add(e); }
                        if (ls.Count > 0) sbLines[s] = ls;
                    }
                }
                var agg = FitCalc.LoadoutStatsWith(gearArr, sbLines);
                var origAgg = FitCalc.LoadoutStatsWith(origArr, origLines);
                double sbDps = FitCalc.LoadoutDpsWith(gearArr, sbLines);
                double origDps = FitCalc.LoadoutDpsWith(origArr, origLines);
                _liveStats.TryGetValue(hero, out var live);   // real character stats (anchor)
                double meas; _measDps.TryGetValue(hero, out meas);
                double ratio = origDps > 0 ? sbDps / origDps : 1.0;
                double shownDps = meas > 0 ? meas * ratio : sbDps;

                // DPS (prominent) + a before→after comparison list with rightward difference bars.
                // O = the real current value (anchor); N = the value after the sandbox edits.
                string rc = ratio > 1.001 ? "#7fffa0" : (ratio < 0.999 ? "#ff8a8a" : "#cdd5df");
                GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>{Loc.G("fit_dps")}</color> <color=#eaf3ee><b>{FmtNum(shownDps)}</b></color>  " +
                    $"<color={rc}>(×{ratio:0.000} {Loc.G("fit_vs")})</color>   <size=10><color=#8a93a0>{Loc.G("fit_approx")}</color></size>", _label); cy += lh;
                // table header: 原 / 新 / 差異
                GUI.Label(new Rect(ix + 66, cy, 60, lh), $"<size=10><color=#6b7280>{Loc.G("fit_orig")}</color></size>", _dim);
                GUI.Label(new Rect(ix + 128, cy, 60, lh), $"<size=10><color=#6b7280>{Loc.G("fit_new")}</color></size>", _dim);
                GUI.Label(new Rect(ix + 190, cy, 46, lh), $"<size=10><color=#6b7280>{Loc.G("fit_diff")}</color></size>", _dim);
                cy += lh * 0.72f;
                double oAtk = Sv(live, "attack"); cy = StatBarRow(ix, cy, iw, lh, Loc.G("attack"), oAtk, DispN(oAtk, Sv(origAgg, "AttackDamage"), Sv(agg, "AttackDamage"), 1), "0", "");
                double oAsp = Sv(live, "aspd"); cy = StatBarRow(ix, cy, iw, lh, Loc.G("aspd"), oAsp, DispN(oAsp, Sv(origAgg, "AttackSpeed"), Sv(agg, "AttackSpeed"), 1), "0.##", "");
                double oCr = Sv(live, "critrate") * 100; cy = StatBarRow(ix, cy, iw, lh, Loc.G("critrate"), oCr, DispN(oCr, Sv(origAgg, "CriticalChance"), Sv(agg, "CriticalChance"), 10), "0.#", "%");
                double oCd = Sv(live, "critdmg") * 100; cy = StatBarRow(ix, cy, iw, lh, Loc.G("critdmg"), oCd, DispN(oCd, Sv(origAgg, "CriticalDamage"), Sv(agg, "CriticalDamage"), 10), "0", "%");
                double oPh = Sv(live, "Phys%") * 100; cy = StatBarRow(ix, cy, iw, lh, Loc.G("PhysicalDamagePercent"), oPh, DispN(oPh, Sv(origAgg, "PhysicalDamagePercent"), Sv(agg, "PhysicalDamagePercent"), 10), "0.#", "%");
                double oAoe = Sv(live, "AoE"); cy = StatBarRow(ix, cy, iw, lh, Loc.G("AoE"), oAoe, DispN(oAoe, Sv(origAgg, "AreaOfEffect"), Sv(agg, "AreaOfEffect"), 1), "0.#", "");
                double oMs = Sv(live, "mspd") * 100; cy = StatBarRow(ix, cy, iw, lh, Loc.G("mspd"), oMs, DispN(oMs, Sv(origAgg, "MovementSpeed"), Sv(agg, "MovementSpeed"), 1), "0", "");
                double oCdr = Sv(live, "cdr") * 100; cy = StatBarRow(ix, cy, iw, lh, Loc.G("cdr"), oCdr, DispN(oCdr, Sv(origAgg, "CooldownReduction"), Sv(agg, "CooldownReduction"), 10), "0.#", "%");
                DrawRect(ix, cy, iw, 1, new Color(1, 1, 1, 0.12f)); cy += 3;

                // live clear-time prediction: party-DPS factor from this hero's change × its per-stage share
                double msO = Sv(origAgg, "MovementSpeed"), msN = Sv(agg, "MovementSpeed");
                cy = DrawClearRows(ix, cy, iw, lh, hero, ratio, msO > 0.0001 ? msN / msO : 1.0);
                DrawRect(ix, cy, iw, 1, new Color(1, 1, 1, 0.12f)); cy += 3;

                // gear slots (click a row to view its sockets below; 換 swaps the item)
                _swapRects.Clear(); _focusRects.Clear();
                _load.TryGetValue(hero, out var arr);
                for (int s = 0; s < SlotParts.Length; s++)
                {
                    int key = (arr != null && s < arr.Length) ? arr[s] : 0;
                    bool changed = _orig.TryGetValue(hero, out var oa) && oa != null && s < oa.Length && oa[s] != key;
                    if (_focus == s) DrawRect(ix, cy, iw, lh, new Color(0.85f, 0.70f, 0.30f, 0.14f));
                    else if ((s & 1) == 1) DrawRect(ix, cy, iw, lh, new Color(1, 1, 1, 0.03f));
                    _focusRects.Add(new Rect(ix, cy, iw - 56, lh));
                    GUI.Label(new Rect(ix, cy, 44, lh), $"<color=#9aa3b0>{SlotL(s)}</color>", _label);
                    var stex = GearIconCache.Get(key);
                    if (stex != null) GUI.DrawTexture(new Rect(ix + 46, cy + 1, lh - 2, lh - 2), stex, ScaleMode.ScaleToFit);
                    var gt = GearDatabase.ByKey(key);
                    string ghex = changed ? "7fffa0" : GradeHex(gt != null ? gt.Grade : "");
                    string slvl = (gt != null && gt.Level > 0) ? $" <size=10><color=#8a93a0>Lv{gt.Level}</color></size>" : "";
                    float nameW = iw - 48 - lh - 56;
                    if (changed)
                    {
                        // a swapped slot shows the ORIGINAL on the right so old vs new are visible together
                        nameW -= 156;
                        GUI.Label(new Rect(ix + iw - 56 - 152, cy, 152, lh), $"<size=11><color=#6b7682>← 原 </color><color=#9aa3b0>{Nm(oa[s])}</color></size>", _label);
                    }
                    GUI.Label(new Rect(ix + 48 + lh, cy, nameW, lh), $"<color=#{ghex}>{Nm(key)}</color>{slvl}", _label);
                    bool open = _focus == s && _sockSlot < 0 && !_fitList;   // its items are the ones showing in the picker
                    var sr = new Rect(ix + iw - 52, cy + 1, 50, lh - 3);
                    GUI.Button(sr, open ? "▸ " + Loc.G("fit_swap") : Loc.G("fit_swap"), _btn); _swapRects.Add(sr);
                    if (open) DrawRect(ix - 2, cy, 2, lh, new Color(0.45f, 0.65f, 0.95f, 0.9f));   // marker: this slot's list is open
                    cy += lh;
                }

                // ---- sockets of the focused item (裝飾槽 / 雕刻槽 / 銘文槽); their effects fold into the stats above ----
                DrawRect(ix, cy, iw, 1, new Color(1, 1, 1, 0.12f)); cy += 3;
                int fkey = (arr != null && _focus < arr.Length) ? arr[_focus] : 0;
                string fgg = SlotGroup(hero, _focus);
                int[] cc = SlotSockets(hero, _focus);
                GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>{Loc.G("fit_sockets")}</color>  <color=#cdd5df>{SlotL(_focus)} · {Nm(fkey)}</color>", _dim); cy += lh;
                _sockRects.Clear(); _sockPosList.Clear();
                if (cc[0] + cc[1] + cc[2] == 0)
                {
                    GUI.Label(new Rect(ix + 12, cy, iw - 12, lh), $"<size=11><color=#67707d>{Loc.G("sock_none")}</color></size>", _label); cy += lh;
                }
                else
                {
                    // effective effect per cell: edited material wins, else the item's REAL applied effect
                    bool fUnchanged = origArr != null && gearArr != null && _focus < origArr.Length && _focus < gearArr.Length && origArr[_focus] == gearArr[_focus];
                    var fReal = RealSockets.Get(hero, _focus);
                    int[] fEdited = (hsock != null && hsock.TryGetValue(_focus, out var fea)) ? fea : null;
                    string[] tk = { "sock_deco", "sock_engrave", "sock_inscribe" };
                    char[] tc = { 'D', 'E', 'I' };
                    int pos = 0;
                    for (int ti = 0; ti < 3; ti++)
                    {
                        if (cc[ti] == 0) continue;
                        GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9aa3b0>{Loc.G(tk[ti])} ×{cc[ti]}</color>", _dim); cy += lh;
                        for (int j = 0; j < cc[ti]; j++)
                        {
                            var eff = FitCalc.EffectiveCell(fReal, fEdited, pos, fgg, fUnchanged);
                            bool filled = !string.IsNullOrEmpty(eff.Stat);
                            var cell = new Rect(ix + 12, cy, iw - 12, lh - 1);
                            DrawRect(cell.x, cell.y, cell.width, cell.height, filled ? new Color(0.25f, 0.42f, 0.40f, 0.22f) : new Color(1, 1, 1, 0.04f));
                            if (filled)
                            {
                                // material key: edited cell knows it; a real cell borrows a matching material's icon
                                int mk = (fEdited != null && pos < fEdited.Length && fEdited[pos] > 0) ? fEdited[pos] : MatCatalog.FindByEffect(tc[ti], fgg, eff.Stat, eff.Mod);
                                var tex = (mk > 0 && tc[ti] != 'I') ? GearIconCache.Get(mk) : null;   // inscriptions have no item icon
                                var ir = new Rect(ix + 15, cy + 1, lh - 3, lh - 3);
                                if (tex != null) GUI.DrawTexture(ir, tex, ScaleMode.ScaleToFit);
                                else GUI.Label(ir, "<color=#7fd0c2>◆</color>", _label);   // glyph instead of a broken square
                                GUI.Label(new Rect(ix + 16 + lh, cy, iw - 22 - lh, lh), $"<size=11><color=#bcd0ea>{StatL(eff.Stat)} {StatVal(eff.Stat, eff.Mod, eff.Value)}</color></size>", _label);
                            }
                            else GUI.Label(new Rect(ix + 18, cy, iw - 24, lh), $"<size=11>◇ <color=#67707d>{Loc.G("sock_empty")}</color></size>", _label);
                            _sockRects.Add(cell); _sockPosList.Add(pos);
                            pos++; cy += lh;
                        }
                    }
                }

                // side column (always on): the gear picker for the focused slot; the socket-material picker
                // and the load-fitting list temporarily take its place.
                {
                    float divX = x + baseW;
                    DrawRect(divX, _rect.y + Pad, 1, _rect.height - Pad * 2, new Color(1, 1, 1, 0.14f));
                    float pix = divX + 8, piw = PickerW - 16, pcy = _rect.y + Pad + lh;
                    if (_sockSlot >= 0) DrawSockPicker(pix, pcy, piw, lh, fgg);
                    else if (_fitList) DrawFitList(pix, pcy, piw, lh, hero);
                    else DrawPicker(pix, pcy, piw, lh, hero, _focus);
                }
                _resize.DrawGrip(_white, _rect);
            }
            catch { }
            finally { GUI.matrix = prevM; }
        }

        private void DrawPicker(float ix, float cy, float iw, float lh, int hero, int slot)
        {
            GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>{SlotL(slot)} — {Loc.G("fit_pickgear")}</color>", _label);
            cy += lh;
            var list = GearDatabase.BySlot(SlotParts[slot]);
            // weapon slots hold many gear TYPES (sword/bow/staff…); a class can only use its own type,
            // so restrict the list to the type the hero currently has equipped (e.g. ranger -> BOW only).
            if (slot == 0 || slot == 1)
            {
                int eq = (_orig.TryGetValue(hero, out var oa) && slot < oa.Length) ? oa[slot] : 0;
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
            float chx = ix, chy = cy, chh = lh - 1, cw = 50, gap = 3;
            DrawRect(chx, chy, cw, chh, _pickGrade == "" ? new Color(0.30f, 0.45f, 0.75f, 0.50f) : new Color(1, 1, 1, 0.06f));
            GUI.Label(new Rect(chx + 6, chy, cw, chh), Loc.G("fit_all"), _dim);
            _gradeRects.Add(new Rect(chx, chy, cw, chh)); _gradeKeys.Add(""); chx += cw + gap;
            for (int gi = 0; gi < GradeLadder.Length; gi++)
            {
                if (!present.Contains(GradeLadder[gi])) continue;
                if (chx + cw > ix + iw) { chx = ix; chy += chh + 2; }
                bool act = _pickGrade == GradeLadder[gi];
                DrawRect(chx, chy, cw, chh, act ? new Color(0.30f, 0.45f, 0.75f, 0.50f) : new Color(1, 1, 1, 0.06f));
                GUI.Label(new Rect(chx + 6, chy, cw, chh), $"<color=#{GradeHex(GradeLadder[gi])}>{GradeL(GradeLadder[gi])}</color>", _dim);
                _gradeRects.Add(new Rect(chx, chy, cw, chh)); _gradeKeys.Add(GradeLadder[gi]); chx += cw + gap;
            }
            cy = chy + chh + 3;
            // --- stat-filter chips: narrow to items that carry a chosen StatType (+傷害/+冷卻/+範圍/+攻速…) ---
            DrawStatChips(ix, ref cy, iw, lh, list);
            if (_pickGrade != "")
            {
                var fg = new List<GearTemplate>();
                foreach (var g in list) if (g.Grade == _pickGrade) fg.Add(g);
                list = fg;
            }
            if (_pickStat != "")
            {
                var fs = new List<GearTemplate>();
                foreach (var g in list) foreach (var st in g.Stats) if (st.Stat == _pickStat) { fs.Add(g); break; }
                list = fs;
            }
            int per = 4; float maxRowH = lh * 3.9f, iconSz = lh * 1.7f;
            int pages = Mathf.Max(1, (list.Count + per - 1) / per);
            _pickerPage = Mathf.Clamp(_pickerPage, 0, pages - 1);
            int start = _pickerPage * per; int shown = Mathf.Min(per, list.Count - start);
            _pickRects.Clear(); _pickKeys.Clear();
            int curKey = (_load.TryGetValue(hero, out var arr) && slot < arr.Length) ? arr[slot] : 0;
            // the variant the hero actually owns in this slot (each item ships as 2 inherent-roll variants).
            int ownedKey = (_orig.TryGetValue(hero, out var oa2) && slot < oa2.Length) ? oa2[slot] : 0;
            for (int i = 0; i < shown; i++)
            {
                var g = list[start + i];
                string own = g.Key == ownedKey ? "<color=#7fffa0>✓</color> " : "";
                string lvl = g.Level > 0 ? $" <size=10><color=#8a93a0>Lv{g.Level}</color></size>" : "";
                string nameStr = $"{own}<color=#{GradeHex(g.Grade)}><b>{Nm(g.Key)}</b></color>{lvl}";
                string stats = "";
                foreach (var st in g.Stats) stats += $"{StatL(st.Stat)} {StatVal(st.Stat, st.Mod, st.Value)}　";
                string statStr = $"<color=#9aa3b0>{stats}</color>";
                float tx = ix + iconSz + 6, statW = iw - iconSz - 10;
                float statH = _wrap.CalcHeight(new GUIContent(statStr), statW);
                float thisH = Mathf.Clamp(lh + statH + 4, lh * 1.9f, maxRowH);   // size the row to its wrapped stats
                bool cur = g.Key == curKey;
                if (cur) DrawRect(ix, cy, iw, thisH, new Color(0.30f, 0.45f, 0.75f, 0.30f)); else if ((i & 1) == 1) DrawRect(ix, cy, iw, thisH, new Color(1, 1, 1, 0.03f));
                var tex = GearIconCache.Get(g.Key);
                var iconR = new Rect(ix + 3, cy + 3, iconSz, iconSz);
                if (tex != null) GUI.DrawTexture(iconR, tex, ScaleMode.ScaleToFit);
                else { var pc = GUI.color; GUI.color = new Color(1, 1, 1, 0.10f); GUI.DrawTexture(iconR, _white); GUI.color = pc; }
                GUI.Label(new Rect(tx, cy + 1, statW, lh), nameStr, _label);
                GUI.Label(new Rect(tx, cy + lh - 1, statW, thisH - lh), statStr, _wrap);
                _pickRects.Add(new Rect(ix, cy, iw, thisH - 1)); _pickKeys.Add(g.Key); cy += thisH;
            }
            _ppPrev = new Rect(ix, cy, 26, lh - 2); _ppNext = new Rect(ix + 30, cy, 26, lh - 2);
            GUI.Button(_ppPrev, "◀", _btn); GUI.Button(_ppNext, "▶", _btn);
            GUI.Label(new Rect(ix + 64, cy, iw - 64, lh), $"<color=#9fb4cc>{_pickerPage + 1}/{pages}　{list.Count} {Loc.G("fit_count")}</color>", _dim);
        }

        // useful stats first; any others present in the list are appended after these
        private static readonly string[] StatChipOrder =
        {
            "AttackDamage", "PhysicalDamagePercent", "AttackSpeed", "CastSpeed", "CriticalChance", "CriticalDamage",
            "CooldownReduction", "AreaOfEffect", "MovementSpeed", "MaxHp", "Armor",
        };

        // stat-filter chips (single-select; click an active chip to clear). Derived from the stats actually
        // present in the slot's items, so a hero only ever sees relevant filters.
        private void DrawStatChips(float ix, ref float cy, float iw, float lh, List<GearTemplate> list)
        {
            _statRects.Clear(); _statKeys.Clear();
            var present = new HashSet<string>();
            foreach (var g in list) foreach (var st in g.Stats) present.Add(st.Stat);
            if (present.Count == 0) return;
            var ordered = new List<string>();
            foreach (var s in StatChipOrder) if (present.Remove(s)) ordered.Add(s);
            foreach (var s in present) ordered.Add(s);   // anything not in the priority list
            float chx = ix, chy = cy, chh = lh - 1, gap = 3;
            var on = new Color(0.26f, 0.55f, 0.46f, 0.55f); var off = new Color(1, 1, 1, 0.06f);
            float aw = 40;
            DrawRect(chx, chy, aw, chh, _pickStat == "" ? on : off);
            GUI.Label(new Rect(chx + 5, chy, aw, chh), Loc.G("fit_all"), _dim);
            _statRects.Add(new Rect(chx, chy, aw, chh)); _statKeys.Add(""); chx += aw + gap;
            foreach (var s in ordered)
            {
                string lbl = StatL(s);
                float cw = Mathf.Min(iw, _dim.CalcSize(new GUIContent(lbl)).x + 12);
                if (chx + cw > ix + iw) { chx = ix; chy += chh + 2; }
                DrawRect(chx, chy, cw, chh, _pickStat == s ? on : off);
                GUI.Label(new Rect(chx + 5, chy, cw, chh), $"<size=11><color=#cbe3da>{lbl}</color></size>", _dim);
                _statRects.Add(new Rect(chx, chy, cw, chh)); _statKeys.Add(s); chx += cw + gap;
            }
            cy = chy + chh + 3;
        }

        private void SaveCurrentFit()
        {
            int h = CurHero; if (h == 0) return;
            var f = new FitStore.Fit { Hero = h };
            if (_load.TryGetValue(h, out var g)) Array.Copy(g, f.Gear, Math.Min(g.Length, f.Gear.Length));
            if (_sockets.TryGetValue(h, out var sm))
                foreach (var kv in sm) { var a = new int[kv.Value.Length]; Array.Copy(kv.Value, a, a.Length); f.Sockets[kv.Key] = a; }
            int n = 0; foreach (var e in FitStore.LoadAll()) if (e.Hero == h) n++;
            f.Name = HeroProbe.HeroName(h) + " " + (n + 1);
            FitStore.Add(f); _savedFlash = 90f;
        }
        private void LoadFit(int storeIdx)
        {
            var all = FitStore.LoadAll();
            if (storeIdx < 0 || storeIdx >= all.Count) return;
            var f = all[storeIdx];
            int hi = _heroes.IndexOf(f.Hero); if (hi >= 0) _heroIdx = hi;
            var g = new int[SlotParts.Length]; Array.Copy(f.Gear, g, Math.Min(f.Gear.Length, g.Length)); _load[f.Hero] = g;
            var sm = SockOf(f.Hero); sm.Clear();
            foreach (var kv in f.Sockets) { var a = new int[kv.Value.Length]; Array.Copy(kv.Value, a, a.Length); sm[kv.Key] = a; }
            _fitList = false;
        }
        // live clear-time prediction block, drawn in the top panel under the stats. Each stage's clear =
        // active/F + idle/speed, where F = party-DPS factor from changing the focused hero (its DPS ratio ×
        // its damage share in that stage). Returns the new cy.
        private float DrawClearRows(float ix, float cy, float iw, float lh, int hero, double dpsRatio, double speedMult)
        {
            GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>⏱ {Loc.G("fit_cleartitle")}</color> <size=10><color=#6b7280>{HeroProbe.HeroName(hero)} ·{Loc.G("fit_dps")}×{dpsRatio:0.00}</color></size>", _label);
            cy += lh;
            if (_clearStages.Count == 0) { GUI.Label(new Rect(ix, cy, iw, lh), $"<size=11><color=#67707d>{Loc.G("fit_norun")}</color></size>", _label); return cy + lh; }

            string hk = hero.ToString();
            float barX = ix + 290, barW = iw - 290;
            double maxBase = 0, totSaved = 0, totBase = 0;
            // pre-pass for bar scaling
            foreach (var r in _clearStages)
            {
                double t = (r.ActiveSeconds + r.IdleSeconds);
                if (t > maxBase) maxBase = t;
            }
            if (maxBase <= 0) maxBase = 1;
            foreach (var r in _clearStages)
            {
                double focusDmg = 0; if (r.Party != null) foreach (var snap in r.Party) if (snap != null && snap.Character == hk) { focusDmg = snap.DamageDealt; break; }
                double share = r.Total > 0 ? focusDmg / r.Total : 0;
                double F = 1.0 + share * (dpsRatio - 1.0);
                var sim = ClearTimeSim.SimulateSplit(r.ActiveSeconds, r.IdleSeconds, F, speedMult);
                bool faster = sim.SavedSec > 0.05, slower = sim.SavedSec < -0.05;
                string nc = faster ? "#7fffa0" : (slower ? "#ff8a8a" : "#cdd5df");
                GUI.Label(new Rect(ix, cy, 96, lh), $"<size=11>{StageLabel(r.StageId)}</size>", _label);
                GUI.Label(new Rect(ix + 98, cy, 118, lh), $"<size=11><color=#8a93a0>{sim.BaseClear:0}s</color> → <color={nc}>{sim.NewClear:0}s</color></size>", _label);
                GUI.Label(new Rect(ix + 218, cy, 70, lh), $"<size=11><color={nc}>{(sim.SavedSec >= 0 ? "−" : "+")}{System.Math.Abs(sim.SavedSec):0.0}s</color></size>", _label);
                // bar: base track (gray) + predicted overlay (green shorter / red longer)
                float by = cy + lh * 0.5f - 3;
                DrawRect(barX, by, (float)(barW * sim.BaseClear / maxBase), 6, new Color(1, 1, 1, 0.10f));
                var col = faster ? new Color(0.50f, 1f, 0.63f, 0.85f) : (slower ? new Color(1f, 0.54f, 0.54f, 0.85f) : new Color(0.80f, 0.84f, 0.87f, 0.6f));
                DrawRect(barX, by, (float)(barW * sim.NewClear / maxBase), 6, col);
                cy += lh; totSaved += sim.SavedSec; totBase += sim.BaseClear;
            }
            if (totBase > 0)
            {
                double pct = totSaved / totBase * 100;
                string sc = totSaved > 0.05 ? "#7fffa0" : (totSaved < -0.05 ? "#ff8a8a" : "#cdd5df");
                GUI.Label(new Rect(ix, cy, iw, lh), $"<size=11><color=#9fb4cc>{Loc.G("fit_clearavg")}</color> <color={sc}>{(pct >= 0 ? "−" : "+")}{System.Math.Abs(pct):0.#}%</color></size>", _label);
                cy += lh;
            }
            return cy;
        }

        // friendly stage label, e.g. "2-1 TORMENT" → keep as-is (already short); fall back to the raw id
        private static string StageLabel(string stageId) => string.IsNullOrEmpty(stageId) ? "?" : stageId;

        private void DrawFitList(float ix, float cy, float iw, float lh, int hero)
        {
            _backRect = new Rect(ix, cy, 60, lh - 2); GUI.Button(_backRect, "◀ " + Loc.G("fit_back"), _btn);
            GUI.Label(new Rect(ix + 70, cy, iw - 70, lh), $"<color=#9fb4cc>{Loc.G("fit_loadtitle")}</color>", _label);
            cy += lh + 2;
            _fitLoadRects.Clear(); _fitDelRects.Clear(); _fitIdx.Clear();
            var all = FitStore.LoadAll();
            for (int i = 0; i < all.Count; i++)
            {
                var f = all[i];
                if ((i & 1) == 1) DrawRect(ix, cy, iw, lh, new Color(1, 1, 1, 0.03f));
                DrawRect(ix, cy, 3, lh, ClassColor(f.Hero));
                GUI.Label(new Rect(ix + 8, cy, iw - 8 - 78, lh), $"<color=#eaf3ee>{f.Name}</color>", _label);
                var lr = new Rect(ix + iw - 76, cy + 1, 36, lh - 3); GUI.Button(lr, Loc.G("fit_load"), _btn);
                var dr = new Rect(ix + iw - 36, cy + 1, 34, lh - 3); GUI.Button(dr, Loc.G("fit_del"), _btn);
                _fitLoadRects.Add(lr); _fitDelRects.Add(dr); _fitIdx.Add(i);
                cy += lh;
            }
            if (all.Count == 0) GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#67707d>{Loc.G("fit_nosaves")}</color>", _label);
        }

        private void DrawSockPicker(float ix, float cy, float iw, float lh, string gearGroup)
        {
            string tk = _sockType == 'D' ? "sock_deco" : (_sockType == 'E' ? "sock_engrave" : "sock_inscribe");
            _backRect = new Rect(ix, cy, 60, lh - 2); GUI.Button(_backRect, "◀ " + Loc.G("fit_back"), _btn);
            GUI.Label(new Rect(ix + 70, cy, iw - 70, lh), $"<color=#9fb4cc>{Loc.G(tk)} — {Loc.G("fit_pickmat")}</color>", _label);
            cy += lh;
            // only materials that actually grant something on this gear group
            var all = MatCatalog.ByType(_sockType);
            var list = new List<SockMat>();
            foreach (var mm in all) if (mm.HasFor(gearGroup)) list.Add(mm);
            // tier-filter chips (材料分品質)
            var present = new HashSet<int>();
            foreach (var mm in list) present.Add(mm.TierFor(gearGroup));
            _gradeRects.Clear(); _gradeKeys.Clear();
            float chx = ix, chy = cy, chh = lh - 1, cw = 42, gap = 3;
            DrawRect(chx, chy, cw, chh, _pickGrade == "" ? new Color(0.30f, 0.45f, 0.75f, 0.50f) : new Color(1, 1, 1, 0.06f));
            GUI.Label(new Rect(chx + 5, chy, cw, chh), Loc.G("fit_all"), _dim);
            _gradeRects.Add(new Rect(chx, chy, cw, chh)); _gradeKeys.Add(""); chx += cw + gap;
            for (int t = 1; t <= 12; t++)
            {
                if (!present.Contains(t)) continue;
                if (chx + cw > ix + iw) { chx = ix; chy += chh + 2; }
                string key = "T" + t; bool act = _pickGrade == key;
                DrawRect(chx, chy, cw, chh, act ? new Color(0.30f, 0.45f, 0.75f, 0.50f) : new Color(1, 1, 1, 0.06f));
                GUI.Label(new Rect(chx + 6, chy, cw, chh), $"<color=#bcd0ea>{key}</color>", _dim);
                _gradeRects.Add(new Rect(chx, chy, cw, chh)); _gradeKeys.Add(key); chx += cw + gap;
            }
            cy = chy + chh + 3;
            if (_pickGrade != "")
            {
                var f = new List<SockMat>();
                foreach (var mm in list) if ("T" + mm.TierFor(gearGroup) == _pickGrade) f.Add(mm);
                list = f;
            }
            int per = 6; float rowH = lh * 1.95f, iconSz = lh * 1.55f;
            int pages = Mathf.Max(1, (list.Count + per - 1) / per);
            _pickerPage = Mathf.Clamp(_pickerPage, 0, pages - 1);
            int start = _pickerPage * per; int shown = Mathf.Min(per, list.Count - start);
            _pickRects.Clear(); _pickKeys.Clear();
            // page 0 leads with an "empty / remove" option
            if (_pickerPage == 0)
            {
                var er = new Rect(ix, cy, iw, lh - 1); DrawRect(ix, cy, iw, lh, new Color(1, 1, 1, 0.03f));
                GUI.Label(new Rect(ix + 6, cy, iw - 8, lh), $"<color=#67707d>✕ {Loc.G("sock_empty")}</color>", _label);
                _pickRects.Add(er); _pickKeys.Add(0); cy += lh;
            }
            for (int i = 0; i < shown; i++)
            {
                var mm = list[start + i]; var e = mm.Effect(gearGroup);
                var r = new Rect(ix, cy, iw, rowH - 1); if ((i & 1) == 1) DrawRect(ix, cy, iw, rowH, new Color(1, 1, 1, 0.03f));
                float tx, tw;
                if (_sockType == 'I')   // inscription options are stat picks, not items -> a ◆ glyph, no icon box
                {
                    GUI.Label(new Rect(ix + 6, cy + (rowH - lh) * 0.5f, 18, lh), "<color=#7fd0c2>◆</color>", _label);
                    tx = ix + 24; tw = iw - 28;
                }
                else
                {
                    var tex = GearIconCache.Get(mm.Key);
                    var iconR = new Rect(ix + 3, cy + (rowH - iconSz) * 0.5f, iconSz, iconSz);
                    if (tex != null) GUI.DrawTexture(iconR, tex, ScaleMode.ScaleToFit);
                    else { var pc = GUI.color; GUI.color = new Color(1, 1, 1, 0.10f); GUI.DrawTexture(iconR, _white); GUI.color = pc; }
                    tx = ix + iconSz + 8; tw = iw - iconSz - 12;
                }
                GUI.Label(new Rect(tx, cy + 2, tw, lh), $"<color=#eaf3ee>{StatL(e.Stat)} {StatValRange(e.Stat, e.Mod, mm.MinFor(gearGroup), mm.MaxFor(gearGroup))}</color> <size=10><color=#8a93a0>T{mm.TierFor(gearGroup)}</color></size>", _label);
                if (_sockType != 'I') GUI.Label(new Rect(tx, cy + lh, tw, lh), $"<color=#9aa3b0>{Nm(mm.Key)}</color>", _dim);
                _pickRects.Add(r); _pickKeys.Add(mm.Key); cy += rowH;
            }
            _ppPrev = new Rect(ix, cy, 26, lh - 2); _ppNext = new Rect(ix + 30, cy, 26, lh - 2);
            GUI.Button(_ppPrev, "◀", _btn); GUI.Button(_ppNext, "▶", _btn);
            GUI.Label(new Rect(ix + 64, cy, iw - 64, lh), $"<color=#9fb4cc>{_pickerPage + 1}/{pages}　{list.Count} {Loc.G("fit_count")}</color>", _dim);
        }
    }
}
