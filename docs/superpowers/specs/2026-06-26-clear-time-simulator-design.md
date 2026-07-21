# 通關提速模擬器 — 設計 spec

- 日期：2026-06-26
- 分支：`feat/clear-time-sim`
- 前置分析：[2026-06-26-clear-time-what-if-feasibility.md](2026-06-26-clear-time-what-if-feasibility.md)

## 目標

一個新的中控台面板：玩家為每個職業設定 DPS 提升倍率、為全隊設定速度倍率，
即時看到全 108 關的通關時間如何變化（原通關 / 模擬通關 / 省秒 / 省%）。
回答「我把裝備資源投到哪個職業、堆攻擊還是堆速度，對每一關各能省多少時間」。

**不需要重打關卡**：沿用 FarmPlanner 既有的兩段時間模型外推全圖；職業占比用目前隊伍即時 `ByChar`。

## 使用者已拍板的決策

1. **逐職業**調整 DPS（不是按攻擊種類）。
2. 職業占比用**目前隊伍即時占比**，不重打。
3. 放**中控台**（F1 Hub），**不綁快捷鍵**（`KeyCode.None`）。

## 引擎（純計算，無 Unity 依賴）

抽成 `DpsMeter/ClearTimeSim.cs`，可在 `TrackerTests` 單元測試。

輸入：
- `shares`：職業占比 `Dictionary<int heroKey, double share>`（來自 `Snapshot.ByChar`，share = 占全場傷害比例）。
- `mult`：職業倍率 `Dictionary<int heroKey, double>`（預設 1.0；缺項視為 1.0）。
- `speedMult`：全隊速度倍率 `double`（預設 1.0）。
- 每關的 `ClearSec`、`SecPerHP`、`PerWaveSec`、`TotalHP`、`Waves`、`HasTimeModel`（來自 `EfficiencyRow` + `Calibration`）。

計算：
```
F = Σ_h ( share_h × mult_h )        // = 新全隊DPS / 舊全隊DPS;占0%的職業不影響
若 F <= 0 → F = 1                    // 防呆

每關:
  若 HasTimeModel 且 ClearSec > 0:
     hp   = SecPerHP × TotalHP
     wave = PerWaveSec × Waves
     dpsFrac = hp / (hp + wave)          // 0..1;hp+wave<=0 → dpsFrac = 1
  否則:
     dpsFrac = 1                          // 無模型 → 整段當輸出段(保守)
  newClear = ClearSec × ( dpsFrac / F + (1 − dpsFrac) / speedMult )
  savedSec = ClearSec − newClear
  savedPct = ClearSec > 0 ? savedSec / ClearSec : 0
```

輸出：每關一個 `SimRow { EfficiencyRow Row; double NewClear; double SavedSec; double SavedPct; double DpsFrac; }`
+ 聚合摘要 `SimSummary { double PartyDpsFactor; double AvgSavedPct; SimRow Best; }`（只統計 `ClearSec > 0` 的關）。

`ClearTimeSim` 不依賴 `Calibration` 型別本身，改吃單純的數值參數，方便測試。
面板負責把 `EfficiencyRow` + `Calibration` 餵進來。

## 面板 `SimOverlayBehaviour`（IMGUI overlay）

結構照抄 `FarmOverlayBehaviour`（同樣的樣式建構、拖曳、縮放、難度 chip、分頁、resize grip）。

**註冊**（`Awake`）：
```
PanelRegistry.Register("sim", 4, "<BMP icon>", () => Loc.G("sim_title"), KeyCode.None,
    () => _visible, v => _visible = v);
```
- order 4：排在 farm(3) 後面。
- icon：單一 BMP 字符（emoji 不會在 IMGUI 顯示）。候選 `⏱` / `Σ` / `↯`，最終以實機顯示為準。

**資料載入**（`Reload`，與 RunStore.Version 連動，與 Farm 面板相同）：
- `FarmPlanner.Rank(FarmDataStore.Stages, RunStore.LoadAll(), out _calib, curLevel)` 取得 rows + calib。
- 從 `Plugin.Tracker.GetSnapshot(Time.time).ByChar` 讀目前隊伍占比；**快取最後一次非空** 的 `(heroKey→share)`（回城/停戰仍保有）。每幀更新快取，不在每幀重算 Rank。

**輸入區（頂部）**
- 每個有占比的職業一行：色點（`key/100` 配色，重用 F9 詳細的配色邏輯）＋ `HeroProbe.HeroName(key)` ＋ 占比% ＋ `[−][+]` 倍率（顯示 ×1.30，步進 +0.10，下限 1.00，上限例如 5.00）。
- 全隊速度行：`[−][+]` ×speed。
- `[重置]`：所有倍率與速度歸 1.00。
- 摘要行：`全隊DPS ×F → 平均省 X%,最受惠 <關> 省 Y%`。

**輸出表**（沿用難度 chip + 分頁 + 排序）
欄位：`關卡 ｜ 原通關 ｜ 模擬通關 ｜ 省秒 ｜ 省%`，省% 後接一條比例 bar。
排序鍵新增 `SavedSec` / `SavedPct`（沿用 `ClearSec` 升冪當預設之一）。未知通關(ClearSec=0)排最後、模擬欄顯示「—」。

**邊界提示（UI 文案）**
- `!_calib.HasTimeModel` → 頂部黃字「先清 2 種不同關卡以建立時間模型；目前把整段視為輸出段」。
- 快取占比為空（本 session 未戰鬥）→ 「先打任意一關讀取職業占比」，職業區顯示空狀態、倍率區停用。
- 固定註腳：「輸出半邊準；速度半邊為估計（含生怪等待等固定開銷）」。

## 互動細節

- 倍率/速度的 `[−][+]`、難度 chip、排序、分頁、重置全部走既有的 `HandlePointer` click-rect 模式（不可用 IMGUI 原生 slider，與其他面板一致）。
- 調整倍率**只重算模擬**（純算很便宜），不重跑 `FarmPlanner.Rank`。Rank 只在 RunStore.Version 改變或面板開啟時跑。

## IL2CPP 注意（[[il2cpp-injected-method-signatures]]）

`SimOverlayBehaviour` 是注入的 MonoBehaviour：**禁止** local function、optional/default 參數、struct 參數出現在會被 IL2CPP 掃描的方法簽名上。完全比照現有 overlay 寫法。實作後必須在 log 看到面板正常出現、F1 中控台多一個圖示。

## 測試（`TrackerTests/Program.cs`）

純算 `ClearTimeSim`：
1. `F = Σ share×mult`：兩職業 60/40，法師 ×1.5 → F=1.30。
2. benched(share 0) 職業給高倍率 → F 不變。
3. `dpsFrac`：給定 SecPerHP/PerWaveSec/HP/Waves 算出比例；hp 全部時 dpsFrac=1。
4. `newClear`：DPS×2、純輸出關 → 通關砍半;速度×2、純跑路關 → 通關砍半。
5. 無時間模型 → dpsFrac=1，速度倍率不影響。
6. 摘要 Best/AvgSavedPct 只計 ClearSec>0。

## 變更檔案

- 新增 `DpsMeter/ClearTimeSim.cs`（純算）
- 新增 `DpsMeter/SimOverlayBehaviour.cs`（面板）
- 改 `DpsMeter/Plugin.cs`（RegisterTypeInIl2Cpp + AddComponent）
- 改 `DpsMeter/Localization.cs`（`sim_title` 等字串，至少 zh-Hant/zh-Hans/en）
- 改 `DpsMeter/FarmPlanner.cs`：可能把 `FarmSortKey` 擴充或在面板自排（傾向面板自排，不污染 FarmPlanner）
- 改 `TrackerTests/Program.cs`（單元測試）
- 可能新增 `Plugin.Sim*` config（位置/寬度/起始可見），比照 `Farm*`

## v2 修訂（2026-06-26,使用者實機回饋後）

實機看到「每關都省 52.5%」→ 使用者指出兩個問題,核對後屬實:
1. 那是**單關校準**的退化結果(只刷過 1-4 → 無時間模型 → 整段當輸出 → 均一 `1−1/F`)。
2. 更根本:線性「加傷害=等比例減時間」物理錯誤(溢傷 + 施法僵硬有上限),且回歸外推不如 F11 的**真實量測**。

**模型改為直接吃 `RunRecord` 的真實 `ActiveSeconds`(有效輸出)/`IdleSeconds`(停輸出):**
- `ClearTimeSim.AggregateTiming(runs, stageId, buildSig)` → 該關(當前build)的中位數 active/idle。
- 已清關卡:`SimulateSplit(active, idle, F, speed)` = `active/F + idle/speed`(per-stage 差異化,速度真正有意義)。
- 未清關卡:`SimulateFallback(clearSec, avgActiveFrac, F, speed)`,`avgActiveFrac` = 所有實測關的全域有效輸出占比;標「估」。
- **兩個 lever 都是最佳上限**(UI `sim_note` 明講):DPS 受溢傷/施法僵硬限制,速度受等怪生成限制。
- **零新擷取**:active/idle/DPS/build/stage 全部 `RunRecord` 已存,不用重打。

驗證:1-4 真實 active 90.5/idle 55.5、F=2.11 → 省 ~33%(非 52.5%),且速度開始有效(38% 時間是跑路)。單元測試涵蓋 split/fallback/aggregate/summary。

**下一層(待資料累積):** 從同關不同 build 的歷史 run 實測 `DPS→有效輸出` 曲線,自動吃進邊際遞減,取代 active 半邊的線性假設。使用者會用「跑不同關 / 換裝備」產生樣本。

## 非目標（YAGNI）

- 不做按攻擊種類(AOE/DOT…)的輸入軸。
- 不做裝備→DPS% 的自動預測（層級3）。
- 不把 per-char 占比寫進 run 檔（用即時占比）。
- 不建模護甲隨等級成長（沿用 FarmPlanner 既有線性外推限制）。
