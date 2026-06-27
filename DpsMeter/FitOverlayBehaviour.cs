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
                SkillDb.Load(ReadRes(asm, "fit_skills.json"));
                StageDb.Load(ReadRes(asm, "fit_stages.json"));
                Plugin.Logger?.LogInfo($"[fit] gear DB: {GearDatabase.Count} items, {SkillDb.Count} skills, {StageDb.Count} stages loaded");
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
        private readonly List<Rect> _sockRects = new List<Rect>();    // clickable socket cells (all columns, inline)
        private readonly List<int> _sockPosList = new List<int>();    // parallel: socket position per cell
        private readonly List<int> _sockHeroList = new List<int>();   // parallel: heroKey per socket cell
        private readonly List<int> _sockSlotList = new List<int>();   // parallel: gear slot per socket cell
        private readonly List<Rect> _focusRects = new List<Rect>();   // clickable gear rows (set focus) — parallel to _colHero/_colSlot
        private readonly List<int> _colHero = new List<int>();        // heroKey per gear-row hitbox (multi-column layout)
        private readonly List<int> _colSlot = new List<int>();        // slot index per gear-row hitbox
        private bool _fitList;          // load-fitting side panel open (temporarily overrides the always-on picker)
        private Rect _saveRect, _loadRect;
        private readonly List<Rect> _fitLoadRects = new List<Rect>();   // per saved-fit "load" hitboxes
        private readonly List<Rect> _fitDelRects = new List<Rect>();    // per saved-fit "delete" hitboxes
        private readonly List<int> _fitIdx = new List<int>();           // parallel: store index per shown row
        private readonly List<RunRecord> _clearStages = new List<RunRecord>();   // latest run per farmed stage (built on Reload), for the live clear-time block
        private readonly HashSet<int> _partyHeroes = new HashSet<int>();          // heroKeys that fought in the newest run (the current party — no save field for it)
        // cached iterative clear-time sim (recomputed only when the sandbox streams change — WaveSim is heavy)
        private readonly List<string> _simStage = new List<string>();
        private readonly List<double> _simBase = new List<double>(), _simNew = new List<double>(), _simFloor = new List<double>();
        private int _simHash = -1;
        // flat/percent split of the orig + sandbox loadouts, for live-anchored stat display (set each frame in OnGUI)
        private Dictionary<string, double> _fpFlatO, _fpPctO, _fpFlatN, _fpPctN;
        private float _savedFlash;      // frame counter for the "已儲存" toast
        private int _pickFirst;
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
                // current party = who fought in the newest run that has one (no deployed-party field in the save)
                _partyHeroes.Clear();
                for (int i = runs.Count - 1; i >= 0; i--)
                {
                    var r = runs[i]; if (r == null || r.Party == null || r.Party.Count == 0) continue;
                    foreach (var snap in r.Party) if (snap != null && snap.DamageDealt > 0 && int.TryParse(snap.Character, out var phk)) _partyHeroes.Add(phk);
                    break;
                }
            }
            catch { }
            if (_heroIdx >= _heroes.Count) _heroIdx = 0;
            _simHash = -1;   // force the iterative clear-time sim to recompute after a reload
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
            if (_focus != slot) { _pickGrade = ""; _pickStat = ""; _pickFirst = 0; }
            _focus = slot; _sockSlot = -1; _fitList = false;
        }
        // focus a (hero, slot) cell from any column: the picker + sockets below follow it
        private void FocusColumn(int hero, int slot)
        {
            int hi = _heroes.IndexOf(hero);
            bool changed = (hi >= 0 && hi != _heroIdx) || _focus != slot;
            if (hi >= 0) _heroIdx = hi;
            _focus = slot; _sockSlot = -1; _fitList = false;
            if (changed) { _pickGrade = ""; _pickStat = ""; _pickFirst = 0; }
        }
        // open the side-column material picker for a socket; its type (D/E/I) follows the position
        private void OpenSockPicker(int slot, int pos)
        {
            var cc = SlotSockets(CurHero, slot);
            _sockType = pos < cc[0] ? 'D' : (pos < cc[0] + cc[1] ? 'E' : 'I');
            _sockSlot = slot; _sockPos = pos; _fitList = false; _pickFirst = 0; _pickGrade = ""; _pickStat = "";
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
            // mouse-wheel scrolls the item / material list (both share _pickFirst; the short fit-list doesn't scroll)
            if (!_fitList) { float wd = InputCompat.WheelDelta(Slot); if (wd != 0f) _pickFirst = Mathf.Max(0, _pickFirst - Mathf.RoundToInt(wd / 120f)); }
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
                        if (_gradeRects[i].Contains(m)) { _pickGrade = _gradeKeys[i]; _pickFirst = 0; return; }
                    for (int i = 0; i < _statRects.Count && i < _statKeys.Count; i++)
                        if (_statRects[i].Contains(m)) { _pickStat = _pickStat == _statKeys[i] ? "" : _statKeys[i]; _pickFirst = 0; return; }
                    for (int i = 0; i < _pickRects.Count && i < _pickKeys.Count; i++)
                        if (_pickRects[i].Contains(m)) { SetSocket(_sockSlot, _sockPos, _pickKeys[i]); _sockSlot = -1; return; }
                }
                else   // always-on gear picker for the focused slot
                {
                    for (int i = 0; i < _gradeRects.Count && i < _gradeKeys.Count; i++)
                        if (_gradeRects[i].Contains(m)) { _pickGrade = _gradeKeys[i]; _pickFirst = 0; return; }
                    for (int i = 0; i < _statRects.Count && i < _statKeys.Count; i++)
                        if (_statRects[i].Contains(m)) { _pickStat = _pickStat == _statKeys[i] ? "" : _statKeys[i]; _pickFirst = 0; return; }
                    for (int i = 0; i < _pickRects.Count && i < _pickKeys.Count; i++)
                        if (_pickRects[i].Contains(m)) { SetSlot(_focus, _pickKeys[i]); return; }   // keep list open after a swap
                }
                if (_resetRect.Contains(m)) { ResetLoadout(); return; }
                // click a gear slot in any column → focus that (hero, slot); the picker on the right follows
                for (int i = 0; i < _focusRects.Count && i < _colHero.Count && i < _colSlot.Count; i++)
                    if (_focusRects[i].Contains(m)) { FocusColumn(_colHero[i], _colSlot[i]); return; }
                for (int i = 0; i < _sockRects.Count && i < _sockPosList.Count; i++)
                    if (_sockRects[i].Contains(m)) { FocusColumn(_sockHeroList[i], _sockSlotList[i]); OpenSockPicker(_sockSlotList[i], _sockPosList[i]); return; }
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
        // live-anchored display using the flat/percent split.
        //  • when the gear contributes a flat base for this stat (origAgg = flat·pct > 0) use the RATIO
        //    live·(newAgg/origAgg) — the raw flat units cancel, so it's scale-correct for any stat (attack,
        //    AttackSpeed via the weapon base, etc.). This is the robust common path.
        //  • only when the gear gives NO flat base (e.g. 範圍 — its base lives on the character) fall back to
        //    scaling the live value by the percent factor, so a percent socket isn't lost to 0×factor.
        private double DispFP(double live, string stat, double aggScale)
        {
            double of = (_fpFlatO != null && _fpFlatO.TryGetValue(stat, out var a)) ? a : 0;
            double op = (_fpPctO != null && _fpPctO.TryGetValue(stat, out var b)) ? b : 1.0;
            double nf = (_fpFlatN != null && _fpFlatN.TryGetValue(stat, out var c)) ? c : 0;
            double np = (_fpPctN != null && _fpPctN.TryGetValue(stat, out var d)) ? d : 1.0;
            if (op <= 1e-9) op = 1.0; if (np <= 1e-9) np = 1.0;
            double origAgg = of * op, newAgg = nf * np;
            if (origAgg > 1e-6) return live * (newAgg / origAgg);
            return live * (np / op) + ((nf - of) / aggScale) * np;
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
                // party heroes get a column each (shown side by side); fall back to all heroes if no party info
                var colHeroes = new List<int>();
                foreach (var hc in _heroes) if (_partyHeroes.Contains(hc)) colHeroes.Add(hc);
                if (colHeroes.Count == 0) colHeroes.AddRange(_heroes);
                int colN = Mathf.Max(1, colHeroes.Count);
                if (_heroes.Count > 0 && !colHeroes.Contains(CurHero)) _heroIdx = _heroes.IndexOf(colHeroes[0]);   // focus a visible column
                // main column + always-on item/material side-column to the RIGHT (expand, don't replace the page)
                const bool sideOpen = true;   // the gear picker is now persistent (sock/fit-list temporarily override it)
                float baseW = Mathf.Max(560f, Plugin.FitPanelWidth.Value, colN * 282f + Pad * 2);
                _rect.width = baseW + PickerW;
                float x = _rect.x, ix = x + Pad, w = _rect.width, iw = baseW - Pad * 2;

                // tallest column: header(2)+stats(8) + per slot [gear row + ceil(sockets/2) wrapped chip rows]
                int maxSlotRows = 0;
                foreach (var hcol in colHeroes)
                {
                    int sr = 0;
                    for (int s = 0; s < SlotParts.Length; s++) { var cc = SlotSockets(hcol, s); int t = cc[0] + cc[1] + cc[2]; sr += 1 + (t > 0 ? (t + 1) / 2 : 0); }
                    if (sr > maxSlotRows) maxSlotRows = sr;
                }
                int clearRows = (_clearStages.Count > 0 ? _clearStages.Count + 3 : 2);   // title + legend + per-stage + average
                int mainRows = clearRows + 6 + maxSlotRows;   // clear-time + (header+dps+4 stat-rows) + gear+chip-sockets
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

                // computed stats (sandbox gear + effective sockets) for the EXPANDED hero.
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
                // flat/percent split so percent sockets on no-flat-base stats (e.g. 範圍) still scale the live value
                _fpFlatO = new Dictionary<string, double>(); _fpPctO = new Dictionary<string, double>();
                _fpFlatN = new Dictionary<string, double>(); _fpPctN = new Dictionary<string, double>();
                FitCalc.LoadoutFP(origArr, origLines, _fpFlatO, _fpPctO);
                FitCalc.LoadoutFP(gearArr, sbLines, _fpFlatN, _fpPctN);
                _liveStats.TryGetValue(hero, out var live);   // real character stats (anchor)
                double ratio = LiveRatio(live);
                // per-hero DPS ratios → party clear-time at the TOP (combines EVERY member's edits). Move speed is
                // deliberately NOT modelled: the game's "no-damage" time is monster-approach (uses MONSTER speed) +
                // fixed spawn/cooldown timers, and 120 real runs show +14% hero move-speed changed idle by ~0s.
                // skills per hero (from run snapshots) for the iterative WaveSim
                var skKeys = new Dictionary<int, List<int>>(); var skLvl = new Dictionary<int, List<int>>();
                foreach (var rr0 in _clearStages) if (rr0.Party != null) foreach (var snap in rr0.Party)
                {
                    if (snap == null || snap.Skills == null || snap.Skills.Count == 0 || !int.TryParse(snap.Character, out var hk) || skKeys.ContainsKey(hk)) continue;
                    var kl = new List<int>(); var ll = new List<int>();
                    foreach (var sk in snap.Skills) if (sk.Key > 0) { kl.Add(sk.Key); ll.Add(sk.Level); }
                    if (kl.Count > 0) { skKeys[hk] = kl; skLvl[hk] = ll; }
                }
                // per-hero DPS ratios + attack streams (focused uses the _fp set above; others via HeroRatio)
                var ratioByHero = new Dictionary<int, double>();
                var curStreams = new Dictionary<int, List<AtkStream>>();
                var sbStreams = new Dictionary<int, List<AtkStream>>();
                AddStreams(hero, live, skKeys, skLvl, curStreams, sbStreams);
                foreach (var hh in _heroes)
                {
                    if (hh == hero) { ratioByHero[hh] = ratio; continue; }
                    ratioByHero[hh] = HeroRatio(hh);   // sets _fp* for hh
                    _liveStats.TryGetValue(hh, out var lvh);
                    AddStreams(hh, lvh, skKeys, skLvl, curStreams, sbStreams);
                }
                int simHash = StreamHash(sbStreams);
                if (simHash != _simHash) { _simHash = simHash; RecomputeSim(curStreams, sbStreams); }
                cy = DrawClearRows(ix, cy, iw, lh, hero, ratioByHero);
                DrawRect(ix, cy, iw, 1, new Color(1, 1, 1, 0.12f)); cy += 3;

                // ===== party heroes side by side, one column each (all expanded); click a gear slot to focus it =====
                _focusRects.Clear(); _colHero.Clear(); _colSlot.Clear();
                _sockRects.Clear(); _sockPosList.Clear(); _sockHeroList.Clear(); _sockSlotList.Clear();
                float colW = (iw - (colHeroes.Count - 1) * 4) / colHeroes.Count;
                float colTop = cy, colBot = cy;
                for (int c = 0; c < colHeroes.Count; c++)
                {
                    int h = colHeroes[c];
                    float cxh = ix + c * (colW + 4);
                    if (c > 0) DrawRect(cxh - 2, colTop, 1, _rect.y + _rect.height - Pad - colTop, new Color(1, 1, 1, 0.08f));
                    float b = DrawHeroColumn(cxh, colTop, colW, lh, h, h == hero, ratioByHero.TryGetValue(h, out var rv) ? rv : 1.0);
                    if (b > colBot) colBot = b;
                }
                cy = colBot;

                string fgg = SlotGroup(hero, _focus);   // gear group of the focused slot (for the socket-material picker)

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
            var statSet = new HashSet<string>();
            foreach (var g in list) foreach (var st in g.Stats) statSet.Add(st.Stat);
            DrawStatChips(ix, ref cy, iw, lh, statSet);
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
            float maxRowH = lh * 3.9f, iconSz = lh * 1.7f;
            float botY = _rect.y + _rect.height - Pad - lh;   // leave a row for the footer
            _pickFirst = Mathf.Clamp(_pickFirst, 0, Mathf.Max(0, list.Count - 1));
            _pickRects.Clear(); _pickKeys.Clear();
            int curKey = (_load.TryGetValue(hero, out var arr) && slot < arr.Length) ? arr[slot] : 0;
            // the variant the hero actually owns in this slot (each item ships as 2 inherent-roll variants).
            int ownedKey = (_orig.TryGetValue(hero, out var oa2) && slot < oa2.Length) ? oa2[slot] : 0;
            int i = _pickFirst;
            for (; i < list.Count; i++)
            {
                var g = list[i];
                string own = g.Key == ownedKey ? "<color=#7fffa0>✓</color> " : "";
                string lvl = g.Level > 0 ? $" <size=10><color=#8a93a0>Lv{g.Level}</color></size>" : "";
                string nameStr = $"{own}<color=#{GradeHex(g.Grade)}><b>{Nm(g.Key)}</b></color>{lvl}";
                string stats = "";
                foreach (var st in g.Stats) stats += $"{StatL(st.Stat)} {StatVal(st.Stat, st.Mod, st.Value)}　";
                string statStr = $"<color=#9aa3b0>{stats}</color>";
                float tx = ix + iconSz + 6, statW = iw - iconSz - 10;
                float statH = _wrap.CalcHeight(new GUIContent(statStr), statW);
                float thisH = Mathf.Clamp(lh + statH + 4, lh * 1.9f, maxRowH);   // size the row to its wrapped stats
                if (cy + thisH > botY && i > _pickFirst) break;   // doesn't fit — stop (always show ≥1)
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
            DrawScrollFooter(ix, _rect.y + _rect.height - Pad - lh, iw, lh, _pickFirst, i, list.Count);
        }

        // footer for the scrollable pickers: shows the visible range / total and a thin scrollbar
        private void DrawScrollFooter(float ix, float y, float iw, float lh, int first, int afterLast, int count)
        {
            GUI.Label(new Rect(ix, y, iw - 70, lh), $"<size=11><color=#9fb4cc>↕ {(count == 0 ? 0 : first + 1)}–{afterLast} / {count} {Loc.G("fit_count")}</color></size>", _dim);
            if (count > 0 && afterLast - first < count)   // scrollbar only when not everything fits
            {
                float trackX = ix + iw - 60, trackW = 56, ty = y + lh * 0.45f;
                DrawRect(trackX, ty, trackW, 4, new Color(1, 1, 1, 0.10f));
                float frac = (float)(afterLast - first) / count, pos = (float)first / count;
                DrawRect(trackX + trackW * pos, ty, Mathf.Max(6f, trackW * frac), 4, new Color(0.45f, 0.65f, 0.95f, 0.8f));
            }
        }

        // useful stats first; any others present in the list are appended after these
        private static readonly string[] StatChipOrder =
        {
            "AttackDamage", "PhysicalDamagePercent", "AttackSpeed", "CastSpeed", "CriticalChance", "CriticalDamage",
            "CooldownReduction", "AreaOfEffect", "MovementSpeed", "MaxHp", "Armor",
        };

        // stat-filter chips (single-select; click an active chip to clear). `present` = the stats actually
        // offered by the current list (gear base stats, or socket-material effects), so only relevant chips show.
        private void DrawStatChips(float ix, ref float cy, float iw, float lh, HashSet<string> present)
        {
            _statRects.Clear(); _statKeys.Clear();
            if (present == null || present.Count == 0) return;
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
        // DPS ratio (sandbox edits vs original) for ANY hero. Builds the hero's flat/percent split (sets the
        // _fp* fields) then anchors the formula to the hero's LIVE stats — see LiveRatio for why.
        private double HeroRatio(int hero)
        {
            if (!_load.TryGetValue(hero, out var gearArr) || !_orig.TryGetValue(hero, out var origArr)) return 1.0;
            if (!_liveStats.TryGetValue(hero, out var live) || live == null) return 1.0;
            _sockets.TryGetValue(hero, out var hsock);
            var sbLines = new Dictionary<int, List<GearStat>>();
            var origLines = new Dictionary<int, List<GearStat>>();
            for (int s = 0; s < SlotParts.Length; s++)
            {
                var realO = RealSockets.Get(hero, s);
                if (realO != null) { var lo = new List<GearStat>(); foreach (var c in realO) if (!string.IsNullOrEmpty(c.Stat)) lo.Add(c); if (lo.Count > 0) origLines[s] = lo; }
                bool unchanged = s < origArr.Length && s < gearArr.Length && origArr[s] == gearArr[s];
                int[] edited = (hsock != null && hsock.TryGetValue(s, out var ea)) ? ea : null;
                string gg = SlotGroup(hero, s);
                var scc = SlotSockets(hero, s); int n = scc[0] + scc[1] + scc[2];
                if (n > 0) { var ls = new List<GearStat>(); for (int p = 0; p < n; p++) { var e = FitCalc.EffectiveCell(realO, edited, p, gg, unchanged); if (!string.IsNullOrEmpty(e.Stat)) ls.Add(e); } if (ls.Count > 0) sbLines[s] = ls; }
            }
            _fpFlatO = new Dictionary<string, double>(); _fpPctO = new Dictionary<string, double>();
            _fpFlatN = new Dictionary<string, double>(); _fpPctN = new Dictionary<string, double>();
            FitCalc.LoadoutFP(origArr, origLines, _fpFlatO, _fpPctO);
            FitCalc.LoadoutFP(gearArr, sbLines, _fpFlatN, _fpPctN);
            return LiveRatio(live);
        }

        // DPS ratio from LIVE-anchored combat stats (real attack/aspd/crit), using the _fp* fields already set
        // for this hero. The raw collapsed aggregate zeroes out stats the gear gives only as a percent (e.g. a
        // weapon's AttackSpeed) → the formula's ÷0 made the ratio explode (×3406). Anchoring to live + the
        // per-stat DispFP change keeps it realistic. Units cancel in the ratio, so absolute scale is irrelevant.
        private double LiveRatio(Dictionary<string, double> live)
        {
            double oAtk = Sv(live, "attack"), oAsp = Sv(live, "aspd");
            double oCrR = Sv(live, "critrate"), oCrD = Sv(live, "critdmg"), oPh = Sv(live, "Phys%");
            var oc = new CombatStats { AttackDamage = oAtk, AttackSpeed = oAsp, CritChance = oCrR, CritDamage = oCrD, DamageMult = 1.0 + oPh };
            var nc = new CombatStats
            {
                AttackDamage = DispFP(oAtk, "AttackDamage", 1),
                AttackSpeed = DispFP(oAsp, "AttackSpeed", 1),
                CritChance = DispFP(oCrR * 100, "CriticalChance", 10) / 100.0,
                CritDamage = DispFP(oCrD * 100, "CriticalDamage", 10) / 100.0,
                DamageMult = 1.0 + DispFP(oPh * 100, "PhysicalDamagePercent", 10) / 100.0,
            };
            double od = DamageFormula.ExpectedDps(oc);
            return od > 0 ? DamageFormula.ExpectedDps(nc) / od : 1.0;
        }

        // build a hero's CURRENT (live) and SANDBOX (DispFP'd) attack streams. Must be called with this hero's
        // _fp* fields already set (so DispFP gives its sandbox stats). Skills come from its run snapshot.
        private void AddStreams(int hero, Dictionary<string, double> live,
            Dictionary<int, List<int>> skKeys, Dictionary<int, List<int>> skLvl,
            Dictionary<int, List<AtkStream>> cur, Dictionary<int, List<AtkStream>> sb)
        {
            if (live == null) return;
            double a = Sv(live, "attack"), asp = Sv(live, "aspd"), cr = Sv(live, "critrate"), cd = Sv(live, "critdmg"), cdr = Sv(live, "cdr");
            skKeys.TryGetValue(hero, out var ks); skLvl.TryGetValue(hero, out var ls);
            cur[hero] = StreamBuilder.Build(hero, a, asp, cr, cd, cdr, ks, ls);
            sb[hero] = StreamBuilder.Build(hero,
                DispFP(a, "AttackDamage", 1), DispFP(asp, "AttackSpeed", 1),
                DispFP(cr * 100, "CriticalChance", 10) / 100.0, DispFP(cd * 100, "CriticalDamage", 10) / 100.0,
                DispFP(cdr * 100, "CooldownReduction", 10) / 100.0, ks, ls);
        }

        // cheap change-hash over the sandbox streams, so RecomputeSim only runs when an edit changes something.
        private static int StreamHash(Dictionary<int, List<AtkStream>> sb)
        {
            int h = 17;
            foreach (var kv in sb) { h = h * 31 + kv.Key; foreach (var s in kv.Value) h = h * 31 + (int)s.PerHit + (int)(s.Interval * 137) + s.Targets; }
            return h;
        }

        // run the iterative WaveSim for every farmed stage: calibrate the effHP scale so the CURRENT streams
        // reproduce the measured clear, then predict with the SANDBOX streams. Overkill / cadence / AoE fall out.
        private void RecomputeSim(Dictionary<int, List<AtkStream>> cur, Dictionary<int, List<AtkStream>> sb)
        {
            _simStage.Clear(); _simBase.Clear(); _simNew.Clear(); _simFloor.Clear();
            foreach (var r in _clearStages)
            {
                double measured = r.ActiveSeconds + r.IdleSeconds;
                int wc = r.WaveDurations != null ? r.WaveDurations.Count : 0;
                _simStage.Add(r.StageId); _simBase.Add(measured);
                var curS = new List<AtkStream>(); var sbS = new List<AtkStream>();
                if (r.Party != null) foreach (var snap in r.Party)
                {
                    if (snap == null || snap.DamageDealt <= 0 || !int.TryParse(snap.Character, out var hk)) continue;
                    if (cur.TryGetValue(hk, out var cs)) curS.AddRange(cs);
                    if (sb.TryGetValue(hk, out var ss)) sbS.AddRange(ss);
                }
                if (!StageDb.TryGet(r.StageId, out var st) || st.Waves <= 0 || curS.Count == 0 || sbS.Count == 0)
                {   // no sim data → fall back to the floor model (measured unchanged baseline)
                    _simNew.Add(measured); _simFloor.Add(ClearTimeSim.FixedFloor(wc > 0 ? wc : st.Waves));
                    continue;
                }
                double k = StreamBuilder.CalibrateHpScale(st, curS, measured);   // effHP scale so current ≈ measured
                double pred = WaveSim.StageTime(st.Waves, st.Mpw, st.EffHp * k, sbS, 0, 0, st.BossHp * k);
                _simNew.Add(pred); _simFloor.Add(ClearTimeSim.FixedFloor(st.Waves));
            }
        }

        // live clear-time prediction block, drawn in the top panel under the stats. Each stage's clear =
        // active/F + idle, where the party-DPS factor F = Σ (each party member's damage share × its DPS ratio)
        // — so editing ANY party member moves it. idle is NOT scaled (move speed barely affects clear time —
        // see the IL2CPP/empirical investigation: monster-approach + spawn/cooldown timers dominate). Returns cy.
        private float DrawClearRows(float ix, float cy, float iw, float lh, int hero, Dictionary<int, double> ratioByHero)
        {
            // header: the party + each member's ratio (★ members), so it's clear the prediction is whole-party
            string ph = "";
            foreach (var h in _heroes)
            {
                if (!_partyHeroes.Contains(h)) continue;
                double rr = ratioByHero != null && ratioByHero.TryGetValue(h, out var v) ? v : 1.0;
                string col = rr > 1.001 ? "#7fffa0" : (rr < 0.999 ? "#ff8a8a" : "#9aa3b0");
                ph += $"<color={col}>{HeroProbe.HeroName(h)}×{rr:0.00}</color>  ";
            }
            if (ph == "") { double rr = ratioByHero != null && ratioByHero.TryGetValue(hero, out var v) ? v : 1.0; ph = $"{HeroProbe.HeroName(hero)}×{rr:0.00}"; }
            GUI.Label(new Rect(ix, cy, iw, lh), $"<color=#9fb4cc>⏱ {Loc.G("fit_cleartitle")}</color>  <size=10>{ph}</size>", _label);
            cy += lh;
            // legend: 地板 = fixed spawn/end floor (DPS can't cut it); 戰 = the DPS-bound kill+cadence time
            GUI.Label(new Rect(ix + 258, cy, iw - 258, lh), $"<size=10><color=#7d8aa0>■地板=固定</color>  <color=#9fb4cc>■戰=DPS壓縮</color></size>", _dim);
            cy += (int)(lh * 0.7f);
            if (_clearStages.Count == 0) { GUI.Label(new Rect(ix, cy, iw, lh), $"<size=11><color=#67707d>{Loc.G("fit_norun")}</color></size>", _label); return cy + lh; }

            float barX = ix + 416, barW = iw - 416;
            double maxBase = 0, totSaved = 0, totBase = 0;
            for (int i = 0; i < _simBase.Count; i++) if (_simBase[i] > maxBase) maxBase = _simBase[i];
            if (maxBase <= 0) maxBase = 1;
            // each row = the cached iterative sim: BaseClear (measured) → NewClear (predicted), floor/battle split
            for (int i = 0; i < _simStage.Count; i++)
            {
                double baseC = _simBase[i], newC = _simNew[i], floor = System.Math.Min(_simFloor[i], baseC);
                double saved = baseC - newC, battle = System.Math.Max(0, newC - floor);
                bool faster = saved > 0.05, slower = saved < -0.05;
                string nc = faster ? "#7fffa0" : (slower ? "#ff8a8a" : "#cdd5df");
                GUI.Label(new Rect(ix, cy, 92, lh), $"<size=11>{StageLabel(_simStage[i])}</size>", _label);
                GUI.Label(new Rect(ix + 94, cy, 104, lh), $"<size=11><color=#8a93a0>{baseC:0}s</color> → <color={nc}>{newC:0}s</color></size>", _label);
                GUI.Label(new Rect(ix + 200, cy, 54, lh), $"<size=11><color={nc}>{(saved >= 0 ? "−" : "+")}{System.Math.Abs(saved):0.0}s</color></size>", _label);
                GUI.Label(new Rect(ix + 258, cy, 156, lh), $"<size=11><color=#7d8aa0>地板{floor:0}</color> + <color={nc}>戰{battle:0}</color></size>", _label);
                float by = cy + lh * 0.5f - 3;
                DrawRect(barX, by, (float)(barW * baseC / maxBase), 6, new Color(1, 1, 1, 0.10f));
                float floorW = (float)(barW * floor / maxBase), newW = (float)(barW * newC / maxBase);
                DrawRect(barX, by, Mathf.Min(floorW, newW), 6, new Color(0.42f, 0.48f, 0.60f, 0.80f));
                var col = faster ? new Color(0.50f, 1f, 0.63f, 0.85f) : (slower ? new Color(1f, 0.54f, 0.54f, 0.85f) : new Color(0.80f, 0.84f, 0.87f, 0.6f));
                if (newW > floorW) DrawRect(barX + floorW, by, newW - floorW, 6, col);
                cy += lh; totSaved += saved; totBase += baseC;
            }
            if (totBase > 0)
            {
                double pct = totSaved / totBase * 100;
                string sc = totSaved > 0.05 ? "#7fffa0" : (totSaved < -0.05 ? "#ff8a8a" : "#cdd5df");
                GUI.Label(new Rect(ix, cy, iw, lh), $"<size=11><color=#9fb4cc>{Loc.G("fit_clearavg")}</color> <color={sc}>{(pct >= 0 ? "−" : "+")}{System.Math.Abs(pct):0.#}%</color>  <size=9><color=#6b7280>迭代模擬</color></size></size>", _label);
                cy += lh;
            }
            return cy;
        }

        // friendly stage label, e.g. "2-1 TORMENT" → keep as-is (already short); fall back to the raw id
        private static string StageLabel(string stageId) => string.IsNullOrEmpty(stageId) ? "?" : stageId;

        // one hero's column: header (★ name + DPS×ratio) + compact stats + the 10 gear slots (click to focus).
        // Sets the _fp* fields for this hero so its stat values reflect its own edits. Returns the column bottom.
        private float DrawHeroColumn(float cx, float top, float colW, float lh, int hero, bool focused, double ratioH)
        {
            float cy = top, iw = colW;
            _load.TryGetValue(hero, out var gearArr);
            _orig.TryGetValue(hero, out var origArr);
            _sockets.TryGetValue(hero, out var hsock);
            var sbLines = new Dictionary<int, List<GearStat>>();
            var origLines = new Dictionary<int, List<GearStat>>();
            for (int s = 0; s < SlotParts.Length; s++)
            {
                var realO = RealSockets.Get(hero, s);
                if (realO != null) { var lo = new List<GearStat>(); foreach (var c in realO) if (!string.IsNullOrEmpty(c.Stat)) lo.Add(c); if (lo.Count > 0) origLines[s] = lo; }
                bool unchanged = origArr != null && gearArr != null && s < origArr.Length && s < gearArr.Length && origArr[s] == gearArr[s];
                int[] edited = (hsock != null && hsock.TryGetValue(s, out var ea)) ? ea : null;
                string gg = SlotGroup(hero, s);
                var scc = SlotSockets(hero, s); int n = scc[0] + scc[1] + scc[2];
                if (n > 0) { var ls = new List<GearStat>(); for (int p = 0; p < n; p++) { var e = FitCalc.EffectiveCell(realO, edited, p, gg, unchanged); if (!string.IsNullOrEmpty(e.Stat)) ls.Add(e); } if (ls.Count > 0) sbLines[s] = ls; }
            }
            _fpFlatO = new Dictionary<string, double>(); _fpPctO = new Dictionary<string, double>();
            _fpFlatN = new Dictionary<string, double>(); _fpPctN = new Dictionary<string, double>();
            FitCalc.LoadoutFP(origArr, origLines, _fpFlatO, _fpPctO);
            FitCalc.LoadoutFP(gearArr, sbLines, _fpFlatN, _fpPctN);
            _liveStats.TryGetValue(hero, out var live);

            // header + DPS
            DrawRect(cx, cy, iw, lh, focused ? new Color(0.30f, 0.45f, 0.75f, 0.30f) : new Color(1, 1, 1, 0.06f));
            DrawRect(cx, cy, 3, lh, ClassColor(hero));
            string star = _partyHeroes.Contains(hero) ? "<color=#ffd86b>★</color>" : "";
            GUI.Label(new Rect(cx + 8, cy, iw - 8, lh), $"{star}<b><color=#dfe7f0>{HeroProbe.HeroName(hero)}</color></b>", _label); cy += lh;
            double dpsH = (_measDps.TryGetValue(hero, out var mh) && mh > 0) ? mh * ratioH : 0;
            string rcH = ratioH > 1.001 ? "#7fffa0" : (ratioH < 0.999 ? "#ff8a8a" : "#9aa3b0");
            GUI.Label(new Rect(cx + 8, cy, iw - 8, lh), $"<size=11><color=#9fb4cc>{Loc.G("fit_dps")}</color> <b>{FmtNum(dpsH)}</b> <color={rcH}>×{ratioH:0.00}</color></size>", _label); cy += lh;

            // compact stats — 2 per row (4 rows) so the column stays short; coloured vs the live/original value
            float hw = iw * 0.5f;
            double oAtk = Sv(live, "attack"), oAsp = Sv(live, "aspd");
            ColStatAt(cx, cy, hw, lh, Loc.G("attack"), oAtk, DispFP(oAtk, "AttackDamage", 1), "0", ""); ColStatAt(cx + hw, cy, hw, lh, Loc.G("aspd"), oAsp, DispFP(oAsp, "AttackSpeed", 1), "0.##", ""); cy += lh;
            double oCr = Sv(live, "critrate") * 100, oCd = Sv(live, "critdmg") * 100;
            ColStatAt(cx, cy, hw, lh, Loc.G("critrate"), oCr, DispFP(oCr, "CriticalChance", 10), "0.#", "%"); ColStatAt(cx + hw, cy, hw, lh, Loc.G("critdmg"), oCd, DispFP(oCd, "CriticalDamage", 10), "0", "%"); cy += lh;
            double oPh = Sv(live, "Phys%") * 100, oAoe = Sv(live, "AoE");
            ColStatAt(cx, cy, hw, lh, Loc.G("PhysicalDamagePercent"), oPh, DispFP(oPh, "PhysicalDamagePercent", 10), "0.#", "%"); ColStatAt(cx + hw, cy, hw, lh, Loc.G("AoE"), oAoe, DispFP(oAoe, "AreaOfEffect", 1), "0.#", ""); cy += lh;
            double oMs = Sv(live, "mspd") * 100, oCdr = Sv(live, "cdr") * 100;
            ColStatAt(cx, cy, hw, lh, Loc.G("mspd"), oMs, DispFP(oMs, "MovementSpeed", 1), "0", ""); ColStatAt(cx + hw, cy, hw, lh, Loc.G("cdr"), oCdr, DispFP(oCdr, "CooldownReduction", 10), "0.#", "%"); cy += lh;
            DrawRect(cx, cy, iw, 1, new Color(1, 1, 1, 0.10f)); cy += 3;

            // gear list — click a slot to focus it (its sockets show below, the picker on the right follows)
            for (int s = 0; s < SlotParts.Length; s++)
            {
                int key = (gearArr != null && s < gearArr.Length) ? gearArr[s] : 0;
                bool changed = origArr != null && s < origArr.Length && origArr[s] != key;
                bool sel = focused && _focus == s && _sockSlot < 0 && !_fitList;
                if (sel) DrawRect(cx, cy, iw, lh, new Color(0.85f, 0.70f, 0.30f, 0.18f));
                else if ((s & 1) == 1) DrawRect(cx, cy, iw, lh, new Color(1, 1, 1, 0.03f));
                GUI.Label(new Rect(cx + 4, cy, 34, lh), $"<color=#8a93a0>{SlotL(s)}</color>", _label);
                var stex = GearIconCache.Get(key);
                if (stex != null) GUI.DrawTexture(new Rect(cx + 36, cy + 1, lh - 3, lh - 3), stex, ScaleMode.ScaleToFit);
                var gt = GearDatabase.ByKey(key);
                string ghex = changed ? "7fffa0" : GradeHex(gt != null ? gt.Grade : "");
                GUI.Label(new Rect(cx + 38 + lh, cy, iw - 40 - lh, lh), $"<color=#{ghex}>{Nm(key)}</color>", _label);
                _focusRects.Add(new Rect(cx, cy, iw, lh)); _colHero.Add(hero); _colSlot.Add(s);
                cy += lh;
                cy = DrawSlotSockets(cx + 12, cy, cx + iw - 2, lh, hero, s);   // sockets flow horizontally (mind-map style), wrap
            }
            return cy;
        }

        // one column stat at a fixed (x,y) in width w — label + new value, coloured green/red vs the original
        private void ColStatAt(float x, float y, float w, float lh, string label, double o, double n, string fmt, string suffix)
        {
            string c = n > o * 1.001 ? "#7fffa0" : (n < o * 0.999 ? "#ff8a8a" : "#cdd5df");
            GUI.Label(new Rect(x + 4, y, w * 0.52f, lh), $"<color=#8a93a0>{label}</color>", _label);
            GUI.Label(new Rect(x + 4 + w * 0.52f, y, w * 0.46f - 6, lh), $"<color={c}>{n.ToString(fmt, System.Globalization.CultureInfo.InvariantCulture)}{suffix}</color>", _label);
        }

        // one gear slot's sockets, drawn as horizontal CHIPS that flow left→right under the gear row and wrap
        // when they hit rightX (mind-map style — keeps the column from getting tall). Each chip is clickable
        // (→ focus that hero+slot + open the material picker). Type colour: deco teal / engrave gold / inscribe
        // violet. Returns the bottom y (= topY if the slot has no sockets, so the gear row alone advances cy).
        private float DrawSlotSockets(float startX, float topY, float rightX, float lh, int hero, int slot)
        {
            int[] cc = SlotSockets(hero, slot);
            if (cc[0] + cc[1] + cc[2] == 0) return topY;
            _load.TryGetValue(hero, out var gearArr);
            _orig.TryGetValue(hero, out var origArr);
            _sockets.TryGetValue(hero, out var hsock);
            string gg = SlotGroup(hero, slot);
            bool unchanged = origArr != null && gearArr != null && slot < origArr.Length && slot < gearArr.Length && origArr[slot] == gearArr[slot];
            var real = RealSockets.Get(hero, slot);
            int[] edited = (hsock != null && hsock.TryGetValue(slot, out var ea)) ? ea : null;
            string[] gl = { "#7fd0c2", "#ffd86b", "#c79bff" };
            float chx = startX, chy = topY, gap = 3;
            int pos = 0;
            for (int ti = 0; ti < 3; ti++)
                for (int j = 0; j < cc[ti]; j++)
                {
                    var eff = FitCalc.EffectiveCell(real, edited, pos, gg, unchanged);
                    bool filled = !string.IsNullOrEmpty(eff.Stat);
                    string body = filled ? $"{StatL(eff.Stat)}{StatVal(eff.Stat, eff.Mod, eff.Value)}" : Loc.G("sock_empty");
                    float cw = Mathf.Min(rightX - startX, _label.CalcSize(new GUIContent(body)).x + 18);
                    if (chx + cw > rightX && chx > startX) { chx = startX; chy += lh; }   // wrap
                    var cell = new Rect(chx, chy, cw, lh - 1);
                    DrawRect(chx, chy, cw, lh - 1, filled ? new Color(0.25f, 0.42f, 0.40f, 0.20f) : new Color(1, 1, 1, 0.03f));
                    GUI.Label(new Rect(chx + 3, chy, cw - 4, lh), $"<size=11><color={gl[ti]}>◆</color> <color={(filled ? "#bcd0ea" : "#5d6470")}>{body}</color></size>", _label);
                    _sockRects.Add(cell); _sockPosList.Add(pos); _sockHeroList.Add(hero); _sockSlotList.Add(slot);
                    chx += cw + gap; pos++;
                }
            return chy + lh;
        }

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
            // --- stat-filter chips: narrow to materials granting a chosen stat (生命/範圍/移速/暴擊…) ---
            var statSet = new HashSet<string>();
            foreach (var mm in list) statSet.Add(mm.Effect(gearGroup).Stat);
            DrawStatChips(ix, ref cy, iw, lh, statSet);
            if (_pickGrade != "")
            {
                var f = new List<SockMat>();
                foreach (var mm in list) if ("T" + mm.TierFor(gearGroup) == _pickGrade) f.Add(mm);
                list = f;
            }
            if (_pickStat != "")
            {
                var f = new List<SockMat>();
                foreach (var mm in list) if (mm.Effect(gearGroup).Stat == _pickStat) f.Add(mm);
                list = f;
            }
            float rowH = lh * 1.95f, iconSz = lh * 1.55f;
            float botY = _rect.y + _rect.height - Pad - lh;
            _pickFirst = Mathf.Clamp(_pickFirst, 0, Mathf.Max(0, list.Count - 1));
            _pickRects.Clear(); _pickKeys.Clear();
            // the "empty / remove" option leads the list (only when scrolled to the top)
            if (_pickFirst == 0)
            {
                var er = new Rect(ix, cy, iw, lh - 1); DrawRect(ix, cy, iw, lh, new Color(1, 1, 1, 0.03f));
                GUI.Label(new Rect(ix + 6, cy, iw - 8, lh), $"<color=#67707d>✕ {Loc.G("sock_empty")}</color>", _label);
                _pickRects.Add(er); _pickKeys.Add(0); cy += lh;
            }
            int i = _pickFirst;
            for (; i < list.Count; i++)
            {
                if (cy + rowH > botY && i > _pickFirst) break;
                var mm = list[i]; var e = mm.Effect(gearGroup);
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
            DrawScrollFooter(ix, _rect.y + _rect.height - Pad - lh, iw, lh, _pickFirst, i, list.Count);
        }
    }
}
