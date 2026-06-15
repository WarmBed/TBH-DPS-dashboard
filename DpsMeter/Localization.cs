using System.Collections.Generic;
using UnityEngine;

namespace TbhDpsMeter
{
    public enum Lang { ZhHant = 0, En = 1, Ja = 2, ZhHans = 3, Es = 4 }

    /// <summary>Tiny localization table for the overlay UI.
    /// zh-Hant / English / 日本語 / zh-Hans / Español.</summary>
    public static class Loc
    {
        public static Lang Current = Lang.ZhHant;

        // True when the user left Language=Auto: we then follow the game's in-game locale live.
        private static bool _auto;
        private static float _nextAutoCheck;

        public static void Init(string cfg)
        {
            _auto = false;
            switch ((cfg ?? "Auto").Trim().ToLowerInvariant())
            {
                case "zh": case "zh-hant": case "zh_tw": case "chinese": case "繁體中文": Current = Lang.ZhHant; break;
                case "zh-hans": case "zh_cn": case "zh-cn": case "simplified": case "简体中文": Current = Lang.ZhHans; break;
                case "en": case "english": Current = Lang.En; break;
                case "ja": case "jp": case "japanese": case "日本語": Current = Lang.Ja; break;
                case "es": case "spanish": case "español": case "espanol": Current = Lang.Es; break;
                default: _auto = true; Current = Detect(); break;
            }
        }

        /// <summary>In Auto mode, re-read the game's current locale so an in-game language switch
        /// updates the overlays live. Throttled to ~1/sec; called from the overlay Update loop.</summary>
        public static void MaybeRefreshAuto()
        {
            if (!_auto) return;
            float t = Time.realtimeSinceStartup;
            if (t < _nextAutoCheck) return;
            _nextAutoCheck = t + 1f;
            var g = GameLang();
            if (g.HasValue) Current = g.Value;
        }

        /// <summary>Map the current language to the wiki's farm_stages.json locale code.</summary>
        public static string WikiLangCode()
        {
            switch (Current)
            {
                case Lang.ZhHant: return "zh-Hant";
                case Lang.ZhHans: return "zh-Hans";
                case Lang.Ja: return "ja-JP";
                case Lang.Es: return "es-ES";
                default: return "en-US";
            }
        }

        private static Lang Detect()
        {
            // Prefer the game's in-game locale (what the player actually selected); the system
            // language is only a fallback for before Localization is ready / when unavailable.
            var g = GameLang();
            if (g.HasValue) return g.Value;
            try
            {
                switch (Application.systemLanguage)
                {
                    case SystemLanguage.Japanese: return Lang.Ja;
                    case SystemLanguage.ChineseSimplified: return Lang.ZhHans;
                    case SystemLanguage.Chinese:
                    case SystemLanguage.ChineseTraditional: return Lang.ZhHant;
                    case SystemLanguage.Spanish: return Lang.Es;
                    case SystemLanguage.English: return Lang.En;
                }
            }
            catch { }
            return Lang.En;
        }

        /// <summary>The game's currently selected Unity-Localization locale, mapped to our Lang.
        /// Null if Localization isn't ready or the code is unrecognized.</summary>
        private static Lang? GameLang()
        {
            try
            {
                const string LS = "UnityEngine.Localization.Settings.LocalizationSettings";
                var sel = Refl.CallStatic(LS, "get_SelectedLocale");
                if (sel == null) return null;
                var id = Refl.Get(sel, "Identifier");
                string code = Refl.Str(Refl.Get(id, "Code"));
                if (string.IsNullOrEmpty(code)) code = Refl.Str(id);   // LocaleIdentifier.ToString() fallback
                return MapCode(code);
            }
            catch { return null; }
        }

        private static Lang? MapCode(string code)
        {
            if (string.IsNullOrEmpty(code)) return null;
            code = code.Trim().ToLowerInvariant().Replace('_', '-');
            if (code.StartsWith("zh"))
            {
                if (code.Contains("hans") || code.Contains("cn") || code.Contains("sg")) return Lang.ZhHans;
                return Lang.ZhHant;   // zh / zh-hant / zh-tw / zh-hk
            }
            if (code.StartsWith("ja")) return Lang.Ja;
            if (code.StartsWith("es")) return Lang.Es;
            if (code.StartsWith("en")) return Lang.En;
            return null;
        }

        // key -> { zh-Hant, English, 日本語, zh-Hans, Español }
        private static readonly Dictionary<string, string[]> Table = new Dictionary<string, string[]>
        {
            { "dps_title",      new[] { "TBH DPS", "TBH DPS", "TBH DPS", "TBH DPS", "TBH DPS" } },
            { "hub_title",      new[] { "中控台", "Control Center", "コントロール", "中控台", "Centro" } },
            { "hide_on_menu",   new[] { "選單隱藏", "Hide in menu", "メニュー時隠す", "选单隐藏", "Ocultar" } },
            { "border",         new[] { "邊框", "Border", "枠線", "边框", "Borde" } },
            { "day_stats",      new[] { "總數 · 平均間隔", "total · avg gap", "合計 · 平均間隔", "总数 · 平均间隔", "total · intervalo" } },
            { "font_big",       new[] { "大字", "Big", "大", "大字", "Grande" } },
            { "font_small",     new[] { "小字", "Small", "小", "小字", "Pequeño" } },
            { "lootmap_title",  new[] { "掉寶熱力圖", "Loot Heatmap", "ドロップ分布", "掉宝热力图", "Mapa de botín" } },
            { "metric_opens",   new[] { "開箱率", "Opens", "開封数", "开箱率", "Aperturas" } },
            { "metric_pickup",  new[] { "寶箱獲取", "Box Pickups", "宝箱取得", "宝箱获取", "Cajas" } },
            { "metric_loot",    new[] { "掉寶率", "Loot", "良品率", "掉宝率", "Botín" } },
            { "metric_openlog", new[] { "開箱紀錄", "Open Log", "開封記録", "开箱记录", "Registro" } },
            { "lm_total",       new[] { "總計", "Total", "合計", "总计", "Total" } },
            { "lm_today",       new[] { "今日", "Today", "今日", "今日", "Hoy" } },
            { "lm_week",        new[] { "本週", "This week", "今週", "本周", "Semana" } },
            { "taken_title",    new[] { "受到傷害", "Damage Taken", "被ダメージ", "受到伤害", "Daño recibido" } },
            { "reset",          new[] { "重置", "Reset", "リセット", "重置", "Reiniciar" } },
            { "peak",           new[] { "峰值", "Peak", "ピーク", "峰值", "Pico" } },
            { "avg",            new[] { "平均", "Avg", "平均", "平均", "Prom." } },
            { "total_dealt",    new[] { "總傷", "Total", "合計", "总伤", "Total" } },
            { "total_taken",    new[] { "總承受", "Total", "総被ダメ", "总承受", "Total" } },
            { "duration",       new[] { "時長", "Time", "時間", "时长", "Tiempo" } },
            { "crit",           new[] { "暴擊", "Crit", "会心", "暴击", "Crít." } },
            { "crit_share",     new[] { "暴傷佔", "CritDmg", "会心割合", "暴伤占", "DañoCr" } },
            { "wave_short",     new[] { "波", "W", "波", "波", "Ol" } },
            { "review",         new[] { "回顧", "Review", "履歴", "回顾", "Histor." } },
            { "review_tag",     new[] { "平均", "avg", "平均", "平均", "prom" } },
            { "live_hint",      new[] { "即時統計（◀ 看歷史）", "Live  (◀ history)", "リアルタイム（◀ 履歴）", "实时统计（◀ 看历史）", "En vivo  (◀ historial)" } },
            { "review_hint",    new[] { "瀏覽存檔（▶ 回到即時）", "Saved run  (▶ live)", "保存記録（▶ 現在）", "浏览存档（▶ 回到实时）", "Guardado  (▶ en vivo)" } },
            { "per_sec_taken",  new[] { "每秒承受", "Taken/s", "被ダメ/秒", "每秒承受", "Recib./s" } },
            { "biggest_hit",    new[] { "最大單擊", "Biggest", "最大単発", "最大单击", "Máx." } },
            { "hits",           new[] { "受擊", "Hits", "被弾", "受击", "Golpes" } },
            { "incoming_crit",  new[] { "入站暴擊", "In.Crit", "被会心", "入站暴击", "Cr.recib" } },
            { "element_dist",   new[] { "元素分布", "Elements", "属性分布", "元素分布", "Elementos" } },
            // stage-compare panel
            { "compare_title",  new[] { "關卡比較", "Stage Compare", "ステージ比較", "关卡比较", "Comparar" } },
            { "baseline",       new[] { "基準", "Baseline", "基準", "基准", "Base" } },
            { "this_run",       new[] { "這場", "This", "この回", "这场", "Esta" } },
            { "set_baseline",   new[] { "設為基準", "Set base", "基準に設定", "设为基准", "Fijar base" } },
            { "pinned",         new[] { "已釘選", "Pinned", "固定中", "已钉选", "Fijado" } },
            { "active_time",    new[] { "有效輸出", "Active", "有効出力", "有效输出", "Activo" } },
            { "idle_time",      new[] { "停輸出", "Idle", "停止", "停输出", "Inactivo" } },
            { "per_wave",       new[] { "每波時間", "Wave times", "波別時間", "每波时间", "Por oleada" } },
            { "dmg_dist",       new[] { "傷害分配", "Damage", "ダメージ配分", "伤害分配", "Daño" } },
            { "gear_changes",   new[] { "裝備變更", "Gear", "装備変更", "装备变更", "Equipo" } },
            { "skill_changes",  new[] { "技能變更", "Skills", "スキル変更", "技能变更", "Habilidades" } },
            { "stat_changes",   new[] { "屬性", "Stats", "ステータス", "属性", "Atributos" } },
            { "no_runs",        new[] { "尚無紀錄", "No runs yet", "記録なし", "尚无记录", "Sin datos" } },
            // gear-score panel
            { "gearscore_title", new[] { "裝備評分", "Gear Score", "装備スコア", "装备评分", "Puntuación" } },
            { "gearscore_empty", new[] { "找不到角色", "No characters found", "キャラ未検出", "找不到角色", "Sin personajes" } },
            { "mode_simple",    new[] { "簡易", "Simple", "簡易", "简易", "Simple" } },
            { "mode_detailed",  new[] { "詳細", "Detailed", "詳細", "详细", "Detalle" } },
            { "grade",          new[] { "稀有度", "Rarity", "レア度", "稀有度", "Rareza" } },
            { "level",          new[] { "等級", "Level", "レベル", "等级", "Nivel" } },
            { "reset_all",      new[] { "清除全部", "Reset all", "全削除", "清除全部", "Borrar todo" } },
            { "confirm_reset",  new[] { "確認清除?", "Confirm?", "確認?", "确认清除?", "¿Confirmar?" } },
            { "uncategorized",  new[] { "未分類", "Other", "未分類", "未分类", "Otros" } },
            { "lv",             new[] { "Lv", "Lv", "Lv", "Lv", "Nv" } },
            { "total_time",     new[] { "總時長", "Total", "総時間", "总时长", "Total" } },
            { "trend",          new[] { "通關秒數趨勢", "Clear-time trend", "クリア秒数推移", "通关秒数趋势", "Tendencia" } },
            { "runs",           new[] { "場", "runs", "回", "场", "part." } },
            { "chart_hint",     new[] { "點某點看該場詳細比較", "click a point for details", "点で詳細比較", "点击查看详细", "click un punto" } },
            // rewards
            { "gold",           new[] { "金幣", "Gold", "ゴールド", "金币", "Oro" } },
            { "exp",            new[] { "經驗", "EXP", "経験値", "经验", "EXP" } },
            { "boxes",          new[] { "寶箱", "Boxes", "宝箱", "宝箱", "Cajas" } },
            { "rewards",        new[] { "獎勵", "Rewards", "報酬", "奖励", "Recompensas" } },
            { "farm_title",     new[] { "刷關效率", "Farming Planner", "周回効率", "刷关效率", "Farmeo" } },
            { "stage_col",      new[] { "關卡", "Stage", "ステージ", "关卡", "Etapa" } },
            { "clear_sec",      new[] { "時間", "Time", "時間", "时间", "Tiempo" } },
            { "source_col",     new[] { "來源", "Source", "ソース", "来源", "Fuente" } },
            { "src_measured",   new[] { "實測", "Real", "実測", "实测", "Real" } },
            { "src_estimated",  new[] { "估", "Est.", "推定", "估", "Est." } },
            { "src_old",        new[] { "舊", "Old", "旧", "旧", "Viejo" } },
            { "update_available", new[] { "有新版", "update available", "新バージョン", "有新版", "actualización" } },
            { "download",       new[] { "下載", "Download", "DL", "下载", "Bajar" } },
            { "downloading",    new[] { "下載中…", "downloading…", "DL中…", "下载中…", "bajando…" } },
            { "restart_apply",  new[] { "已下載，重開遊戲套用", "downloaded — restart to apply", "DL完了・再起動で適用",
                                        "已下载，重开游戏应用", "listo — reinicia para aplicar" } },
            { "update_error",   new[] { "更新檢查失敗", "update check failed", "更新確認失敗", "更新检查失败", "fallo de actualización" } },
            { "box_title",      new[] { "寶箱記錄", "Box Log", "宝箱ログ", "宝箱记录", "Cajas" } },
            { "box_total",      new[] { "總計", "Total", "合計", "总计", "Total" } },
            { "box_boss",       new[] { "王箱", "Boss", "ボス箱", "王箱", "Jefe" } },
            { "box_sound",      new[] { "音效", "Sound", "音声", "音效", "Sonido" } },
            { "box_vol",        new[] { "音量", "Vol", "音量", "音量", "Vol" } },
            { "box_test",       new[] { "試聽", "Test", "試聴", "试听", "Probar" } },
            { "snd_on",         new[] { "開", "On", "オン", "开", "On" } },
            { "snd_off",        new[] { "關", "Off", "オフ", "关", "Off" } },
            { "snd_file",       new[] { "音效檔", "Sound file", "音声ファイル", "音效档", "Archivo" } },
            { "snd_pick",       new[] { "選擇…", "Browse…", "選択…", "选择…", "Elegir…" } },
            { "snd_builtin",    new[] { "內建嗶聲", "built-in chime", "内蔵音", "内建提示音", "interno" } },
            { "box_per_hr",     new[] { "個/小時", "/hr", "個/時", "个/小时", "/h" } },
            { "box_empty",      new[] { "尚未取得寶箱", "no boxes yet", "宝箱なし", "尚未取得宝箱", "sin cajas" } },
            { "time_col",       new[] { "時間", "Time", "時刻", "时间", "Hora" } },
            { "boxopen_title",  new[] { "開箱統計", "Box Opens", "開封統計", "开箱统计", "Aperturas" } },
            { "boxopen_total",  new[] { "開出", "Opened", "開封", "开出", "Abiertas" } },
            { "boxopen_kind",   new[] { "箱種", "Kind", "箱種", "箱种", "Tipo" } },
            { "boxopen_grade",  new[] { "品質", "Grade", "品質", "品质", "Calidad" } },
            { "boxopen_item",   new[] { "物品", "Item", "アイテム", "物品", "Objeto" } },
            { "box_kind_normal",new[] { "一般", "Normal", "通常", "一般", "Normal" } },
            { "box_kind_boss",  new[] { "王箱", "Boss", "ボス", "王箱", "Jefe" } },
            { "box_kind_actboss",new[]{ "首領", "ActBoss", "章ボス", "首领", "ActJefe" } },
            { "box_kind_unknown",new[]{ "未知", "Unknown", "不明", "未知", "Desc." } },
            { "price_panel",    new[] { "Steam 報價", "Steam Price", "Steam 価格", "Steam 报价", "Precio Steam" } },
            { "price_drag_hint",new[] { "拖曳移動位置", "Drag to move", "ドラッグで移動", "拖动移动位置", "Arrastra para mover" } },
            { "price_drag_done",new[] { "完成", "to finish", "完了", "完成", "para terminar" } },
            { "order_book",     new[] { "掛單簿", "Order book", "板情報", "挂单簿", "Libro" } },
            { "pin_hint",       new[] { "右鍵釘選看掛單", "Right-click to pin + order book", "右クリックで固定", "右键钉选看挂单", "Clic dcho. para fijar" } },
            { "grade_common",   new[] { "普通", "Common", "コモン", "普通", "Común" } },
            { "grade_uncommon", new[] { "罕見", "Uncommon", "アンコモン", "罕见", "Infrecuente" } },
            { "grade_rare",     new[] { "稀有", "Rare", "レア", "稀有", "Raro" } },
            { "grade_legendary",new[] { "傳奇", "Legendary", "レジェンダリー", "传奇", "Legendario" } },
            { "grade_immortal", new[] { "不朽", "Immortal", "イモータル", "不朽", "Inmortal" } },
            { "grade_arcana",   new[] { "至寶", "Treasure", "至宝", "至宝", "Tesoro" } },
            { "grade_beyond",   new[] { "超凡", "Transcendent", "超凡", "超凡", "Trascendente" } },
            { "grade_celestial",new[] { "天界", "Celestial", "セレスティアル", "天界", "Celestial" } },
            { "grade_divine",   new[] { "神聖", "Divine", "ディヴァイン", "神圣", "Divino" } },
            { "grade_cosmic",   new[] { "宇宙", "Cosmic", "コズミック", "宇宙", "Cósmico" } },
            { "farm_note",      new[] { "實測為主，未打過用 wiki×個人倍率推估", "Measured first; unplayed = wiki × your multiplier",
                                        "実測優先・未挑戦はwiki×個人倍率で推定", "实测为主，未打过用 wiki×个人倍率推估",
                                        "Real primero; no jugadas = wiki × tu multiplicador" } },
            { "your_mult",      new[] { "你的倍率", "Your mult", "個人倍率", "你的倍率", "Tu mult." } },
            { "retention",      new[] { "保留", "Keep", "維持", "保留", "Ret." } },
            { "farm_stale",     new[] { "估算基於舊裝備，打一場以更新基準", "Estimates use an old build — clear a stage to re-calibrate",
                                        "推定は旧装備基準・1回クリアで更新", "估算基于旧装备，打一场以更新基准",
                                        "Estimaciones con build viejo — juega una etapa para recalibrar" } },
            { "basis",          new[] { "基準", "Basis", "基準", "基准", "Base" } },
            { "cur_build",      new[] { "目前裝備", "current build", "現在の装備", "当前装备", "build actual" } },
            { "old_build",      new[] { "舊裝備", "old build", "旧装備", "旧装备", "build viejo" } },
            { "per_s",          new[] { "/秒", "/s", "/秒", "/秒", "/s" } },
            // stage difficulty (ESTAGEDIFFICULTY)
            { "NORMAL",         new[] { "普通", "Normal", "ノーマル", "普通", "Normal" } },
            { "NIGHTMARE",      new[] { "惡夢", "Nightmare", "ナイトメア", "恶梦", "Pesadilla" } },
            { "HELL",           new[] { "地獄", "Hell", "ヘル", "地狱", "Infierno" } },
            { "TORMENT",        new[] { "折磨", "Torment", "トーメント", "折磨", "Tormento" } },
            // common stat keys (StatType names from RE; unknown keys fall back to the raw name)
            { "attack",         new[] { "攻擊", "Attack", "攻撃", "攻击", "Ataque" } },
            { "aspd",           new[] { "攻速", "AtkSpd", "攻速", "攻速", "Vel.Atq" } },
            { "critrate",       new[] { "暴擊率", "CritRate", "会心率", "暴击率", "Crít%" } },
            { "critdmg",        new[] { "暴傷", "CritDmg", "会心ダメ", "暴伤", "DañoCr" } },
            { "hp",             new[] { "生命", "HP", "HP", "生命", "Vida" } },
            { "armor",          new[] { "護甲", "Armor", "防御", "护甲", "Armadura" } },
            { "mspd",           new[] { "移速", "MoveSpd", "移動速度", "移速", "Vel.mov" } },
            // damage types (EDamageType)
            { "Melee",          new[] { "近戰", "Melee", "近接", "近战", "Melé" } },
            { "Projectile",     new[] { "投射", "Projectile", "投射", "投射", "Proyectil" } },
            { "AOE",            new[] { "範圍", "AOE", "範囲", "范围", "Área" } },
            { "Summon",         new[] { "召喚", "Summon", "召喚", "召唤", "Invoc." } },
            { "DOT",            new[] { "持續", "DoT", "継続", "持续", "DoT" } },
            { "Trap",           new[] { "陷阱", "Trap", "罠", "陷阱", "Trampa" } },
            { "None",           new[] { "無", "None", "なし", "无", "Ninguno" } },
            // damage attributes (EDamageAttribute)
            { "Physical",       new[] { "物理", "Physical", "物理", "物理", "Físico" } },
            { "Fire",           new[] { "火", "Fire", "炎", "火", "Fuego" } },
            { "Cold",           new[] { "冰", "Cold", "氷", "冰", "Frío" } },
            { "Lightning",      new[] { "雷", "Lightning", "雷", "雷", "Rayo" } },
            { "Chaos",          new[] { "混沌", "Chaos", "混沌", "混沌", "Caos" } },
            { "AllElement",     new[] { "全元素", "AllElem", "全属性", "全元素", "Todos" } },
        };

        /// <summary>Localized string for a key (falls back to zh-Hant, then the key).</summary>
        public static string G(string key)
        {
            if (Table.TryGetValue(key, out var a))
            {
                int i = (int)Current;
                if (i >= 0 && i < a.Length && !string.IsNullOrEmpty(a[i])) return a[i];
                return a[0];
            }
            return key;
        }

        /// <summary>Localize a (possibly "+"-combined) English type/attribute name.</summary>
        public static string Name(string en)
        {
            if (string.IsNullOrEmpty(en)) return en;
            if (en.IndexOf('+') >= 0)
            {
                var parts = en.Split('+');
                for (int i = 0; i < parts.Length; i++) parts[i] = G(parts[i]);
                return string.Join("+", parts);
            }
            return G(en);
        }
    }
}
