# DPS 按角色拆分 — 設計文件

日期:2026-06-16
狀態:設計已確認,待寫實作計畫

## 目標

在 DPS 面板(F9)加入「按角色」的傷害拆分:整隊每個英雄各自貢獻多少輸出,讓玩家看出誰打得多。
透過一個 **簡易 / 詳細** 切換鈕控制顯示,solo 時自動隱藏。承受傷害端不在本期範圍。

## 顯示 / 切換

DPS 面板加一個 **簡易 / 詳細** 切換鈕(沿用裝備評分面板 `GearScoreOverlayBehaviour` 的切換樣式)。

- **簡易**(預設):面板維持現狀 —— 即時 / 峰值 / 平均 DPS + 傷害類型分布條。**完全不變**。
- **詳細**:在傷害類型分布條下方多一段 **「按角色」**:
  - 每個參戰英雄一行:**職業色名稱 + 該角色 DPS + 總傷 + 佔比%**。
  - 依**總傷由高到低**排序。
- **Solo 自動隱藏**:場上只有 1 個角色時,「按角色」整段不顯示(即使在詳細模式)。

## 資料層

### Hook
傷害 hook `DamageHooks.MonsterDealt`(`DpsMeter/Hooks.cs`)每次命中時,除了現有的 `amount / crit / type`,
再讀**攻擊者的角色身分**(英雄 key,經由 `EEquipClassType` 推導,與裝備評分同一套 `HeroProbe.ReadClass`/
`ReadHeroKey` 的邏輯),傳入 tracker。讀不到時用 0(未知)—— 視為「不分角色」。

### DpsTracker
擴充 `DpsTracker`(`DpsMeter/DpsTracker.cs`):
- `Record(...)` 多收一個 `charKey` 參數;滑動視窗的每筆事件多帶一個角色 key。
- 逐角色 **總傷 / 佔比** 累加。
- 逐角色 **DPS**:用現有的 5 秒滑動視窗、依角色 key 過濾計算(與主面板即時 DPS 同口徑,只是分角色)。
- snapshot 新增逐角色清單:`{ charKey, totalDamage, share, dps }`,依總傷排序。
- 純邏輯,於 `TrackerTests` 單元測試(沿用現有測試風格)。

### 名稱 / 職業色
角色顯示名稱與職業色由 charKey(英雄 key)解析:沿用 `HeroProbe`/在地化既有的英雄名稱解析
(`GameLoc("HeroName_" + heroKey)`),職業色用一張 heroKey/class → 顏色的小表(面板內)。

## 驗證風險

唯一不確定:`a.Attacker` 是 `Unit`,要確認在它身上讀職業的路徑 —— 直接讀 `EEquipClassType`,
或透過它的 `cache`(與 `HeroProbe.ReadClass` 相同手法)。

- 實作計畫第一步:`DebugDamage` gated 的一次性 dump,確認攻擊者的職業讀取路徑。
- 防呆降級:讀不到角色 key → 該筆記為「未知(0)」;若全隊都讀不到,詳細模式的「按角色」段自動隱藏,
  **現有 DPS / 類型功能完全不受影響**。

## 不在本期範圍(YAGNI)

- 承受傷害「按來源 / 按怪物」拆分。
- 每角色再細拆傷害**類型**(角色 × 類型矩陣)。
- 切換鈕的狀態持久化(本期記憶體內即可,重啟回簡易)。
