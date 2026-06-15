using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace TbhDpsMeter
{
    /// <summary>Reads equipped gear from the decrypted Easy Save 3 save file (SaveFile_Live.es3),
    /// keyed by heroKey. This sidesteps the in-memory ACTk ObscuredULong collection (which can't be
    /// enumerated). Decrypt: AES-128-CBC, key = PBKDF2-SHA1(password, salt=first16, 100), iv=first16.</summary>
    public static class SaveGearReader
    {
        // ES3 password for SaveFile_Live.es3. NOTE: the game can change this on update — if gear stops
        // reading after a patch, update this constant (the community save tools track the current one).
        private const string Password = "emuMqG3bLYJ938ZDCfieWJ";

        /// <summary>Hero level per heroKey, populated as a side-effect of the last <see cref="ReadParty"/>
        /// call (the save's heroSaveDatas[].HeroLevel). Used by the farming planner's exp-retention model.</summary>
        public static readonly Dictionary<int, int> LastHeroLevels = new Dictionary<int, int>();

        /// <summary>Equipped skill keys per heroKey (save's equippedSKillKey, -1 slots dropped). Stable
        /// across game updates (plain JSON), unlike the obfuscated in-memory skill list.</summary>
        public static readonly Dictionary<int, System.Collections.Generic.List<int>> LastHeroSkills = new Dictionary<int, System.Collections.Generic.List<int>>();

        /// <summary>Total accumulated HeroExp per heroKey (save's HeroExp). Used to value-match the live
        /// in-memory exp accessor so per-run exp deltas survive obfuscation renames.</summary>
        public static readonly Dictionary<int, double> LastHeroExp = new Dictionary<int, double>();

        /// <summary>Account gold from the save's currenySaveDatas (the game's own misspelling) entry whose
        /// Key == 100001. The live in-memory currency store is obfuscated (ue+su churns every update); the
        /// save's plain Key/Quantity is the stable source. Populated by <see cref="ReadGold"/>/ReadParty.</summary>
        public static long LastGold;
        private const int GoldCurrencyKey = 100001;

        /// <summary>Decrypt the live save and return current account gold (Key 100001 Quantity), or the last
        /// known value on failure. Cheap enough for run-boundary calls; not for per-frame use.</summary>
        public static long ReadGold()
        {
            try
            {
                string path = Path.Combine(UnityEngine.Application.persistentDataPath, "SaveFile_Live.es3");
                if (!File.Exists(path)) return LastGold;
                string json = Decrypt(File.ReadAllBytes(path), Password);
                if (string.IsNullOrEmpty(json)) return LastGold;
                var outer = Json.Parse(json);
                string inas = Json.Str(Json.Get(Json.Get(outer, "PlayerSaveData"), "value"));
                var inner = string.IsNullOrEmpty(inas) ? outer : Json.Parse(inas);
                var curr = FindArray(inner, "currenySaveDatas");
                if (curr != null)
                    foreach (var c in curr)
                        if ((int)Json.Num(Json.Get(c, "Key")) == GoldCurrencyKey)
                        { LastGold = Json.Long(Json.Get(c, "Quantity")); break; }
            }
            catch { }
            return LastGold;
        }

        /// <summary>Active-skill levels per heroKey, sorted by talent key — read from the save's
        /// attributeSaveDatas. The talent tree's column-3/4 nodes (key%10 ∈ {3,4}) ARE the active skills;
        /// their Level is the real in-game skill level (the obfuscated in-memory level field is NOT — it
        /// returns an effective/garbage value, e.g. 13 for a level-5 skill). Paired in CharacterReader
        /// with the equipped non-basic skills (also sorted by key). Stable across game updates.</summary>
        public static readonly Dictionary<int, System.Collections.Generic.List<int>> LastHeroSkillLevels = new Dictionary<int, System.Collections.Generic.List<int>>();

        /// <summary>Parse the live save and return equipped gear per heroKey. Empty on any failure.</summary>
        public static Dictionary<int, List<GearItem>> ReadParty()
        {
            var result = new Dictionary<int, List<GearItem>>();
            try
            {
                string path = Path.Combine(UnityEngine.Application.persistentDataPath, "SaveFile_Live.es3");
                if (!File.Exists(path)) return result;
                string json = Decrypt(File.ReadAllBytes(path), Password);
                if (string.IsNullOrEmpty(json)) return result;

                var outer = Json.Parse(json);
                // PlayerSaveData.value is itself a JSON string
                string inas = Json.Str(Json.Get(Json.Get(outer, "PlayerSaveData"), "value"));
                var inner = string.IsNullOrEmpty(inas) ? outer : Json.Parse(inas);

                var items = FindArray(inner, "itemSaveDatas");
                var byUid = new Dictionary<long, object>();
                if (items != null)
                    foreach (var it in items)
                    {
                        long uid = Json.Long(Json.Get(it, "UniqueId"));
                        if (uid != 0) byUid[uid] = it;
                    }

                // active-skill levels: the talent tree (attributeSaveDatas) holds the real per-skill level.
                // Group leveled column-3/4 nodes (key%10 ∈ {3,4}) by heroKey (talent key range
                // [heroKey*1000, heroKey*1000+1000)), sorted by talent key.
                LastHeroSkillLevels.Clear();
                var attrs = FindArray(inner, "attributeSaveDatas");
                if (attrs != null)
                {
                    var byHero = new Dictionary<int, List<KeyValuePair<int, int>>>();
                    foreach (var a in attrs)
                    {
                        int ak = (int)Json.Num(Json.Get(a, "Key"));
                        int lv = (int)Json.Num(Json.Get(a, "Level"));
                        if (ak <= 0 || lv <= 0) continue;
                        int col = ak % 10;
                        if (col != 3 && col != 4) continue;          // active-skill columns only
                        int hk = ak / 1000;                          // 201033 -> 201
                        if (!byHero.TryGetValue(hk, out var l)) byHero[hk] = l = new List<KeyValuePair<int, int>>();
                        l.Add(new KeyValuePair<int, int>(ak, lv));
                    }
                    foreach (var kv in byHero)
                    {
                        kv.Value.Sort((x, y) => x.Key.CompareTo(y.Key));
                        var levels = new List<int>();
                        foreach (var p in kv.Value) levels.Add(p.Value);
                        LastHeroSkillLevels[kv.Key] = levels;
                    }
                }

                var heroes = FindArray(inner, "heroSaveDatas");
                if (heroes != null)
                {
                    LastHeroLevels.Clear();
                    LastHeroSkills.Clear();
                    LastHeroExp.Clear();
                    foreach (var h in heroes)
                    {
                        int heroKey = (int)Json.Num(Json.Get(h, "heroKey"));
                        if (heroKey == 0) continue;
                        double he = Json.Num(Json.Get(h, "HeroExp"));
                        if (he > 0) LastHeroExp[heroKey] = he;
                        var sk = Json.Arr(Json.Get(h, "equippedSKillKey"));
                        if (sk != null)
                        {
                            var keys = new System.Collections.Generic.List<int>();
                            foreach (var k in sk) { int kk = (int)Json.Num(k); if (kk > 0) keys.Add(kk); }
                            LastHeroSkills[heroKey] = keys;
                        }
                        int heroLevel = (int)Json.Num(Json.Get(h, "HeroLevel"));
                        if (heroLevel > 0) LastHeroLevels[heroKey] = heroLevel;
                        var equipped = Json.Arr(Json.Get(h, "equippedItemIds"));
                        if (equipped == null) continue;
                        var list = new List<GearItem>();
                        for (int slot = 0; slot < equipped.Count; slot++)
                        {
                            long uid = Json.Long(equipped[slot]);
                            if (uid == 0) continue;
                            if (!byUid.TryGetValue(uid, out var item)) continue;
                            int itemKey = (int)Json.Num(Json.Get(item, "ItemKey"));
                            var g = new GearItem { Slot = "slot" + slot, Name = "item" + itemKey, ItemKey = itemKey, Uid = (ulong)uid };
                            // applied socket counts (裝飾/雕刻/銘文) — plain ints in the save instance
                            g.DecoCount = (int)Json.Num(Json.Get(item, "DecorationAppliedTotalCount"));
                            g.EngraveCount = (int)Json.Num(Json.Get(item, "EngravingAppliedTotalCount"));
                            g.InscribeCount = (int)Json.Num(Json.Get(item, "InscriptionAppliedTotalCount"));
                            var ench = Json.Arr(Json.Get(item, "EnchantData"));
                            if (ench != null)
                                foreach (var m in ench)
                                {
                                    int st = (int)Json.Num(Json.Get(m, "StatType"));
                                    double val = Json.Num(Json.Get(m, "Value"));
                                    if (st == 0 || val == 0) continue;
                                    g.Affixes.Add(new Affix(StatName(st), val));
                                }
                            list.Add(g);
                        }
                        result[heroKey] = list;
                    }
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("SaveGearReader: " + e.Message); }
            return result;
        }

        private static string Decrypt(byte[] data, string password)
        {
            if (data == null || data.Length <= 16) return null;
            var iv = new byte[16];
            Array.Copy(data, 0, iv, 0, 16);
            byte[] key;
            using (var kdf = new Rfc2898DeriveBytes(password, iv, 100))   // SHA1 (matches the web tool)
                key = kdf.GetBytes(16);
            using (var aes = Aes.Create())
            {
                aes.Key = key; aes.IV = iv; aes.Mode = CipherMode.CBC; aes.Padding = PaddingMode.PKCS7;
                using (var dec = aes.CreateDecryptor())
                {
                    byte[] plain = dec.TransformFinalBlock(data, 16, data.Length - 16);
                    return Encoding.UTF8.GetString(plain);
                }
            }
        }

        /// <summary>First array found anywhere under the node with the given key (depth-first).</summary>
        private static List<object> FindArray(object node, string key)
        {
            if (node is Dictionary<string, object> d)
            {
                if (d.TryGetValue(key, out var v) && v is List<object> direct) return direct;
                foreach (var val in d.Values)
                {
                    var r = FindArray(val, key);
                    if (r != null) return r;
                }
            }
            else if (node is List<object> list)
            {
                foreach (var item in list)
                {
                    var r = FindArray(item, key);
                    if (r != null) return r;
                }
            }
            return null;
        }

        // StatType enum (subset) -> friendly key for affix display; unknown -> "stat{n}".
        private static readonly Dictionary<int, string> StatNames = new Dictionary<int, string>
        {
            {1,"attack"}, {2,"aspd"}, {3,"critrate"}, {4,"critdmg"}, {5,"hp"}, {6,"armor"}, {7,"mspd"},
            {8,"AoE"}, {10,"cdr"}, {12,"FireRes"}, {13,"ColdRes"}, {14,"LightRes"}, {15,"ChaosRes"},
            {16,"Dodge"}, {17,"Block"}, {20,"Multistrike"}, {21,"HpLeech"}, {22,"ProjCount"}, {23,"HpRegen"},
            {24,"Phys%"}, {25,"Fire%"}, {26,"Cold%"}, {27,"Light%"}, {28,"Chaos%"},
            {49,"CastSpd"}, {53,"ProjDmg"}, {54,"MeleeDmg"}, {55,"AoEDmg"}, {56,"SummonDmg"},
        };

        private static string StatName(int st) => StatNames.TryGetValue(st, out var n) ? n : "stat" + st;
    }
}
