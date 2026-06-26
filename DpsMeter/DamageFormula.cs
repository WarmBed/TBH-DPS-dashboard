namespace TbhDpsMeter
{
    /// <summary>A hero's final (post-aggregation) combat stats, read live from the pm engine via HeroProbe.
    /// These are the inputs to the damage formula. Extended as the native decomp reveals which the
    /// formula actually consumes.</summary>
    public struct CombatStats
    {
        public double AttackDamage;   // "attack"  — final attack value
        public double AttackSpeed;    // "aspd"
        public double CritChance;     // "critrate" 0..1
        public double CritDamage;     // "critdmg"  multiplier (e.g. 3.57 = 357%)
        public double Multistrike;    // "Multistrike"
        public double ProjCount;      // "ProjCount"
    }

    /// <summary>Pure damage-formula module. TBH combat is client-authoritative (see memory
    /// client-authoritative-combat) so the literal per-hit arithmetic IS extractable — it comes from the
    /// native decomp of the pm engine + skill scaling. Until that lands, the damage-specific body is a
    /// CLEARLY-MARKED PLACEHOLDER; the game-independent pieces (crit expected-multiplier) are real and
    /// unit-tested. No Unity deps.</summary>
    public static class DamageFormula
    {
        /// <summary>Expected crit multiplier on AVERAGE damage = 1 + chance×(critDamage − 1).
        /// chance clamped to 0..1; critDamage below 1 treated as 1 (no negative crit).</summary>
        public static double CritMultiplier(double critChance, double critDamage)
        {
            if (critChance < 0) critChance = 0;
            if (critChance > 1) critChance = 1;
            if (critDamage < 1) critDamage = 1;
            return 1.0 + critChance * (critDamage - 1.0);
        }

        /// <summary>PLACEHOLDER expected DPS = AttackDamage × AttackSpeed × CritMultiplier. The REAL formula
        /// (skill base + scaling, per-element / per-delivery %, multistrike, projectile count, armor
        /// mitigation) replaces this body once the native decomp delivers it — inputs/shape may change.</summary>
        public static double ExpectedDps(CombatStats s)
        {
            // TODO(native-decomp): transcribe the pm-engine damage formula here.
            return s.AttackDamage * s.AttackSpeed * CritMultiplier(s.CritChance, s.CritDamage);
        }
    }
}
