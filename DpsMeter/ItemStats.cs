using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>Aggregated counts of everything the player is holding in the backpack, warehouse (stash)
    /// and trading stash — NOT equipped gear. Built by <see cref="SaveGearReader.ReadInventory"/> from the
    /// decrypted save's slot grids (inventory/stash/tradingStash) joined to itemSaveDatas for the ItemKey,
    /// then bucketed by grade and category. Drives the F8 item-stats panel.</summary>
    public sealed class InventoryStats
    {
        public int BagUsed, BagTotal;        // backpack (inventorySaveDatas) occupied / slots
        public int StashUsed, StashTotal;    // warehouse (stashSaveDatas)
        public int TradeUsed, TradeTotal;    // trading stash (tradingStashSaveDatas)
        public int Total;                    // BagUsed + StashUsed + TradeUsed (distinct slot occupancy)

        /// <summary>One row per distinct ItemKey, count = total held across all three areas. Sorted by
        /// rarity (best first), then name.</summary>
        public readonly List<ItemCount> Items = new List<ItemCount>();

        /// <summary>grade name -> count, ordered best-rarity-first. Unknown grade keyed as "".</summary>
        public readonly List<KeyValuePair<string, int>> ByGrade = new List<KeyValuePair<string, int>>();

        /// <summary>type label -> count, ordered by count desc. Label is the gear subtype (SWORD/RING…) for
        /// gear, else the category (MATERIAL/STAGEBOX…).</summary>
        public readonly List<KeyValuePair<string, int>> ByType = new List<KeyValuePair<string, int>>();
    }

    /// <summary>A merged inventory row: an ItemKey, how many are held, and its display metadata.</summary>
    public struct ItemCount
    {
        public int ItemKey;
        public int Count;
        public string Name;     // localized; falls back to "item{key}"
        public string Grade;    // EGradeType name, "" if unknown
        public string Type;     // gear subtype or category label
    }
}
