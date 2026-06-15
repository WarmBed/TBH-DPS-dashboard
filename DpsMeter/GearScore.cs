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
            foreach (var a in g.Affixes) { double p = a.Value * WeightOf(a.Name); s.Parts.Add(new Part(a.Name, p)); s.Total += p; }
            foreach (var a in g.Sockets) { double p = a.Value * WeightOf(a.Name); s.Parts.Add(new Part("socket:" + a.Name, p)); s.Total += p; }
            return s;
        }

        public static double ScoreCharacter(CharacterSnapshot snap)
        {
            if (snap == null) return 0;
            double total = 0;
            foreach (var g in snap.Equipment) total += ScoreItem(g).Total;
            return total;
        }
    }
}
