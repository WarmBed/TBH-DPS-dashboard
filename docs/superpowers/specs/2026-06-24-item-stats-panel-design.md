# 物品統計面板 (Item Stats Panel) — 設計

日期：2026-06-24
熱鍵：**F8**（目前未使用；F2 也空著）

## 目標

新增一個面板，統計玩家**倉庫 (stash) + 背包 (inventory) + 交易倉 (trading stash)** 內所有物品的數量，提供三種視圖：依品階分組、依種類分組、逐項清單。**不含**英雄身上已裝備的物品。

## 資料來源（已驗證）

存檔 `SaveFile_Live.es3` 解密後（既有 `SaveGearReader` 的解密邏輯）內含三個 slot 陣列：

| 陣列 | 意義 | 結構 |
|---|---|---|
| `inventorySaveDatas` | 背包 | `{ Index, ItemUniqueId, IsUnlock, IsUnlockedByRune }` |
| `stashSaveDatas` | 倉庫 | `{ Index, ItemUniqueId, IsUnLock }` |
| `tradingStashSaveDatas` | 交易倉 | `{ Index, ItemUniqueId, IsUnLock }` |

- 每一筆 = 一個格子。`ItemUniqueId == 0` = 空格。佔用數 = 非 0 的格子數；總格數 = 陣列長度。
- `ItemUniqueId` join 既有 `itemSaveDatas`（uid → `ItemKey`）取得物品種類。
- 樣本存檔實測：背包 76/260、倉庫 194/343、交易倉 0/10，合計 **270 件**，全部能對映 `itemSaveDatas`（missing=0）。

### Meta 擴充（品階 / 種類 / 名稱）

- 目前 bundled `item_meta.json` 只收 **GEAR**（5760 筆中只有裝備），非裝備（材料/箱子/符文…）查不到 grade/type。
- 來源確認：`https://taskbarhero.wiki/data/items.json`，**帶 `User-Agent` header** 時回傳完整 5944 筆陣列，每筆 `{ id(=ItemKey), name{lang}, grade, type, gear, level, icon, affix, slug }`。（不帶 UA 會回傳 market 分頁格式，無 ItemKey。）
- 實測：270 件持有物對此完整表 **100% 覆蓋**（grade + type 皆有）。`type` 為大類（GEAR / MATERIAL / STAGEBOX…），裝備細分種類在 `gear` 欄位（BOW / HELMET…）。
- **動作**：重新產生 bundled `item_meta.json`，移除 gear-only 過濾，納入全部 5944 筆。需新增一支產生腳本（`scripts/fetch-item-meta.mjs` 或 .py），fetch 時帶 UA。
- 名稱沿用既有 `ItemNameStore.Get(itemKey)`：實測 111 件非裝備名稱 100% 覆蓋（離線），另有 live fallback。

## 面板呈現

沿用現有 overlay 面板樣式（標題列、可拖曳、固定高度捲動沿用 GearScore 機制）+ box-open 既有品階配色。

1. **頂部摘要列**：背包 76/260、倉庫 194/343、交易倉 0/10、**合計 270 件**（佔用/總格）。
2. **依品階分組**：每個 EGradeType（COMMON/UNCOMMON/RARE/LEGENDARY/ARCANA/IMMORTAL/BEYOND…）一條，品階色 bar + 數量。
3. **依種類分組**：裝備用 `gear` 細分（武器/防具/飾品…），非裝備用 `type`（材料/箱子/符文…），各類一行 + 數量。
4. **逐項清單**：每個 **ItemKey 合併成一行**，顯示 icon + 中文名 + 品階色 + **×N**（N = 該 ItemKey 在倉/背/交易倉的總持有件數）。固定高度、滾輪捲動。

## 決策（已確認）

- 熱鍵：**F8**
- 逐項清單：同 ItemKey 多件 **合併為一行 ×N**（不逐件展開）
- 範圍：**僅倉庫 + 背包 + 交易倉**，不含已裝備

## 新增 / 改動檔案

| 檔案 | 變更 |
|---|---|
| `DpsMeter/SaveGearReader.cs` | 新增讀取三個 slot 陣列、回報每區佔用/總格、產出 `ItemKey → 件數` 彙總 |
| `DpsMeter/ItemStats.cs`（新） | 彙總模型：品階分組、種類分組、ItemKey 合併計數 |
| `DpsMeter/ItemStatsOverlayBehaviour.cs`（新） | F8 面板繪製（摘要 + 三視圖 + 捲動） |
| `DpsMeter/ItemMetaStore.cs` | 確認非裝備 grade/type/gear 取得（既有 API 已足夠，視 meta 重生後驗證） |
| `DpsMeter/Plugin.cs` | 註冊 F8 熱鍵 + config bind |
| `DpsMeter/item_meta.json` | 重新產生，納入全部物品（非僅 gear） |
| `scripts/fetch-item-meta.mjs`（新） | 從 wiki 帶 UA 抓取、輸出完整 meta |
| `DpsMeter/Localization.cs` | 面板字串（5 語） |

## 待實作時校正

- grade 加總 214 ≠ 持有 270 的細節（疑為跨槽位重複 uid 或計數方式）——彙總時以「實際佔用格子」為準逐一計數，確保總和 = 270。
- 確認交易倉是否要併入「倉庫」或單列。預設單列顯示。
