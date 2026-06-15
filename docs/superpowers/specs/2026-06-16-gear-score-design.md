# GearScore 裝備評分 — 設計文件

日期:2026-06-16
狀態:設計已確認,待寫實作計畫

## 目標

在 TBH DPS Meter 中加入類似 WoW GearScore 的「裝備評分」:把每個角色一身裝備壓成一個一眼就懂的數字,方便比較戰力與判斷「換哪件提升最多」。TBH 沒有內建顯示此分數,但底層資料足夠自行計算。

## 計分公式(稀有度 + 等級 + 詞綴 + 三槽)

每件裝備:

```
itemScore =  gradeBase[稀有度]
           + 裝備等級 × levelWeight
           + Σ 詞綴(屬性值 × statWeight[屬性])
           + Σ 三槽內容物(裝飾/雕刻/銘文,同樣用 statWeight 加權)
```

角色總分 = 該角色全身件數的 itemScore 加總。

係數:
- `gradeBase[grade]`:每個稀有度一個底分,COMMON → … → LEGENDARY 遞增。可調表。
- `levelWeight`:每點裝備等級的分數。
- `statWeight[屬性]`:把不同屬性(攻擊 / 暴擊% / HP / 護甲 …)normalize 到可比尺度的固定係數表。詞綴與三槽內容物**共用同一套** `statWeight`。

**校準步驟(實作時)**:公式「結構」在此釘死;`gradeBase` / `levelWeight` / `statWeight` 的**確切數字**要等進遊戲 dump 一次真實的稀有度底分、裝備等級、詞綴值之後校準到合理範圍,避免某一項數量級壓過其他項。校準結果寫進實作計畫並以常數表落在 `GearScore.cs`,日後可調(設定檔化屬 YAGNI,暫不做)。

## 資料層改動

### `GearItem`(RunModels.cs)新增欄位
- `Grade`(string):稀有度 / EGradeType 名稱。
- `Level`(int):裝備等級(截圖的「需要等級」)。0 = 未知。
- `Sockets`(List):三槽內容物,每個內容物帶屬性名 + 數值(複用 `Affix` 結構或等價型別)。空槽不列入。
- `Icon`(transient,不序列化):該裝備的 sprite/texture 參考,供面板畫 icon。讀不到為 null。

### `HeroProbe.ReadGear` 補抓
- `Grade` ← 沿用 hover price 邏輯已驗證的 `brkk`(EGradeType),套用同一個 live item 物件。
- `Level` ← 進遊戲 dump 確認 live item(tf)上的裝備等級欄位後讀取(**待查證**)。
- `Sockets` ← dump 確認三個槽容器(裝飾 / 雕刻 / 銘文)各自的內容物路徑後讀取(**待查證**)。

所有新欄位讀不到時安全降級(見「驗證風險」)。

### 純計分邏輯
新檔 `GearScore.cs`,無 Unity / BepInEx 依賴,可在 `TrackerTests` 單元測試,與 `StageCompare.cs` 同風格。對外提供:
- 單件 `ScoreItem(GearItem)` → 該件總分 **+ 逐項貢獻明細**(gradeBase / 等級 / 每條詞綴 / 每個槽內容物各自的分數),供「詳細」檢視逐條顯示。
- 角色 `ScoreCharacter(CharacterSnapshot)` → 總分 + 逐件分數明細。

## 顯示

### 新分頁「裝備評分」(主畫面)
- 新增 `GearScoreOverlayBehaviour`,在 `Awake()` 呼叫 `PanelRegistry.Register(...)` 自動掛上 F1 中控台,不需改 hub。結構抄 `CompareOverlayBehaviour`。
- 即時顯示全隊每個角色的**總分**(`HeroProbe.FindParty()` 已讀整隊;solo 自動退化成單一角色)。
- **詳細 / 簡易兩種檢視,以面板內一個開關按鈕切換**:
  - **簡易**:每個角色總分 + 逐件一行(icon / 部位 / 名稱 / 稀有度 / 裝備等級 / 該件分數)。
  - **詳細**:在簡易的逐件行下展開,列出該件每一條**效果**(詞綴 + 三槽內容物),**每條效果旁標出它貢獻幾分**,讓使用者看得出分數如何由稀有度 / 等級 / 各效果組成。
- **裝備 icon**:每件行首顯示。`ReadGear` 嘗試讀該裝備的 sprite/texture(**待查證**);讀得到就用 `GUI.DrawTexture` 畫出,讀不到退回部位字元 / 文字(降級)。
- 不依賴比較模式,隨時可看當下裝分。

### F1 中控台
- 透過 `PanelRegistry.Register` 自動出現開關項。
- 標題列順手顯示角色總分(headline 數字)。

### 關卡比較
- 本期**不改**。公式為 pure 函式,日後可在 `StageCompare.GearChange` 列加「裝分 +N」差值,屬後續增量。

## 驗證風險

不確定處:**裝備等級**、**三槽內容物**、**裝備 icon sprite** 的反射讀取路徑(IL2CPP 混淆欄位)。

- 實作計畫第一步:加一次性 `DiagGearScore` dump(沿用 `HeroProbe.Diagnose` 手法),進遊戲跑一次,確認等級欄位、三槽容器路徑、icon sprite 路徑,並順便擷取真實詞綴值供係數校準。
- 防呆降級:
  - 裝備等級讀不到 → 該項以 0 計(不計等級分),總分仍可算。
  - 三槽內容物讀不到 → 退回「只算已填充槽數 × 固定分」。
  - 稀有度讀不到 → `gradeBase` 以 COMMON 計。
  - icon 讀不到 → 退回部位字元 / 文字,不畫圖。

## 不在本期範圍(YAGNI)
- 關卡比較頁的逐件裝分差值。
- 係數設定檔化 / 使用者自訂權重 UI。
