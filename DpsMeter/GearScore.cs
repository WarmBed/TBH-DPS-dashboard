using System.Collections.Generic;

namespace TbhDpsMeter
{
    /// <summary>Pure (no Unity / no BepInEx) GearScore logic, unit-tested in TrackerTests.
    /// itemScore = gradeBase[grade] + level*LevelWeight + Σ affix(value*statWeight) + Σ socket(value*statWeight).
    /// Coefficient tables are tunable constants; calibrate against real in-game values (see plan Task 7).</summary>
    public static class GearScore
    {
        // Base points per rarity. Keys are upper-cased EGradeType names; unknown -> Default.
        private static readonly Dictionary<string, double> GradeBase = new Dictionary<string, double>
        {
            { "COMMON", 0 }, { "NORMAL", 0 },
            { "UNCOMMON", 50 }, { "MAGIC", 50 },
            { "RARE", 120 },
            { "EPIC", 250 },
            { "LEGENDARY", 450 },
            { "MYTHIC", 700 },
        };
        private const double GradeDefault = 0;
        private const double LevelWeight = 2.0;
        // points per applied socket (裝飾/雕刻/銘文) — the save records counts, not per-gem stats.
        private const double SocketWeight = 40.0;

        // Per-stat normalisation so attack (~hundreds) and crit% (~0.05) contribute comparably.
        // Keys are the affix/stat names emitted by HeroProbe (lower-cased on lookup). Default = 1.
        private static readonly Dictionary<string, double> StatWeight = new Dictionary<string, double>
        {
            { "attack", 1.0 },
            { "hp", 0.1 },
            { "armor", 1.0 },
            { "critrate", 1000.0 },
            { "critdmg", 200.0 },
            { "aspd", 300.0 },
            { "mspd", 50.0 },
        };
        private const double StatWeightDefault = 1.0;

        public static double WeightOf(string stat)
        {
            if (string.IsNullOrEmpty(stat)) return StatWeightDefault;
            return StatWeight.TryGetValue(stat.ToLowerInvariant(), out var w) ? w : StatWeightDefault;
        }

        // Stat -> offence/defence bucket for the attack/defence split. Names match HeroProbe/SaveGearReader.
        private static readonly HashSet<string> DefStats = new HashSet<string>
        { "hp", "armor", "fireres", "coldres", "lightres", "chaosres", "dodge", "block", "hpregen" };
        private static readonly HashSet<string> OffStats = new HashSet<string>
        { "attack", "aspd", "critrate", "critdmg", "aoe", "cdr", "multistrike", "projcount", "hpleech",
          "phys%", "fire%", "cold%", "light%", "chaos%", "castspd", "projdmg", "meleedmg", "aoedmg", "summondmg" };

        /// <summary>+1 offensive, -1 defensive, 0 neutral (general/util) for a stat name.</summary>
        public static int Bucket(string stat)
        {
            if (string.IsNullOrEmpty(stat)) return 0;
            string s = stat.ToLowerInvariant();
            if (OffStats.Contains(s)) return 1;
            if (DefStats.Contains(s)) return -1;
            return 0;
        }

        public static double GradePoints(string grade)
        {
            if (string.IsNullOrEmpty(grade)) return GradeDefault;
            return GradeBase.TryGetValue(grade.ToUpperInvariant(), out var b) ? b : GradeDefault;
        }

        /// <summary>One scored line for the detailed view: a label and its point contribution.</summary>
        public struct Part { public string Label; public double Points; public Part(string l, double p) { Label = l; Points = p; } }

        public class ItemScore
        {
            public double Total;
            public double Attack;    // offensive affix points
            public double Defense;   // defensive affix points
            public readonly List<Part> Parts = new List<Part>();
        }

        public static ItemScore ScoreItem(GearItem g)
        {
            var s = new ItemScore();
            if (g == null) return s;
            double gb = GradePoints(g.Grade);
            s.Parts.Add(new Part("grade", gb)); s.Total += gb;
            double lv = g.Level * LevelWeight;
            if (lv != 0) { s.Parts.Add(new Part("level", lv)); s.Total += lv; }
            foreach (var a in g.Affixes) Add(s, a.Name, a.Value * WeightOf(a.Name), a.Name);
            // live per-gem socket stats (rare); usually empty — socket value comes from the counts below.
            foreach (var a in g.Sockets) Add(s, "socket:" + a.Name, a.Value * WeightOf(a.Name), a.Name);
            int sockets = g.DecoCount + g.EngraveCount + g.InscribeCount;
            if (sockets != 0) { double p = sockets * SocketWeight; s.Parts.Add(new Part("sockets", p)); s.Total += p; }
            return s;
        }

        // add a scored line, bucketing its points into Attack/Defense (general stays in Total only).
        private static void Add(ItemScore s, string label, double pts, string statForBucket)
        {
            s.Parts.Add(new Part(label, pts)); s.Total += pts;
            int b = Bucket(statForBucket);
            if (b > 0) s.Attack += pts; else if (b < 0) s.Defense += pts;
        }

        /// <summary>Whole-character roll-up: total, plus offence/defence split (affix-based).</summary>
        public struct CharScore { public double Total, Attack, Defense; }

        public static CharScore ScoreCharacter(CharacterSnapshot snap)
        {
            var cs = new CharScore();
            if (snap == null) return cs;
            foreach (var g in snap.Equipment)
            {
                var s = ScoreItem(g);
                cs.Total += s.Total; cs.Attack += s.Attack; cs.Defense += s.Defense;
            }
            return cs;
        }
    }
}
