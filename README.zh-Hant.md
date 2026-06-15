# TBH DPS Meter

[English](README.md) · [日本語](README.ja.md) · **繁體中文** · [简体中文](README.zh-Hans.md)

**TaskBarHero**（TBH: Task Bar Hero）的遊戲內 **DPS / 承受傷害 / 關卡比較 / 刷關效率 / 寶箱記錄 / 開箱統計 / 掉寶熱力圖 / Steam 市集查價** 監控外掛，
全部都能從 **F1 中控台** 一鍵開關。以 BepInEx 6 IL2CPP 外掛實作。測試版本 **v1.00.09**（Unity 6 / IL2CPP）。
介面自動偵測 **繁體中文 / 简体中文 / English / 日本語 / Español**。

> ⬇️ **一般玩家只要到 [Releases](../../releases/latest) 下載 zip 就能用，不用編譯。**

![遊戲內疊加面板 — DPS、關卡比較、刷關效率、承受傷害與寶箱記錄](image/overview.png)

<table>
<tr>
<td><img src="image/TaskBarHero_FkEGMBj3Kq.png" alt="DPS 面板"></td>
<td><img src="image/TaskBarHero_3TGLxaOOR2.png" alt="承受傷害面板"></td>
</tr>
<tr>
<td align="center"><b>DPS 面板</b>（你造成的傷害）</td>
<td align="center"><b>承受傷害面板</b>（你受到的傷害）</td>
</tr>
</table>

---

## 顯示內容

**DPS 面板:**
- 即時 DPS（5 秒滑動視窗）+ 峰值 + 平均
- 總傷害 + 戰鬥時長 + 波數
- 傷害類型分布（近戰 / 投射 / 範圍 / 召喚 / 持續 / 陷阱，含複合旗標）
- 暴擊率 + 暴傷佔比

**承受傷害面板:**
- 即時 DTPS（每秒承受）+ 峰值 + 平均
- 總承受 + 時長 + 最大單次受擊
- **受擊**（被打中的次數）+ **入站暴擊**（怪物對你的暴擊率）
- 兩條分布條：元素屬性（物理/火/冰/雷/混沌）與傷害類型

## 關卡比較（F11）
按 **F11** 開啟**關卡比較面板**：把你存下的紀錄依**關卡（含難度）**分組，拿目前這場跟**基準**（預設最快通關，或你手動釘選的一場）對照，
顯示時長、**有效輸出 vs 停輸出（跑路）時間**、平均/峰值/暴擊、屬性、傷害分配%、**每波時間**，
還有**全隊每個角色的完整配置**：已裝備的**裝備**（中文名稱＋詞綴）與**技能**（含等級），並標示與基準的差異。
上方有通關秒數趨勢折線（點某點看該場詳細）。用 ◀ ▶ 翻看場次、≪ ≫ 切換關卡、角色分頁切換英雄、釘選鈕設定基準。

關卡 / 角色 / 技能 / **物品**名稱都**跟隨遊戲語言**——遊戲切成 English / 日本語 / 繁體中文 / 简体中文 / Español，面板會**即時切換、不用重啟**。

<img src="image/TaskBarHero_5BRF6aiQF5.png" alt="關卡比較面板" width="420">

> *上方通關秒數趨勢折線（點某點看該場詳細）；下方「基準 ∣ 這場」對齊兩欄、綠/紅標出變化，並列出該角色的裝備與技能。*

## 刷關效率（F6）
按 **F6** 開啟「現在該刷哪一關」的**個人化**排名 —— **金幣/秒** 與 **經驗/秒** 雙欄並列，可排序，有難度篩選 chip。
跟靜態的 wiki 表不同，它是針對**你自己的 build** 校準的：
- 你打過的關用你的**真實實測**數字（綠色，標 `實測`）。
- 沒打過的用 wiki 基準 × **你的個人倍率**（從你的場次學到），清關時間用你的實測場次擬合
  （`時間 = 每HP秒×血量 + 每波秒×波數`），標 `估`。
- **「保留%」欄**反映遊戲內的等級降損（你的等級 vs 關卡等級），所以過高/過低等級的關會被誠實排序 —— 越高關不一定越好。
- **認得 build：** 每場都會用裝備＋詞綴＋技能＋等級算指紋，只有「跟你現在這套相同」的場次才算進校準。換裝會自動偵測並提示你重打一場校準。

最上面有一行**「基準」**摘要，顯示校準建立在什麼上（幾場 · 幾關 · 等級 · 目前裝備還是舊裝備）。

<img src="image/TaskBarHero_5vnzsSLfTW.png" alt="刷關效率面板" width="420">

> *每一關依你的真實金幣/秒、經驗/秒排名；綠色＝實測，灰色＝用你的個人倍率推估；**「保留」欄**是遊戲內的經驗保留（等級降損）。*

## 寶箱記錄（F5）
按 **F5** 開啟**寶箱記錄**：每個寶箱獲取都記下**時間 · 關卡 · 名稱**。**Stage Boss Box（王箱）**
以**藍字**顯示並**另外統計**，還有每關獲取數與每小時個數。每次獲取會發出**提示音** —— 點 **⚙ 設定**
可開關、調**音量**、**試聽**，或選你自己的 **.wav**（預設為內建雙音「叮咚」聲）。

<img src="image/box-panel.png" alt="寶箱記錄面板" width="420">

> *每個寶箱記下時間、關卡與名稱；王箱以藍字顯示並單獨計數。*

## 開箱統計（F4）
按 **F4** 開啟**開箱統計** —— 開箱後實際拿到什麼品質。一張**品階 × 箱種**矩陣（次數與百分比）
以歷來累計呈現各箱種的稀有度分布（讓你看出哪種箱值得開），旁邊還有一份分頁、依時間排序的
**開箱記錄**。

<img src="image/boxopen-panel.png" alt="開箱統計面板" width="420">

## 掉寶熱力圖（F3）
按 **F3** 開啟**掉寶熱力圖** —— 兩張上下排列的**天 × 24 小時**格子圖，揭露你的掉寶*什麼時候*發生：
上方＝**寶箱獲取**（取自 F5 記錄，藍色），下方＝**傳說以上開箱**（取自 F4，品階 ≥ 3，金綠色），
旁邊有一行摘要。最下方還有一張**通關秒數趨勢**圖，會跟著 F11 比較面板目前選的關卡，
讓你把*什麼時候在刷*跟*當時清關有多快*對照起來。

<img src="image/heatmap-panel.png" alt="掉寶熱力圖面板" width="460">

## Steam 市集查價
把滑鼠移到任何物品上 —— **背包**、獎勵彈窗、任何有 tooltip 的地方 —— 就會跳出一個小框顯示它的
**Steam 社群市集**價格：目前價格、**24h 波動**、**中位**成交價、**在售**數量、**24h 成交量**，
還有一張 **7 天價格曲線**。報價來自每約 30 分鐘更新一次的 cron 資料源，不需要每個玩家各自去爬 Steam。
**右鍵**物品可以把價格框**釘選**在它身上 —— 釘選後框會固定，你可以把滑鼠移到曲線上，
滑過任何一點就會顯示那一點的**時間 · 價格 · 對現在的漲跌**。按 **F4** 進入位置調整模式即可拖動價格框。
可從 F1 中控台開關。

<table>
<tr>
<td><img src="image/背包顯示價格.png" alt="背包物品的價格框" width="300"></td>
<td><img src="image/價格曲線圖.png" alt="互動式 7 天價格曲線" width="260"></td>
</tr>
<tr>
<td align="center"><b>滑過任何物品</b>看 Steam 市集價</td>
<td align="center"><b>釘選後滑過曲線</b>讀每一天的價格</td>
</tr>
</table>

## 中控台（F1）
按 **F1** 開啟**中控台** —— 一個精簡的樞紐，把**每一個**面板都列成切換按鈕
（亮＝顯示中，暗＝隱藏），讓你可以從同一個地方開關 DPS、承傷、關卡比較、刷關效率、寶箱記錄、
開箱統計、掉寶熱力圖，不用記每一個快捷鍵。上方有一個小小的即時摘要
（**DPS · 連線時間 · 開箱數**）。預設啟動時顯示；新面板會自動註冊上去。
下方幾列是全域設定：**UI 縮放**、**選單隱藏**，以及兩組獨立的**字體大小**調整鈕 ——
**大字**控制標題、主要數字、清單內文，**小字**控制灰字說明、軸標、按鈕 —— 即時套用到每個面板。

<img src="image/中控台2.png" alt="含 UI 縮放與大字/小字字體控制的中控台" width="300">

## 介面縮放
小螢幕或低解析度時，面板會**自動縮小**避免超出畫面。也可以用 F1 中控台的 **− UI % +** 控制，
或 **Ctrl + PageUp / PageDown** 自訂大小 —— 套用到所有面板，存進 `UI.UIScale`。
旁邊還有兩組獨立的**字體大小**調整鈕（**大字** / **小字**）控制文字本身，存進 `UI.FontSize` 與 `UI.FontSizeSmall`。

<img src="image/dps-uiscale.png" alt="DPS 面板的 UI 縮放控制" width="300">

## 操作
- **F1** — 顯示/隱藏 中控台（可設定：`HubUI.ToggleKey`）
- **F9** — 顯示/隱藏 DPS 面板（可設定：`ToggleKey`）
- **F10** — 顯示/隱藏 承受傷害面板（可設定：`TakenUI.ToggleKey`）
- **F11** — 顯示/隱藏 關卡比較面板（可設定：`CompareUI.ToggleKey`）
- **F6** — 顯示/隱藏 刷關效率面板（可設定：`FarmUI.ToggleKey`）
- **F5** — 顯示/隱藏 寶箱記錄面板（可設定：`BoxUI.ToggleKey`）
- **F7** — 顯示/隱藏 開箱統計面板（可設定：`BoxOpenUI.ToggleKey`）
- **F3** — 顯示/隱藏 掉寶熱力圖（可設定：`LootMapUI.ToggleKey`）
- **F4** — 進入價格框位置調整（拖動）模式（可設定：`Price.AdjustKey`）
- **右鍵物品** — 釘選／取消釘選價格框（釘選後滑過曲線可讀每一天）
- **滑鼠拖曳** — 移動面板（位置自動記住、彼此獨立；面板不會被拖出畫面外）
- 右上 **重置** 鈕歸零重算；**◀ ▶** 翻看過去關卡紀錄
- **PageUp / PageDown** — 調整面板透明度；**Ctrl + PageUp / PageDown** — 縮放所有面板

> ⚠️ 面板點擊會**穿透**到遊戲（外掛只被動讀取滑鼠、不攔截輸入），戰鬥中點面板時角色仍會動作，這是正常行為。

---

## 安裝

### A. 首次安裝（沒裝過 BepInEx）— 給朋友的做法
1. 到 **[Releases](../../releases/latest)** 下載 `TBH-DpsMeter-vX.Y.Z.zip`。
2. Steam → 對「TBH: Task Bar Hero」按右鍵 → 管理 → 瀏覽本機檔案
   （資料夾裡會看到 `TaskBarHero.exe`）。
3. 把 zip 裡的**所有檔案**解壓進那個資料夾，讓 `winhttp.dll`、`doorstop_config.ini`、
   `dotnet`、`BepInEx` 跟 `TaskBarHero.exe` 在**同一層**（問是否覆蓋選「是」）。
4. **一定要透過 Steam 啟動**遊戲（直接點 exe 不會載入外掛）。
5. 第一次啟動會黑畫面 1～3 分鐘（一次性分析），之後正常。

### B. macOS CrossOver 檢查／修復
在 CrossOver 上，Wine 可能載入內建的 `winhttp.dll`，而不是 BepInEx 的 proxy；macOS 也可能把下載下來的檔案標成 quarantine。若遊戲內沒有 overlay，且 `<遊戲資料夾>\BepInEx\LogOutput.log` 沒有產生，請在 repo 根目錄先檢查：

```sh
bash scripts/repair-crossover-macos.sh --check
```

如果檢查結果顯示缺少 `winhttp` override 或有 quarantine 標記，再執行修復並透過 Steam 重開：

```sh
bash scripts/repair-crossover-macos.sh --repair --launch
```

通常不需要指定 bottle；腳本會自動掃描含有 `TaskBarHero.exe` 的 CrossOver bottle。如果你有多個 CrossOver bottle，請加上 `--bottle "<your-bottle-name>"`；如果 Steam library 不在預設位置，請加上 `--game-dir "/path/to/TaskbarHero"`。`--check` 是唯讀檢查；`--repair` 只會清除 macOS quarantine metadata，並替 `TaskBarHero.exe` 設定 scoped Wine override，不會修改遊戲數值或外掛程式碼。

### C. 更新外掛（已經裝過、只是換新版）
**對，更新只要換 DLL 一個檔案就好。** BepInEx 本體不用動。

把新版 `TBH.DpsMeter.dll` 覆蓋到：
```
<遊戲資料夾>\BepInEx\plugins\TBH.DpsMeter.dll
```
> 覆蓋前請**先完全關閉遊戲**（執行中時 DLL 被佔用，無法覆蓋）。換完透過 Steam 重開即可。

---

## 設定
設定檔：`<遊戲資料夾>\BepInEx\config\tbh.dpsmeter.cfg`（第一次跑完才會產生）
```
[General]
Language = Auto   # 可改成 zh-Hant / zh-Hans / en / ja / es 強制語言
```

## 移除
刪掉遊戲資料夾裡的：`winhttp.dll`、`doorstop_config.ini`、`.doorstop_version`、
`dotnet\`、`BepInEx\`，即可完全還原成原版。

---

## 從原始碼編譯（開發者）
```
dotnet build DpsMeter/DpsMeter.csproj -c Release
# 產物：DpsMeter\bin\Release\TBH.DpsMeter.dll
copy DpsMeter\bin\Release\TBH.DpsMeter.dll  <遊戲>\BepInEx\plugins\
```
重啟遊戲請**透過 Steam**（此 Unity 6 build 直接啟動 exe 不會注入 BepInEx 的 winhttp proxy）。

### 運作原理
- **造成傷害：** Harmony postfix 掛在 `TaskbarHero.Monster.ebj(DamageInfo, bool)`，
  以 `Unit.b_isHero` 過濾出玩家側命中，讀 `OriginDamage` / `IsCritical` / `DamageType`。
- **承受傷害：** Harmony postfix 掛在 `TaskbarHero.Hero.ebj(DamageInfo, bool)`，計入攻擊者
  非玩家的命中，讀 `OriginDamage` / `IsCritical` / `DamageType` / `DamageAttribute`。
- **波次邊界：** 輪詢 `StageManager.stageState`（MONSTERSPAWN → BATTLE → REORGANIZATION），
  每次 MONSTERSPAWN 重置、REORGANIZATION 凍結。
- DPS / DTPS 數學在純 C# 的 `DpsTracker` / `DamageTakenTracker`，並有單元測試（`TrackerTests`）。

---

## ⚠️ 免責聲明
本外掛透過 BepInEx 注入遊戲、僅**被動讀取**傷害數據、不修改任何遊戲數值，且本遊戲為單機作品。
但**任何第三方修改／注入工具都可能違反遊戲或平台（如 Steam）的使用條款**，並存在導致帳號被封鎖、
存檔損毀或其他損失的風險。

**使用本軟體即代表你自行承擔全部風險。** 對於因使用本外掛而導致的任何帳號封鎖、停權、資料毀損或
其他直接或間接損害，作者**概不負責**。若不接受此條件，請勿使用。

## 授權
[MIT](LICENSE) © 2026 WarmBed
