using System;
using BepInEx;
using BepInEx.Configuration;
using BepInEx.Logging;
using BepInEx.Unity.IL2CPP;
using HarmonyLib;
using Il2CppInterop.Runtime.Injection;
using UnityEngine;

namespace TbhDpsMeter
{
    [BepInPlugin(Guid, Name, Version)]
    public class Plugin : BasePlugin
    {
        public const string Guid = "tbh.dpsmeter";
        public const string Name = "TBH DPS Meter";
        public const string Version = "0.9.4";

        public static DpsTracker Tracker;
        public static DamageTakenTracker TakenTracker;
        public static ManualLogSource Logger;

        /// <summary>Current wave index, written by the DPS overlay's stage poll, read by the taken panel.</summary>
        public static int CurrentWave;

        // config
        public static ConfigEntry<float> PosX;
        public static ConfigEntry<float> PosY;
        public static ConfigEntry<float> PanelWidth;
        public static ConfigEntry<float> Opacity;
        public static ConfigEntry<bool> StartVisible;
        public static ConfigEntry<int> FontSize;        // primary/large text (titles, main numbers, list rows)
        public static ConfigEntry<int> FontSizeSmall;   // secondary/detail text (dim hints, axis labels, buttons)
        public static ConfigEntry<string> FontFamily;   // optional IMGUI font family fallback (Auto = resolver list)
        public static ConfigEntry<string> FontPath;     // optional IMGUI font file path (.ttf/.otf/.ttc)
        public static ConfigEntry<float> UIScale;
        public static ConfigEntry<bool> HideOnGameMenu;
        public static ConfigEntry<bool> BorderOn;        // panel border, toggled from the F1 control center
        public static ConfigEntry<string> BorderColor;   // palette id, see PanelBorder.Palette
        public static ConfigEntry<float> WindowSeconds;
        public static ConfigEntry<bool> DebugDamage;
        public static ConfigEntry<bool> AutoCheckUpdate;
        private static ConfigEntry<string> _toggleKeyName;

        // damage-taken panel config
        public static ConfigEntry<float> TakenPosX;
        public static ConfigEntry<float> TakenPosY;
        public static ConfigEntry<float> TakenPanelWidth;
        public static ConfigEntry<bool> TakenStartVisible;
        private static ConfigEntry<string> _takenToggleKeyName;

        // stage-compare panel config
        public static ConfigEntry<float> ComparePosX;
        public static ConfigEntry<float> ComparePosY;
        public static ConfigEntry<float> ComparePanelWidth;
        public static ConfigEntry<bool> CompareStartVisible;
        private static ConfigEntry<string> _compareToggleKeyName;
        public static ConfigEntry<bool> DebugSnapshot;

        // farming-planner panel config
        public static ConfigEntry<float> FarmPosX;
        public static ConfigEntry<float> FarmPosY;
        public static ConfigEntry<float> FarmPanelWidth;
        public static ConfigEntry<bool> FarmStartVisible;
        private static ConfigEntry<string> _farmToggleKeyName;

        // box-log panel config
        public static ConfigEntry<float> BoxPosX;
        public static ConfigEntry<float> BoxPosY;
        public static ConfigEntry<float> BoxPanelWidth;
        public static ConfigEntry<float> BoxPanelHeight;
        public static ConfigEntry<bool> BoxStartVisible;
        private static ConfigEntry<string> _boxToggleKeyName;
        public static ConfigEntry<bool> BoxSoundEnabled;
        public static ConfigEntry<float> BoxSoundVolume;
        public static ConfigEntry<string> BoxSoundFile;

        // control-center (hub) panel config
        public static ConfigEntry<float> HubPosX;
        public static ConfigEntry<float> HubPosY;
        public static ConfigEntry<float> HubPanelWidth;
        public static ConfigEntry<bool> HubStartVisible;
        private static ConfigEntry<string> _hubToggleKeyName;

        // box-open (F4) panel config
        public static ConfigEntry<float> BoxOpenPosX;
        public static ConfigEntry<float> BoxOpenPosY;
        public static ConfigEntry<float> BoxOpenPanelWidth;
        public static ConfigEntry<float> BoxOpenPanelHeight;
        public static ConfigEntry<bool> BoxOpenStartVisible;
        private static ConfigEntry<string> _boxOpenToggleKeyName;
        public static KeyCode BoxOpenToggleKey = KeyCode.F4;
        public static readonly DateTime SessionStart = DateTime.Now;

        // loot-heatmap (F3) panel config
        public static ConfigEntry<float> LootMapPosX;
        public static ConfigEntry<float> LootMapPosY;
        public static ConfigEntry<float> LootMapPanelWidth;
        public static ConfigEntry<bool> LootMapStartVisible;
        private static ConfigEntry<string> _lootMapToggleKeyName;
        public static KeyCode LootMapToggleKey = KeyCode.F3;

        // Steam price-peek (shows the hovered item's Steam market price). Toggled from the F1 control center.
        public static ConfigEntry<bool> PricePeekEnabled;
        public static ConfigEntry<float> PricePosX;   // price-box position (auto-saved when dragged); -1 = auto (under tooltip)
        public static ConfigEntry<float> PricePosY;
        private static ConfigEntry<string> _priceAdjustKeyName;
        public static KeyCode PriceAdjustKey = KeyCode.F4;   // enters drag/position-adjust mode

        public static KeyCode ToggleKey = KeyCode.F9;
        public static KeyCode TakenToggleKey = KeyCode.F10;
        public static KeyCode CompareToggleKey = KeyCode.F11;
        public static KeyCode FarmToggleKey = KeyCode.F6;
        public static KeyCode BoxToggleKey = KeyCode.F5;
        public static KeyCode HubToggleKey = KeyCode.F1;

        private static int _dbgCount;

        public override void Load()
        {
            Logger = Log;

            PosX = Config.Bind("UI", "PosX", -1f, "Overlay X (auto-saved when dragged). -1 = auto bottom-left.");
            PosY = Config.Bind("UI", "PosY", -1f, "Overlay Y (auto-saved when dragged). -1 = auto bottom-left.");
            PanelWidth = Config.Bind("UI", "PanelWidth", 300f, "Overlay panel width in pixels.");
            Opacity = Config.Bind("UI", "Opacity", 0.35f, "Background opacity 0..1 (PageUp/PageDown to adjust live).");
            StartVisible = Config.Bind("UI", "StartVisible", true, "Show the overlay on launch.");
            FontSize = Config.Bind("UI", "FontSize", 15, "Primary/large font size: titles, main numbers, list rows. Adjust live from the F1 control center.");
            FontSizeSmall = Config.Bind("UI", "FontSizeSmall", 13, "Secondary/detail font size: dim hints, chart axis labels, buttons. Adjust live from the F1 control center.");
            FontFamily = Config.Bind("UI", "FontFamily", "Auto", "Overlay font family. Auto tries CJK-capable fonts first; set to a specific installed family if needed.");
            FontPath = Config.Bind("UI", "FontPath", "", "Optional overlay font file path (.ttf/.otf/.ttc). Relative paths are resolved under BepInEx/plugins and BepInEx/plugins/fonts.");
            OverlayFonts.Init(FontFamily.Value, FontPath.Value, Logger);
            UIScale = Config.Bind("UI", "UIScale", 1.0f, "Global overlay scale (0.6–1.5). Panels auto-shrink further if they'd exceed the screen. Adjust live with Ctrl+PageUp/PageDown or the F1 control center's −/+ control.");
            HideOnGameMenu = Config.Bind("UI", "HideOnGameMenu", true, "Hide all overlays while a game menu (TAB) is open. Toggle live from the F1 control center.");
            BorderOn = Config.Bind("UI", "PanelBorder", false, "Draw a border around every overlay panel. Toggle live from the F1 control center.");
            BorderColor = Config.Bind("UI", "PanelBorderColor", "white", "Border color: white, gold, green, cyan, blue, magenta or red. Pick live from the F1 control center.");
            WindowSeconds = Config.Bind("Meter", "LiveWindowSeconds", 5f, "Sliding window length for the live DPS number.");
            _toggleKeyName = Config.Bind("General", "ToggleKey", "F9", "Key to show/hide the DPS overlay (UnityEngine.KeyCode name).");
            DebugDamage = Config.Bind("Debug", "LogDamageSamples", false, "Log the first damage hits to verify the hook is correct.");
            AutoCheckUpdate = Config.Bind("General", "AutoCheckUpdate", true, "On launch, check GitHub for a newer release and show a download prompt in the panel.");

            var lang = Config.Bind("General", "Language", "Auto",
                "UI language: Auto / zh-Hant / zh-Hans / en / ja / es.");
            Loc.Init(lang.Value);

            TakenPosX = Config.Bind("TakenUI", "PosX", -1f, "Damage-taken overlay X (auto-saved when dragged). -1 = auto.");
            TakenPosY = Config.Bind("TakenUI", "PosY", -1f, "Damage-taken overlay Y (auto-saved when dragged). -1 = auto.");
            TakenPanelWidth = Config.Bind("TakenUI", "PanelWidth", 300f, "Damage-taken overlay panel width in pixels.");
            TakenStartVisible = Config.Bind("TakenUI", "StartVisible", true, "Show the damage-taken overlay on launch.");
            _takenToggleKeyName = Config.Bind("TakenUI", "ToggleKey", "F10", "Key to show/hide the damage-taken overlay (UnityEngine.KeyCode name).");

            ComparePosX = Config.Bind("CompareUI", "PosX", -1f, "Stage-compare overlay X (auto-saved when dragged). -1 = auto.");
            ComparePosY = Config.Bind("CompareUI", "PosY", -1f, "Stage-compare overlay Y (auto-saved when dragged). -1 = auto.");
            ComparePanelWidth = Config.Bind("CompareUI", "PanelWidth", 380f, "Stage-compare overlay panel width in pixels.");
            CompareStartVisible = Config.Bind("CompareUI", "StartVisible", false, "Show the stage-compare overlay on launch.");
            _compareToggleKeyName = Config.Bind("CompareUI", "ToggleKey", "F11", "Key to show/hide the stage-compare overlay (UnityEngine.KeyCode name).");
            DebugSnapshot = Config.Bind("Debug", "LogSnapshot", false, "Log character-snapshot reflection diagnostics to verify obfuscated member picks.");

            FarmPosX = Config.Bind("FarmUI", "PosX", -1f, "Farming-planner overlay X (auto-saved when dragged). -1 = auto.");
            FarmPosY = Config.Bind("FarmUI", "PosY", -1f, "Farming-planner overlay Y (auto-saved when dragged). -1 = auto.");
            FarmPanelWidth = Config.Bind("FarmUI", "PanelWidth", 520f, "Farming-planner overlay panel width in pixels.");
            FarmStartVisible = Config.Bind("FarmUI", "StartVisible", false, "Show the farming-planner overlay on launch.");
            _farmToggleKeyName = Config.Bind("FarmUI", "ToggleKey", "F6", "Key to show/hide the farming-planner overlay (UnityEngine.KeyCode name).");

            BoxPosX = Config.Bind("BoxUI", "PosX", -1f, "Box-log overlay X (auto-saved when dragged). -1 = auto.");
            BoxPosY = Config.Bind("BoxUI", "PosY", -1f, "Box-log overlay Y (auto-saved when dragged). -1 = auto.");
            BoxPanelWidth = Config.Bind("BoxUI", "PanelWidth", 420f, "Box-log overlay panel width in pixels.");
            BoxPanelHeight = Config.Bind("BoxUI", "PanelHeight", 180f, "Box-log scrollable list height in pixels (drag the bottom-right grip).");
            BoxStartVisible = Config.Bind("BoxUI", "StartVisible", false, "Show the box-log overlay on launch.");
            _boxToggleKeyName = Config.Bind("BoxUI", "ToggleKey", "F5", "Key to show/hide the box-log overlay (UnityEngine.KeyCode name).");
            BoxSoundEnabled = Config.Bind("BoxUI", "SoundEnabled", true, "Play a sound when a box is picked up.");
            BoxSoundVolume = Config.Bind("BoxUI", "SoundVolume", 0.6f, "Box pickup sound volume (0..1). Adjustable live in the F5 panel.");
            BoxSoundFile = Config.Bind("BoxUI", "SoundFile", "", "Optional path to a custom .wav to play on box pickup. Blank = built-in beep.");

            HubPosX = Config.Bind("HubUI", "PosX", -1f, "Control-center overlay X (auto-saved when dragged). -1 = auto top-left.");
            HubPosY = Config.Bind("HubUI", "PosY", -1f, "Control-center overlay Y (auto-saved when dragged). -1 = auto top-left.");
            HubPanelWidth = Config.Bind("HubUI", "PanelWidth", 260f, "Control-center overlay panel width in pixels.");
            HubStartVisible = Config.Bind("HubUI", "StartVisible", true, "Show the control-center overlay on launch.");
            _hubToggleKeyName = Config.Bind("HubUI", "ToggleKey", "F1", "Key to show/hide the control-center overlay (UnityEngine.KeyCode name).");

            BoxOpenPosX = Config.Bind("BoxOpenUI", "PosX", -1f, "Open-box stats overlay X. -1 = auto.");
            BoxOpenPosY = Config.Bind("BoxOpenUI", "PosY", -1f, "Open-box stats overlay Y. -1 = auto.");
            BoxOpenPanelWidth = Config.Bind("BoxOpenUI", "PanelWidth", 460f, "Open-box stats overlay width in pixels.");
            BoxOpenPanelHeight = Config.Bind("BoxOpenUI", "PanelHeight", 180f, "Open-box log scrollable list height in pixels (drag the bottom-right grip).");
            BoxOpenStartVisible = Config.Bind("BoxOpenUI", "StartVisible", false, "Show the open-box stats overlay on launch.");
            _boxOpenToggleKeyName = Config.Bind("BoxOpenUI", "ToggleKey", "F4", "Key to show/hide the open-box stats overlay (UnityEngine.KeyCode name).");

            LootMapPosX = Config.Bind("LootMapUI", "PosX", -1f, "Loot-heatmap overlay X. -1 = auto.");
            LootMapPosY = Config.Bind("LootMapUI", "PosY", -1f, "Loot-heatmap overlay Y. -1 = auto.");
            LootMapPanelWidth = Config.Bind("LootMapUI", "PanelWidth", 560f, "Loot-heatmap overlay width in pixels.");
            LootMapStartVisible = Config.Bind("LootMapUI", "StartVisible", false, "Show the loot-heatmap overlay on launch.");
            PricePeekEnabled = Config.Bind("Price", "Enabled", true, "Show the hovered item's Steam market price next to the in-game tooltip. Toggle live from the F1 control center.");
            PricePosX = Config.Bind("Price", "PosX", -1f, "Price-box X (auto-saved when dragged in adjust mode). -1 = auto, under the item tooltip.");
            PricePosY = Config.Bind("Price", "PosY", -1f, "Price-box Y (auto-saved when dragged in adjust mode). -1 = auto, under the item tooltip.");
            _priceAdjustKeyName = Config.Bind("Price", "AdjustKey", "F4", "Key to enter the price-box position-adjust (drag) mode (UnityEngine.KeyCode name).");
            _lootMapToggleKeyName = Config.Bind("LootMapUI", "ToggleKey", "F3", "Key to show/hide the loot-heatmap overlay (UnityEngine.KeyCode name).");

            if (!Enum.TryParse(_toggleKeyName.Value, true, out ToggleKey))
                ToggleKey = KeyCode.F9;
            if (!Enum.TryParse(_takenToggleKeyName.Value, true, out TakenToggleKey))
                TakenToggleKey = KeyCode.F10;
            if (!Enum.TryParse(_compareToggleKeyName.Value, true, out CompareToggleKey))
                CompareToggleKey = KeyCode.F11;
            if (!Enum.TryParse(_farmToggleKeyName.Value, true, out FarmToggleKey))
                FarmToggleKey = KeyCode.F6;
            if (!Enum.TryParse(_boxToggleKeyName.Value, true, out BoxToggleKey))
                BoxToggleKey = KeyCode.F5;
            if (!Enum.TryParse(_hubToggleKeyName.Value, true, out HubToggleKey))
                HubToggleKey = KeyCode.F1;
            if (!Enum.TryParse(_boxOpenToggleKeyName.Value, true, out BoxOpenToggleKey))
                BoxOpenToggleKey = KeyCode.F4;
            if (!Enum.TryParse(_lootMapToggleKeyName.Value, true, out LootMapToggleKey))
                LootMapToggleKey = KeyCode.F3;
            if (!Enum.TryParse(_priceAdjustKeyName.Value, true, out PriceAdjustKey))
                PriceAdjustKey = KeyCode.F4;
            // F4 belongs to the price-box adjust now — if the open-box panel still owns it, bump it to F7
            // and persist, so pressing F4 doesn't toggle both.
            if (BoxOpenToggleKey == PriceAdjustKey)
            {
                BoxOpenToggleKey = KeyCode.F7;
                _boxOpenToggleKeyName.Value = "F7";
            }

            Tracker = new DpsTracker(WindowSeconds.Value);
            TakenTracker = new DamageTakenTracker(WindowSeconds.Value);

            var harmony = new Harmony(Guid);
            TryPatch(harmony, typeof(Monster_TakeDamage_Patch), "Monster.ebj damage hook");
            TryPatch(harmony, typeof(Hero_TakeDamage_Patch), "Hero.gnr damage-taken hook");
            TryPatch(harmony, typeof(StageState_Patch), "StageManager.set_stageState wave hook");
            StageProbe.TryHook(harmony);

            BoxStore.Dir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "dpsmeter_boxlog");
            BoxOpenStore.Dir = System.IO.Path.Combine(BepInEx.Paths.ConfigPath, "dpsmeter_boxopen");
            try { foreach (var e in BoxStore.LoadAll(500)) BoxTracker.Events.Add(e); } catch { }
            try { BoxOpenStore.Load(BoxOpenTracker.Stats); } catch { }
            BoxOpenTracker.Install(harmony);

            try
            {
                ClassInjector.RegisterTypeInIl2Cpp<OverlayBehaviour>();
                ClassInjector.RegisterTypeInIl2Cpp<TakenOverlayBehaviour>();
                ClassInjector.RegisterTypeInIl2Cpp<CompareOverlayBehaviour>();
                ClassInjector.RegisterTypeInIl2Cpp<FarmOverlayBehaviour>();
                ClassInjector.RegisterTypeInIl2Cpp<BoxOverlayBehaviour>();
                ClassInjector.RegisterTypeInIl2Cpp<HubOverlayBehaviour>();
                ClassInjector.RegisterTypeInIl2Cpp<BoxOpenOverlayBehaviour>();
                ClassInjector.RegisterTypeInIl2Cpp<LootMapOverlayBehaviour>();
                ClassInjector.RegisterTypeInIl2Cpp<PricePeekBehaviour>();
                var go = new GameObject("TbhDpsOverlay");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.hideFlags = HideFlags.HideAndDontSave;
                go.AddComponent<OverlayBehaviour>();
                go.AddComponent<TakenOverlayBehaviour>();
                go.AddComponent<CompareOverlayBehaviour>();
                go.AddComponent<FarmOverlayBehaviour>();
                go.AddComponent<BoxOverlayBehaviour>();
                go.AddComponent<HubOverlayBehaviour>();
                go.AddComponent<BoxOpenOverlayBehaviour>();
                go.AddComponent<LootMapOverlayBehaviour>();
                go.AddComponent<PricePeekBehaviour>();
                Logger.LogInfo("Overlays created. hub " + HubToggleKey + ", DPS " + ToggleKey + ", taken " + TakenToggleKey + ", compare " + CompareToggleKey + ", farm " + FarmToggleKey + ", box " + BoxToggleKey + ", boxopen " + BoxOpenToggleKey + ", lootmap " + LootMapToggleKey + ".");
            }
            catch (Exception ex)
            {
                Logger.LogError("Failed to create overlay: " + ex);
            }


            SelfCheck.Run();
            if (AutoCheckUpdate.Value) Updater.CheckAsync();
            Logger.LogInfo($"{Name} {Version} loaded.");
        }

        private static void TryPatch(Harmony harmony, Type patchClass, string label)
        {
            try { harmony.PatchAll(patchClass); Logger.LogInfo("Patched: " + label); }
            catch (Exception ex) { Logger.LogError("Patch FAILED (" + label + "): " + ex.Message); }
        }

        public static void LogDamageSample(float amount, bool crit, int type)
        {
            if (_dbgCount >= 25) return;
            _dbgCount++;
            Logger.LogInfo($"[dmg #{_dbgCount}] amount={amount:0.##} crit={crit} type={type}");
        }
    }
}
