using System;
using System.Linq;
using TbhDpsMeter;

class Tests
{
    static int _fail = 0;
    static void Check(string name, bool cond, object got = null)
    {
        Console.WriteLine((cond ? "PASS " : "FAIL ") + name + (cond ? "" : "  (got: " + got + ")"));
        if (!cond) _fail++;
    }
    static bool Near(double a, double b, double eps = 0.01) => Math.Abs(a - b) <= eps;

    static int Main()
    {
        // --- total, hits, crit, type breakdown ---
        var t = new DpsTracker(windowSeconds: 5f);
        t.StartEncounter(0f);
        t.Record(100, false, 1, 0f);   // Melee
        t.Record(200, true, 1, 1f);    // Melee crit
        t.Record(300, false, 2, 2f);   // Projectile
        var s = t.GetSnapshot(2f);
        Check("total = 600", Near(s.Total, 600), s.Total);
        Check("hits = 3", s.Hits == 3, s.Hits);
        Check("duration = 2s", Near(s.DurationSeconds, 2f), s.DurationSeconds);
        Check("avg = 300", Near(s.AvgDps, 300), s.AvgDps);
        Check("critRate = 1/3", Near(s.CritRate, 1.0/3), s.CritRate);
        Check("critDmgShare = 200/600", Near(s.CritDamageShare, 200.0/600), s.CritDamageShare);
        var melee = s.ByType.First(p => p.Name == "Melee");
        var proj = s.ByType.First(p => p.Name == "Projectile");
        Check("melee amount = 300", Near(melee.Amount, 300), melee.Amount);
        Check("melee share = 0.5", Near(melee.Share, 0.5), melee.Share);
        Check("projectile share = 0.5", Near(proj.Share, 0.5), proj.Share);

        // --- sliding window drops old events ---
        var t2 = new DpsTracker(windowSeconds: 5f);
        t2.StartEncounter(0f);
        t2.Record(1000, false, 1, 0f);
        // at t=10, the 1000 hit (at t=0) is outside the 5s window
        Check("live dps decays to 0 after window", Near(t2.LiveDps(10f), 0), t2.LiveDps(10f));

        // --- live dps within window: 500 over min(elapsed,window) ---
        var t3 = new DpsTracker(windowSeconds: 5f);
        t3.StartEncounter(0f);
        t3.Record(500, false, 1, 4f);   // elapsed 4 < window 5 -> divide by 4
        Check("live dps early = 125 (500/4)", Near(t3.LiveDps(4f), 125), t3.LiveDps(4f));
        t3.Record(500, false, 1, 6f);   // elapsed 6 > window 5 -> divide by 5; both hits in window (t>=1)
        Check("live dps steady = 200 (1000/5)", Near(t3.LiveDps(6f), 200), t3.LiveDps(6f));

        // --- no early-start spike: a big first hit at ~0 elapsed must not divide by ~0 ---
        var tspike = new DpsTracker(windowSeconds: 5f);
        tspike.StartEncounter(0f);
        tspike.Record(1000, false, 1, 0.01f);   // 0.01s in
        Check("no early spike (1000/1s floor)", Near(tspike.LiveDps(0.01f), 1000), tspike.LiveDps(0.01f));
        Check("peak not inflated by early hit", tspike.GetSnapshot(0.01f).PeakDps <= 1000f, tspike.GetSnapshot(0.01f).PeakDps);

        // --- peak tracks the max live dps seen ---
        Check("peak >= 200", t3.GetSnapshot(6f).PeakDps >= 200f, t3.GetSnapshot(6f).PeakDps);

        // --- reset clears everything ---
        t3.StartEncounter(100f);
        var s3 = t3.GetSnapshot(100f);
        Check("reset total=0", Near(s3.Total, 0), s3.Total);
        Check("reset hits=0", s3.Hits == 0, s3.Hits);
        Check("reset peak=0", Near(s3.PeakDps, 0), s3.PeakDps);

        // --- zero / negative amounts ignored ---
        var t4 = new DpsTracker();
        t4.StartEncounter(0f);
        t4.Record(0, false, 1, 0f);
        t4.Record(-5, false, 1, 0f);
        Check("zero/neg ignored", t4.GetSnapshot(0f).Hits == 0, t4.GetSnapshot(0f).Hits);

        // --- auto-start when damage arrives before StartEncounter ---
        var t5 = new DpsTracker();
        t5.Record(50, false, 1, 3f);
        Check("auto-start records hit", t5.GetSnapshot(3f).Hits == 1, t5.GetSnapshot(3f).Hits);

        // ================= DamageTakenTracker =================
        // amount, isCritical, damageTypeFlag, attributeValue, now
        var dt = new DamageTakenTracker(windowSeconds: 5f);
        dt.StartEncounter(0f);
        dt.Record(100, false, 1, 0, 0f);   // Melee / Physical
        dt.Record(400, true,  2, 1, 1f);   // Projectile / Fire, monster crit, biggest
        dt.Record(300, false, 2, 1, 2f);   // Projectile / Fire
        var ds = dt.GetSnapshot(2f);
        Check("[taken] total = 800", Near(ds.Total, 800), ds.Total);
        Check("[taken] hits = 3", ds.Hits == 3, ds.Hits);
        Check("[taken] duration = 2s", Near(ds.DurationSeconds, 2f), ds.DurationSeconds);
        Check("[taken] avg = 400", Near(ds.AvgDtps, 400), ds.AvgDtps);
        Check("[taken] biggest hit = 400", Near(ds.BiggestHit, 400), ds.BiggestHit);
        Check("[taken] incoming crit rate = 1/3", Near(ds.CritRate, 1.0/3), ds.CritRate);
        var fire = ds.ByAttribute.First(p => p.Name == "Fire");
        var phys = ds.ByAttribute.First(p => p.Name == "Physical");
        Check("[taken] fire amount = 700", Near(fire.Amount, 700), fire.Amount);
        Check("[taken] fire share = 700/800", Near(fire.Share, 700.0/800), fire.Share);
        Check("[taken] physical share = 100/800", Near(phys.Share, 100.0/800), phys.Share);
        Check("[taken] attr sorted: fire first", ds.ByAttribute[0].Name == "Fire", ds.ByAttribute[0].Name);
        var proj2 = ds.ByType.First(p => p.Name == "Projectile");
        Check("[taken] projectile type amount = 700", Near(proj2.Amount, 700), proj2.Amount);

        // sliding window decays
        var dt2 = new DamageTakenTracker(windowSeconds: 5f);
        dt2.StartEncounter(0f);
        dt2.Record(1000, false, 1, 0, 0f);
        Check("[taken] live dtps decays to 0", Near(dt2.LiveDtps(10f), 0), dt2.LiveDtps(10f));

        // steady-state live dtps: 1000 over 5s window
        var dt3 = new DamageTakenTracker(windowSeconds: 5f);
        dt3.StartEncounter(0f);
        dt3.Record(500, false, 1, 0, 4f);
        Check("[taken] live dtps early = 125 (500/4)", Near(dt3.LiveDtps(4f), 125), dt3.LiveDtps(4f));
        dt3.Record(500, false, 1, 0, 6f);
        Check("[taken] live dtps steady = 200 (1000/5)", Near(dt3.LiveDtps(6f), 200), dt3.LiveDtps(6f));
        Check("[taken] peak >= 200", dt3.GetSnapshot(6f).PeakDtps >= 200f, dt3.GetSnapshot(6f).PeakDtps);

        // reset clears everything
        dt3.StartEncounter(100f);
        var ds3 = dt3.GetSnapshot(100f);
        Check("[taken] reset total=0", Near(ds3.Total, 0), ds3.Total);
        Check("[taken] reset hits=0", ds3.Hits == 0, ds3.Hits);
        Check("[taken] reset biggest=0", Near(ds3.BiggestHit, 0), ds3.BiggestHit);
        Check("[taken] reset peak=0", Near(ds3.PeakDtps, 0), ds3.PeakDtps);

        // zero / negative ignored
        var dt4 = new DamageTakenTracker();
        dt4.StartEncounter(0f);
        dt4.Record(0, false, 1, 0, 0f);
        dt4.Record(-5, false, 1, 0, 0f);
        Check("[taken] zero/neg ignored", dt4.GetSnapshot(0f).Hits == 0, dt4.GetSnapshot(0f).Hits);

        // attribute decode
        Check("[taken] decode attr 3 = Lightning", DamageTakenTracker.DecodeAttribute(3) == "Lightning", DamageTakenTracker.DecodeAttribute(3));

        StageCompareTests();
        SerializerTests();
        JsonTests();
        FarmTests();
        FarmDivisorTests();
        ClearTimeSimTests();
        DamageFormulaTests();
        FitEngineTests();
        RunRetentionTests();
        WavDecoderTests();

        // ===== BoxOpenStats: aggregation + percentages =====
        var bo = new BoxOpenStats();
        // Normal(0): 3 common(0), 1 rare(2). Boss(1): 1 common, 1 legendary(3).
        bo.Add(new BoxOpenEvent { Kind = 0, Grade = 0, Name = "a", Stage = "1-1", Time = DateTime.Now });
        bo.Add(new BoxOpenEvent { Kind = 0, Grade = 0, Name = "b", Stage = "1-1", Time = DateTime.Now });
        bo.Add(new BoxOpenEvent { Kind = 0, Grade = 0, Name = "c", Stage = "1-1", Time = DateTime.Now });
        bo.Add(new BoxOpenEvent { Kind = 0, Grade = 2, Name = "d", Stage = "1-1", Time = DateTime.Now });
        bo.Add(new BoxOpenEvent { Kind = 1, Grade = 0, Name = "e", Stage = "1-1", Time = DateTime.Now });
        bo.Add(new BoxOpenEvent { Kind = 1, Grade = 3, Name = "f", Stage = "1-1", Time = DateTime.Now });
        Check("box total = 6", bo.Total() == 6, bo.Total());
        Check("normal total = 4", bo.KindTotal(0) == 4, bo.KindTotal(0));
        Check("normal common count = 3", bo.Count(0, 0) == 3, bo.Count(0, 0));
        Check("normal rare pct = 25", Near(bo.Percent(0, 2), 25.0), bo.Percent(0, 2));
        Check("boss legendary pct = 50", Near(bo.Percent(1, 3), 50.0), bo.Percent(1, 3));
        Check("grade-total common = 4", bo.GradeTotal(0) == 4, bo.GradeTotal(0));
        Check("unknown kind total = 0", bo.KindTotal(3) == 0, bo.KindTotal(3));
        bo.Add(new BoxOpenEvent { Kind = 9, Grade = 99, Name = "x", Stage = "?", Time = DateTime.Now });
        Check("oob kind -> unknown", bo.KindTotal(3) == 1, bo.KindTotal(3));
        Check("oob grade -> common", bo.Count(3, 0) == 1, bo.Count(3, 0));

        var bo2 = new BoxOpenStats();
        bo2.LoadCounts(bo.SerializeCounts());
        Check("counts round-trip normal common", bo2.Count(0, 0) == 3, bo2.Count(0, 0));
        Check("counts round-trip total", bo2.Total() == bo.Total(), bo2.Total());

        var ev0 = new BoxOpenEvent { Kind = 2, Grade = 5, Name = "Sword of\tTabs", Stage = "3-2 HELL", Time = new DateTime(637000000000000000) };
        var ev1 = BoxOpenStats.ParseEvent(BoxOpenStats.SerializeEvent(ev0));
        Check("event round-trip kind", ev1.Kind == 2, ev1.Kind);
        Check("event round-trip grade", ev1.Grade == 5, ev1.Grade);
        Check("event round-trip stage", ev1.Stage == "3-2 HELL", ev1.Stage);
        Check("event round-trip ticks", ev1.Time.Ticks == ev0.Time.Ticks, ev1.Time.Ticks);
        Check("event sanitizes tabs", !ev1.Name.Contains('\t'), ev1.Name);

        var bo3 = new BoxOpenStats();
        for (int i = 0; i < BoxOpenStats.MaxLog + 50; i++)
            bo3.Add(new BoxOpenEvent { Kind = 0, Grade = 0, Name = "n", Stage = "1-1", Time = DateTime.Now });
        Check("log capped at MaxLog", bo3.Log.Count == BoxOpenStats.MaxLog, bo3.Log.Count);

        // ===== BoxStore: pickup persistence round-trip =====
        BoxStore.Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tbh_boxstore_test_" + Guid.NewGuid().ToString("N"));
        BoxStore.Clear();
        BoxStore.Append(new BoxEvent { Time = new DateTime(637000000000000000), Stage = "2-4 HELL", Arg = 910651, Type = "Normal Monster Box Lv65" });
        BoxStore.Append(new BoxEvent { Time = new DateTime(637000000000000001), Stage = "2-4 HELL", Arg = 910999, Type = "Boss Box" });
        var loaded = BoxStore.LoadAll(500);
        Check("boxstore loaded 2", loaded.Count == 2, loaded.Count);
        Check("boxstore stage", loaded[0].Stage == "2-4 HELL", loaded[0].Stage);
        Check("boxstore arg", loaded[1].Arg == 910999, loaded[1].Arg);
        Check("boxstore type", loaded[0].Type == "Normal Monster Box Lv65", loaded[0].Type);
        var capped = BoxStore.LoadAll(1);
        Check("boxstore cap keeps newest", capped.Count == 1 && capped[0].Arg == 910999, capped.Count);
        BoxStore.Clear();
        Check("boxstore clear empties", BoxStore.LoadAll(500).Count == 0, BoxStore.LoadAll(500).Count);

        // ===== BoxOpenStore: stats persistence round-trip =====
        BoxOpenStore.Dir = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "tbh_boxopen_test_" + Guid.NewGuid().ToString("N"));
        BoxOpenStore.Clear();
        var src = new BoxOpenStats();
        src.Add(new BoxOpenEvent { Kind = 0, Grade = 0, Name = "a", Stage = "1-1", Time = DateTime.Now });
        src.Add(new BoxOpenEvent { Kind = 1, Grade = 3, Name = "b", Stage = "1-1", Time = DateTime.Now });
        BoxOpenStore.Save(src);
        var dst = new BoxOpenStats();
        BoxOpenStore.Load(dst);
        Check("boxopenstore counts restored", dst.Total() == 2 && dst.Count(1, 3) == 1, dst.Total());
        Check("boxopenstore log restored", dst.Log.Count == 2, dst.Log.Count);
        BoxOpenStore.Clear();
        var empty = new BoxOpenStats();
        BoxOpenStore.Load(empty);
        Check("boxopenstore clear empties", empty.Total() == 0, empty.Total());

        // --- DPS per-character rollup ---
        var tc = new DpsTracker(windowSeconds: 5f);
        tc.StartEncounter(0f);
        tc.Record(300, false, 1, 0f, 101);   // Knight deals 300
        tc.Record(100, false, 2, 1f, 201);   // Ranger deals 100
        var sc = tc.GetSnapshot(2f);
        Check("bychar count = 2", sc.ByChar.Count == 2, sc.ByChar.Count);
        Check("bychar[0] = 101 (largest)", sc.ByChar[0].CharKey == 101, sc.ByChar[0].CharKey);
        Check("bychar[0] share = 0.75", Near(sc.ByChar[0].Share, 0.75), sc.ByChar[0].Share);
        Check("bychar[1] = 201 share 0.25", sc.ByChar[1].CharKey == 201 && Near(sc.ByChar[1].Share, 0.25), sc.ByChar[1].Share);
        // per-char DPS sums to the overall live DPS (same window/divisor)
        Check("bychar dps sums to live", Near(sc.ByChar[0].Dps + sc.ByChar[1].Dps, sc.LiveDps), sc.ByChar[0].Dps + sc.ByChar[1].Dps);
        // single character -> one entry
        var tc1 = new DpsTracker(); tc1.StartEncounter(0f); tc1.Record(500, false, 1, 0f, 301);
        Check("bychar solo count = 1", tc1.GetSnapshot(1f).ByChar.Count == 1, tc1.GetSnapshot(1f).ByChar.Count);

        // --- GearScore ---
        var giLegendary = new GearItem { Grade = "LEGENDARY", Level = 80 };
        giLegendary.Affixes.Add(new Affix("attack", 100));      // 100*1
        giLegendary.Sockets.Add(new Affix("critrate", 0.03));   // 0.03*1000 = 30
        var isLeg = GearScore.ScoreItem(giLegendary);
        // 250 (LEGENDARY) + 160 (80*2) + 100 (attack) + 30 (socket crit) = 540
        Check("gearscore item = 540", Near(isLeg.Total, 540), isLeg.Total);
        Check("gearscore unknown grade = 0 base", Near(GearScore.GradePoints(""), 0), GearScore.GradePoints(""));
        Check("gearscore unknown stat weight = 1", Near(GearScore.WeightOf("nonsense"), 1), GearScore.WeightOf("nonsense"));

        var gsSnap = new CharacterSnapshot();
        gsSnap.Equipment.Add(giLegendary);
        gsSnap.Equipment.Add(new GearItem { Grade = "RARE", Level = 0 });   // 120 + 0
        Check("gearscore character total = 660", Near(GearScore.ScoreCharacter(gsSnap).Total, 660), GearScore.ScoreCharacter(gsSnap).Total);
        // attack/defence split: attack=100 (offensive), hp is defensive
        var giMix = new GearItem();
        giMix.Affixes.Add(new Affix("attack", 100));  // offensive
        giMix.Affixes.Add(new Affix("hp", 500));      // defensive: 500*0.1 = 50
        var isMix = GearScore.ScoreItem(giMix);
        Check("gearscore attack bucket = 100", Near(isMix.Attack, 100), isMix.Attack);
        Check("gearscore defense bucket = 50", Near(isMix.Defense, 50), isMix.Defense);
        Check("gearscore null item = 0", Near(GearScore.ScoreItem(null).Total, 0), GearScore.ScoreItem(null).Total);

        // socket counts (裝飾/雕刻/銘文) score at 40 pts each
        var giSock = new GearItem { Grade = "RARE", DecoCount = 2, EngraveCount = 1, InscribeCount = 0 };
        // 120 (rare) + (2+1+0)*40 = 240
        Check("gearscore sockets = 240", Near(GearScore.ScoreItem(giSock).Total, 240), GearScore.ScoreItem(giSock).Total);

        Console.WriteLine(_fail == 0 ? "\nALL TESTS PASSED" : $"\n{_fail} TEST(S) FAILED");
        return _fail == 0 ? 0 : 1;
    }

    // ================= StageCompare =================
    static RunRecord Run(string stage, string title, float dur, double total, float avg)
        => new RunRecord { StageId = stage, Title = title, Duration = dur, Total = total, Avg = avg };

    static void StageCompareTests()
    {
        Console.WriteLine("\n-- StageCompare --");
        var r36a = Run("3-6", "a", 80f, 500, 6.1f);
        var r36b = Run("3-6", "b", 72f, 510, 6.7f);   // fastest -> default baseline
        var r36c = Run("3-6", "c", 90f, 480, 5.9f);
        var r41  = Run("4-1", "d", 60f, 300, 5.0f);
        var runs = new System.Collections.Generic.List<RunRecord> { r36a, r36b, r36c, r41 };

        var groups = StageCompare.GroupByStage(runs);
        Check("[cmp] 2 stage groups", groups.Count == 2, groups.Count);
        Check("[cmp] 3-6 has 3 runs", groups["3-6"].Count == 3, groups["3-6"].Count);

        var baseDefault = StageCompare.PickBaseline(groups["3-6"]);
        Check("[cmp] default baseline = fastest (b)", baseDefault.Title == "b", baseDefault.Title);

        var basePinned = StageCompare.PickBaseline(groups["3-6"], "c");
        Check("[cmp] pinned baseline = c", basePinned.Title == "c", basePinned.Title);

        var cmp = StageCompare.Compare(baseDefault, r36a);
        var dur = cmp.Metrics.Find(m => m.Key == "duration");
        Check("[cmp] duration delta = +8", Near(dur.Delta, 8f), dur.Delta);
        var avgm = cmp.Metrics.Find(m => m.Key == "avg");
        Check("[cmp] avg pct ~ -8.96%", Near(avgm.PercentDelta, (6.1 - 6.7) / 6.7 * 100, 0.1), avgm.PercentDelta);
        Check("[cmp] self-compare flags IsBaseline", StageCompare.Compare(baseDefault, baseDefault).IsBaseline, false);

        // wave diffs
        var b = new RunRecord(); b.WaveDurations.AddRange(new[] { 8f, 9f, 10f });
        var c = new RunRecord(); c.WaveDurations.AddRange(new[] { 8.5f, 9f, 13f });
        var wres = StageCompare.Compare(b, c);
        Check("[cmp] 3 wave deltas", wres.Waves.Count == 3, wres.Waves.Count);
        Check("[cmp] wave3 delta = +3", Near(wres.Waves[2].Delta, 3f), wres.Waves[2].Delta);

        // gear & skill & stat diffs (match by slot)
        var bs = new CharacterSnapshot { Captured = true };
        bs.Stats.Add(new StatEntry("attack", 1240));
        bs.Stats.Add(new StatEntry("aspd", 1.45));
        var bow = new GearItem { Slot = "weapon", Name = "FlameBow" }; bow.Affixes.Add(new Affix("Fire", 45));
        bs.Equipment.Add(bow);
        bs.Equipment.Add(new GearItem { Slot = "ring", Name = "Ring" });
        bs.Skills.Add(new SkillEntry("Trap", 5));
        bs.Skills.Add(new SkillEntry("Shot", 3));

        var cs = new CharacterSnapshot { Captured = true };
        cs.Stats.Add(new StatEntry("attack", 1180));
        cs.Stats.Add(new StatEntry("aspd", 1.62));
        var bow2 = new GearItem { Slot = "weapon", Name = "WindBow" }; bow2.Affixes.Add(new Affix("Speed", 18));
        cs.Equipment.Add(bow2);
        cs.Equipment.Add(new GearItem { Slot = "ring", Name = "Ring" });  // unchanged
        cs.Skills.Add(new SkillEntry("Rain", 3));   // added
        cs.Skills.Add(new SkillEntry("Shot", 4));   // level up 3->4
        // Trap removed

        var rb = new RunRecord(); rb.Party.Add(bs);
        var rc = new RunRecord(); rc.Party.Add(cs);
        var dres = StageCompare.Compare(rb, rc);

        var atk = dres.Stats.Find(m => m.Key == "attack");
        Check("[cmp] attack 1240->1180", Near(atk.Baseline, 1240) && Near(atk.Current, 1180), atk.Current);

        Check("[cmp] 1 gear changed (weapon)", dres.Gear.Count == 1 && dres.Gear[0].Kind == StageCompare.ChangeKind.Changed, dres.Gear.Count);
        Check("[cmp] weapon changed key=weapon", dres.Gear[0].Key == "weapon", dres.Gear[0].Key);

        int added = 0, removed = 0, changed = 0;
        foreach (var sc in dres.Skills)
        {
            if (sc.Kind == StageCompare.ChangeKind.Added) added++;
            if (sc.Kind == StageCompare.ChangeKind.Removed) removed++;
            if (sc.Kind == StageCompare.ChangeKind.Changed) changed++;
        }
        Check("[cmp] skills: +1 added (Rain)", added == 1, added);
        Check("[cmp] skills: 1 removed (Trap)", removed == 1, removed);
        Check("[cmp] skills: 1 changed (Shot 3->4)", changed == 1, changed);

        // gear affix order / duplicate-name must not produce a false "Changed"
        var ga = new CharacterSnapshot { Captured = true };
        var gi1 = new GearItem { Slot = "w", Name = "Bow" }; gi1.Affixes.Add(new Affix("Fire", 10)); gi1.Affixes.Add(new Affix("Fire", 20));
        ga.Equipment.Add(gi1);
        var gb = new CharacterSnapshot { Captured = true };
        var gi2 = new GearItem { Slot = "w", Name = "Bow" }; gi2.Affixes.Add(new Affix("Fire", 20)); gi2.Affixes.Add(new Affix("Fire", 10));
        gb.Equipment.Add(gi2);
        var rga = new RunRecord(); rga.Party.Add(ga);
        var rgb = new RunRecord(); rgb.Party.Add(gb);
        var gcmp = StageCompare.Compare(rga, rgb);
        Check("[cmp] reordered dup affixes = no change", gcmp.Gear.Count == 0, gcmp.Gear.Count);
        var gi3 = new GearItem { Slot = "w", Name = "Bow" }; gi3.Affixes.Add(new Affix("Fire", 10)); gi3.Affixes.Add(new Affix("Fire", 30));
        var gc2 = new CharacterSnapshot { Captured = true }; gc2.Equipment.Add(gi3);
        var rgc2 = new RunRecord(); rgc2.Party.Add(gc2);
        var gcmp2 = StageCompare.Compare(rga, rgc2);
        Check("[cmp] real affix change detected", gcmp2.Gear.Count == 1 && gcmp2.Gear[0].Kind == StageCompare.ChangeKind.Changed, gcmp2.Gear.Count);
    }

    // ================= Json parser =================
    static void JsonTests()
    {
        Console.WriteLine("\n-- Json --");
        // mimic the decrypted save shape: PlayerSaveData.value is a stringified JSON
        string inner = "{\\\"heroSaveDatas\\\":[{\\\"heroKey\\\":401,\\\"equippedItemIds\\\":[552399407316076264,0,0]}],\\\"inventorySaveDatas\\\":{\\\"itemSaveDatas\\\":[{\\\"UniqueId\\\":552399407316076264,\\\"ItemKey\\\":520011,\\\"EnchantData\\\":[{\\\"StatType\\\":25,\\\"Value\\\":45},{\\\"StatType\\\":0,\\\"Value\\\":0}]}]}}";
        string outer = "{ \"PlayerSaveData\": { \"__type\":\"string\", \"value\": \"" + inner + "\" } }";
        var root = Json.Parse(outer);
        string val = Json.Str(Json.Get(Json.Get(root, "PlayerSaveData"), "value"));
        Check("[json] outer.value is string", val != null && val.StartsWith("{"), val == null ? "null" : val.Substring(0, 5));
        var parsed = Json.Parse(val);
        var heroes = Json.Arr(Json.Get(parsed, "heroSaveDatas"));
        Check("[json] 1 hero", heroes != null && heroes.Count == 1, heroes?.Count);
        Check("[json] heroKey 401", (int)Json.Num(Json.Get(heroes[0], "heroKey")) == 401, Json.Num(Json.Get(heroes[0], "heroKey")));
        var eq = Json.Arr(Json.Get(heroes[0], "equippedItemIds"));
        Check("[json] equipped uid", Json.Long(eq[0]) == 552399407316076264L, Json.Long(eq[0]));
        var items = Json.Arr(Json.Get(Json.Get(parsed, "inventorySaveDatas"), "itemSaveDatas"));
        Check("[json] item ItemKey 520011", (int)Json.Num(Json.Get(items[0], "ItemKey")) == 520011, Json.Num(Json.Get(items[0], "ItemKey")));
        var ench = Json.Arr(Json.Get(items[0], "EnchantData"));
        Check("[json] enchant StatType 25 val 45", (int)Json.Num(Json.Get(ench[0], "StatType")) == 25 && Json.Num(Json.Get(ench[0], "Value")) == 45, "");
    }

    // ================= RunSerializer round-trip =================
    static void SerializerTests()
    {
        Console.WriteLine("\n-- RunSerializer --");
        var r = new RunRecord
        {
            Title = "06/07 12:34", StageId = "3-6", Total = 489930, Duration = 72.8f,
            Peak = 15020, Avg = 6730, CritRate = 0.099f, CritShare = 0.149f, Waves = 7,
            ActiveSeconds = 61.0f, IdleSeconds = 11.8f,
        };
        r.TypeFlags.Add(2); r.TypeAmounts.Add(300000);
        r.TypeFlags.Add(1); r.TypeAmounts.Add(189930);
        r.WaveDurations.AddRange(new[] { 8.2f, 9.1f, 10.4f });
        r.Samples.Add(new Sample { Dps = 6500.5f, Wave = 1 });
        r.Samples.Add(new Sample { Dps = 7200f, Wave = 2 });
        r.TakenTotal = 4580; r.TakenPeak = 171; r.TakenAvg = 63; r.TakenBiggestHit = 98; r.TakenCritRate = 0f; r.TakenHits = 59;
        r.TakenAttrValues.Add(1); r.TakenAttrAmounts.Add(3000);
        r.TakenTypeFlags.Add(2); r.TakenTypeAmounts.Add(2000);
        r.TakenSamples.Add(new Sample { Dps = 150f, Wave = 1 });
        r.TakenSamples.Add(new Sample { Dps = 202.5f, Wave = 2 });
        var snap = new CharacterSnapshot { Captured = true };
        snap.Stats.Add(new StatEntry("attack", 1240));
        var g = new GearItem { Slot = "weapon", Name = "Flame Bow" };
        g.Affixes.Add(new Affix("Fire", 45)); g.Affixes.Add(new Affix("Crit", 5.5));
        snap.Equipment.Add(g);
        snap.Skills.Add(new SkillEntry("Arrow Rain", 3));
        snap.Character = "priest";
        snap.DamageDealt = 88000;
        r.Party.Add(snap);

        string text = RunSerializer.Serialize(r);
        var r2 = RunSerializer.Deserialize(text.Split('\n'));
        var snap2 = r2.Party.Count > 0 ? r2.Party[0] : null;

        Check("[ser] title", r2.Title == r.Title, r2.Title);
        Check("[ser] stageid", r2.StageId == "3-6", r2.StageId);
        Check("[ser] total", Near(r2.Total, r.Total), r2.Total);
        Check("[ser] active", Near(r2.ActiveSeconds, 61.0), r2.ActiveSeconds);
        Check("[ser] idle", Near(r2.IdleSeconds, 11.8, 0.05), r2.IdleSeconds);
        Check("[ser] 2 type rows", r2.TypeFlags.Count == 2, r2.TypeFlags.Count);
        Check("[ser] wavedur 3", r2.WaveDurations.Count == 3 && Near(r2.WaveDurations[2], 10.4, 0.05), r2.WaveDurations.Count);
        Check("[ser] samples 2", r2.Samples.Count == 2 && r2.Samples[1].Wave == 2, r2.Samples.Count);
        Check("[ser] taken hits", r2.TakenHits == 59, r2.TakenHits);
        Check("[ser] taken samples 2", r2.TakenSamples.Count == 2 && r2.TakenSamples[1].Wave == 2 && Near(r2.TakenSamples[1].Dps, 202.5, 0.1), r2.TakenSamples.Count);
        Check("[ser] snapshot captured", snap2 != null && snap2.Captured, snap2 != null);
        Check("[ser] character id", snap2.Character == "priest", snap2.Character);
        Check("[ser] snap stat", snap2.Stats.Count == 1 && Near(snap2.Stats[0].Value, 1240), snap2.Stats.Count);
        Check("[ser] snap per-hero damage", Near(snap2.DamageDealt, 88000), snap2.DamageDealt);
        Check("[ser] snap gear+affixes", snap2.Equipment.Count == 1 && snap2.Equipment[0].Affixes.Count == 2, snap2.Equipment.Count);
        Check("[ser] gear name preserved", snap2.Equipment[0].Name == "Flame Bow", snap2.Equipment[0].Name);
        Check("[ser] gear affix value", Near(snap2.Equipment[0].Affixes[1].Value, 5.5), snap2.Equipment[0].Affixes[1].Value);
        Check("[ser] snap skill+level", snap2.Skills.Count == 1 && snap2.Skills[0].Level == 3, snap2.Skills.Count);

        // v1 backward compat: old file with no version / no new fields
        string v1 = "title=old\ntotal=1000\nduration=30\navg=33\nwaves=5\ntype=1:1000\nhist=100:1,200:2\n";
        var r3 = RunSerializer.Deserialize(v1.Split('\n'));
        Check("[ser] v1 loads title", r3.Title == "old", r3.Title);
        Check("[ser] v1 total", Near(r3.Total, 1000), r3.Total);
        Check("[ser] v1 no stageid", r3.StageId == "", r3.StageId);
        Check("[ser] v1 no party", r3.Party.Count == 0, r3.Party.Count);
        Check("[ser] v1 samples", r3.Samples.Count == 2, r3.Samples.Count);

        // multi-character round-trip + legacy v2 single-snap compat
        var rp = new RunRecord { StageId = "3-6" };
        var pa = new CharacterSnapshot { Captured = true, Character = "hunter" }; pa.Stats.Add(new StatEntry("attack", 400));
        var pb = new CharacterSnapshot { Captured = true, Character = "priest" }; pb.Skills.Add(new SkillEntry("Heal", 7));
        rp.Party.Add(pa); rp.Party.Add(pb);
        var rp2 = RunSerializer.Deserialize(RunSerializer.Serialize(rp).Split('\n'));
        Check("[ser] party 2 chars", rp2.Party.Count == 2, rp2.Party.Count);
        Check("[ser] char ids", rp2.Party[0].Character == "hunter" && rp2.Party[1].Character == "priest", rp2.Party[1].Character);
        Check("[ser] char2 skill", rp2.Party[1].Skills.Count == 1 && rp2.Party[1].Skills[0].Level == 7, rp2.Party[1].Skills.Count);

        string legacy = "version=2\ntitle=old\nsnap=1\nstat=attack:100\nskill=S\t3\n";
        var rl = RunSerializer.Deserialize(legacy.Split('\n'));
        Check("[ser] legacy snap -> 1 party member", rl.Party.Count == 1 && rl.Party[0].Stats.Count == 1, rl.Party.Count);

        // per-character compare: same party, only priest's skill changed
        var bRun = new RunRecord { StageId = "3-6", Duration = 70 };
        var b1 = new CharacterSnapshot { Captured = true, Character = "hunter" }; b1.Skills.Add(new SkillEntry("Shot", 5));
        var b2 = new CharacterSnapshot { Captured = true, Character = "priest" }; b2.Skills.Add(new SkillEntry("Heal", 3));
        bRun.Party.Add(b1); bRun.Party.Add(b2);
        var cRun = new RunRecord { StageId = "3-6", Duration = 80 };
        var c1 = new CharacterSnapshot { Captured = true, Character = "hunter" }; c1.Skills.Add(new SkillEntry("Shot", 5));
        var c2 = new CharacterSnapshot { Captured = true, Character = "priest" }; c2.Skills.Add(new SkillEntry("Heal", 6));
        cRun.Party.Add(c1); cRun.Party.Add(c2);
        Check("[ser] party chars listed", StageCompare.PartyCharacters(bRun, cRun).Count == 2, StageCompare.PartyCharacters(bRun, cRun).Count);
        var hunterCmp = StageCompare.Compare(bRun, cRun, "hunter");
        Check("[cmp] hunter unchanged skills", hunterCmp.Skills.Count == 0, hunterCmp.Skills.Count);
        var priestCmp = StageCompare.Compare(bRun, cRun, "priest");
        Check("[cmp] priest skill changed 3->6", priestCmp.Skills.Count == 1 && priestCmp.Skills[0].CurrentLevel == 6, priestCmp.Skills.Count);
    }

    // ---- farming planner ----
    static FarmStage Stg(string label, string diff, double hp, double gold, double exp, int waves = 10, int level = 0)
        => new FarmStage { Label = label, Difficulty = diff, Level = level, TotalHP = hp, ExpectedGold = gold, ExpectedEXP = exp,
                           Waves = waves, GoldPerHP = gold / hp, ExpPerHP = exp / hp };
    static RunRecord Run(string stageId, double gold, double expPerHero, float dur, int party, int heroLevel = 0)
    {
        var r = new RunRecord { StageId = stageId, Duration = dur, GoldGained = gold, ExpGained = expPerHero * party };
        for (int i = 0; i < party; i++) r.Party.Add(new CharacterSnapshot { Captured = true, Character = "h" + i, Level = heroLevel });
        return r;
    }

    static void FarmTests()
    {
        // -- loader --
        string json = "[{\"key\":2401,\"label\":\"2-4\",\"act\":2,\"stageNo\":4,\"level\":64,\"difficulty\":\"HELL\"," +
            "\"name\":{\"zh-Hant\":\"測試\",\"en-US\":\"Test\"},\"waves\":10,\"perWave\":3,\"monsterTypes\":2,\"count\":30," +
            "\"totalHP\":42333079,\"expectedGold\":115054,\"expectedEXP\":2244311,\"goldPerHP\":0.0027,\"expPerHP\":0.053}]";
        var parsed = FarmDataLoader.Parse(json);
        Check("[farm] parse count", parsed.Count == 1, parsed.Count);
        Check("[farm] parse stageid", parsed[0].StageId == "2-4 HELL", parsed[0].StageId);
        Check("[farm] parse gold", Near(parsed[0].ExpectedGold, 115054), parsed[0].ExpectedGold);
        Check("[farm] parse name zh-Hant", parsed[0].LocalizedName("zh-Hant") == "測試", parsed[0].LocalizedName("zh-Hant"));
        Check("[farm] name falls back to en", parsed[0].LocalizedName("ko-KR") == "Test", parsed[0].LocalizedName("ko-KR"));

        // -- calibration + ranking (mirrors observed live data) --
        var stages = new System.Collections.Generic.List<FarmStage>
        {
            Stg("2-4", "HELL", 42333079, 115054, 2244311, 10, 64),
            Stg("2-5", "HELL", 31512877, 88032, 1720981, 12, 65),
            Stg("3-1", "HELL", 51936891, 113190, 2235912, 11, 70),   // unmeasured -> estimated
            Stg("1-1", "NORMAL", 560, 14, 16, 10, 1),                // trivial + far-below level (heavy retention)
        };
        var runs = new System.Collections.Generic.List<RunRecord>
        {
            Run("2-4 HELL", 319000, 6160000, 245, 3, 65),
            Run("2-5 HELL", 243246, 4661825, 226, 3, 65),
            Run("1-1 NORMAL", 33991, 550000, 55, 3, 65),     // mislabeled: ratio ~2428x -> must be rejected
        };
        Calibration cal;
        var rows = FarmPlanner.Rank(stages, runs, out cal);
        Check("[farm] MGold ~2.77", Near(cal.MGold, 2.768, 0.05), cal.MGold);
        Check("[farm] MExp ~2.73", Near(cal.MExp, 2.727, 0.05), cal.MExp);
        Check("[farm] has calibration", cal.HasData, cal.HasData);

        EfficiencyRow R(string id) => rows.Find(x => x.Stage.StageId == id);
        Check("[farm] 2-4 measured", R("2-4 HELL").Measured && R("2-4 HELL").Samples == 1, R("2-4 HELL").Measured);
        Check("[farm] 2-4 gold/s ~1302", Near(R("2-4 HELL").GoldPerSec, 319000.0/245, 1), R("2-4 HELL").GoldPerSec);
        Check("[farm] 2-4 exp/s ~25143", Near(R("2-4 HELL").ExpPerSec, 6160000.0/245, 1), R("2-4 HELL").ExpPerSec);
        Check("[farm] 3-1 estimated", !R("3-1 HELL").Measured && R("3-1 HELL").ClearSec > 0, R("3-1 HELL").ClearSec);
        Check("[farm] 3-1 gold/s positive", R("3-1 HELL").GoldPerSec > 0, R("3-1 HELL").GoldPerSec);
        // the mislabeled 1-1 run must NOT make 1-1 a measured row
        Check("[farm] 1-1 rejected -> estimated", !R("1-1 NORMAL").Measured, R("1-1 NORMAL").Measured);

        // exp retention curve (wiki Vt): full near level, decays to 1% floor far away
        Check("[farm] retention same level = 1", Near(FarmPlanner.ExpRetention(65, 65), 1.0), FarmPlanner.ExpRetention(65, 65));
        Check("[farm] retention small gap = 1", Near(FarmPlanner.ExpRetention(65, 64), 1.0), FarmPlanner.ExpRetention(65, 64));
        Check("[farm] retention far below floor", FarmPlanner.ExpRetention(65, 1) <= 0.02, FarmPlanner.ExpRetention(65, 1));
        Check("[farm] retention 0 levels = 1 (unknown)", Near(FarmPlanner.ExpRetention(0, 50), 1.0), FarmPlanner.ExpRetention(0, 50));
        Check("[farm] calib hero level = 65", cal.HeroLevel == 65, cal.HeroLevel);
        // the trivial 1-1 (level 1) estimate must be crushed by retention, not float to the top for exp
        Check("[farm] 1-1 retention crushed", R("1-1 NORMAL").ExpRetention <= 0.02, R("1-1 NORMAL").ExpRetention);

        // time model fit from 2 stages with distinct (waves, HP)
        Check("[farm] time model fitted", cal.HasTimeModel, cal.HasTimeModel);
        Check("[farm] per-wave overhead >= 0", cal.PerWaveSec >= 0, cal.PerWaveSec);
        // trivial low-HP NORMAL stage must NOT read as near-instant (the bug being fixed):
        // its estimate is dominated by the per-wave overhead, so clear time is a few seconds, not ~0
        Check("[farm] trivial stage not near-instant", R("1-1 NORMAL").ClearSec >= 1.0, R("1-1 NORMAL").ClearSec);

        // -- sorting --
        FarmPlanner.Sort(rows, FarmSortKey.ExpPerSec);
        Check("[farm] sort exp desc", rows[0].ExpPerSec >= rows[1].ExpPerSec, rows[0].Stage.StageId);
        FarmPlanner.Sort(rows, FarmSortKey.GoldPerSec);
        Check("[farm] sort gold desc", rows[0].GoldPerSec >= rows[1].GoldPerSec, rows[0].Stage.StageId);

        // -- no runs at all -> wiki per-HP proxy ranking, everything estimated --
        var rows2 = FarmPlanner.Rank(stages, null, out var cal2);
        Check("[farm] no runs -> no calibration", !cal2.HasData, cal2.HasData);
        Check("[farm] no runs -> all estimated", rows2.TrueForAll(x => !x.Measured), "measured leaked");
        Check("[farm] proxy gold/s = goldPerHP", Near(R2(rows2, "2-4 HELL").GoldPerSec, 115054.0/42333079, 1e-9), R2(rows2, "2-4 HELL").GoldPerSec);

        // fastest-clear representative: a merged/AFK long run must NOT inflate the shown clear time
        var mr = new System.Collections.Generic.List<RunRecord>
        {
            Run("2-4 HELL", 319000, 6160000, 245, 3, 65),
            Run("2-4 HELL", 638000, 12320000, 490, 3, 65),   // didn't reset: 2x time & 2x reward
        };
        var rowsM = FarmPlanner.Rank(stages, mr, out _, 65);
        var r24m = rowsM.Find(x => x.Stage.StageId == "2-4 HELL");
        Check("[farm] clear time = fastest, not median", Near(r24m.ClearSec, 245, 1), r24m.ClearSec);
        Check("[farm] merged run rate still ~1302", Near(r24m.GoldPerSec, 319000.0/245, 1), r24m.GoldPerSec);

        BuildFingerprintTests(stages);
    }

    static RunRecord GearedRun(string stageId, double gold, double expPerHero, float dur, int level, string itemName, double affixVal, int skillLevel)
    {
        var r = new RunRecord { StageId = stageId, Duration = dur, GoldGained = gold, ExpGained = expPerHero };
        var snap = new CharacterSnapshot { Captured = true, Character = "201", Level = level };
        var g = new GearItem { Slot = "slot0", Name = itemName };
        g.Affixes.Add(new Affix("Phys%", affixVal));
        snap.Equipment.Add(g);
        snap.Skills.Add(new SkillEntry("Shot", skillLevel, 1));
        r.Party.Add(snap);
        return r;
    }

    static void BuildFingerprintTests(System.Collections.Generic.List<FarmStage> stages)
    {
        // gear identity drives the signature; routine leveling (char level, skill level) must NOT
        var baseRun = GearedRun("2-4 HELL", 319000, 6160000, 245, 65, "EliteBow", 260, 5);
        var same = GearedRun("2-5 HELL", 243246, 4661825, 226, 65, "EliteBow", 260, 5);
        var swapItem = GearedRun("2-4 HELL", 319000, 6160000, 245, 65, "RareBow", 260, 5);
        var reroll = GearedRun("2-4 HELL", 319000, 6160000, 245, 65, "EliteBow", 300, 5);
        var skillUp = GearedRun("2-4 HELL", 319000, 6160000, 245, 65, "EliteBow", 260, 6);
        var charLvUp = GearedRun("2-4 HELL", 319000, 6160000, 245, 70, "EliteBow", 260, 5);   // leveled 65->70
        string sb = FarmPlanner.BuildSignature(baseRun);
        Check("[fp] same loadout same sig", sb == FarmPlanner.BuildSignature(same), "sig mismatch");
        Check("[fp] item swap changes sig", sb != FarmPlanner.BuildSignature(swapItem), "swap not detected");
        Check("[fp] affix reroll changes sig", sb != FarmPlanner.BuildSignature(reroll), "reroll not detected");
        Check("[fp] skill level-up keeps sig (leveling)", sb == FarmPlanner.BuildSignature(skillUp), "skillup wrongly reset");
        Check("[fp] char level-up keeps sig (leveling)", sb == FarmPlanner.BuildSignature(charLvUp), "charlvup wrongly reset");

        // calibration uses only current-build runs: an old-build run is excluded
        var runs = new System.Collections.Generic.List<RunRecord>
        {
            GearedRun("2-4 HELL", 999999, 9999999, 999, 65, "OldBow", 100, 1),  // old build: wildly different numbers
            GearedRun("2-4 HELL", 319000, 6160000, 245, 65, "EliteBow", 260, 5), // current build
            GearedRun("2-5 HELL", 243246, 4661825, 226, 65, "EliteBow", 260, 5), // current build
        };
        string curSig = FarmPlanner.BuildSignature(runs[1]);
        var rows = FarmPlanner.Rank(stages, runs, out var cal, 65, curSig);
        Check("[fp] not stale (current build present)", !cal.Stale, cal.Stale);
        // 2-4 measured should reflect the CURRENT build (245s / 319000), not the old 999-run
        var r24 = rows.Find(x => x.Stage.StageId == "2-4 HELL");
        Check("[fp] measured uses current build", r24.Measured && Near(r24.ClearSec, 245, 1), r24.ClearSec);
        Check("[fp] MGold from current build", Near(cal.MGold, 2.768, 0.1), cal.MGold);

        // change to a brand-new build with no runs -> stale fallback to previous build
        var newBuildSig = FarmPlanner.BuildSignature(GearedRun("2-4 HELL", 0, 0, 1, 65, "GodBow", 500, 9));
        var rows3 = FarmPlanner.Rank(stages, runs, out var cal3, 65, newBuildSig);
        Check("[fp] stale when no current-build runs", cal3.Stale, cal3.Stale);
        Check("[fp] stale still has calibration", cal3.HasData, cal3.HasData);
        // old-build measured data is still SHOWN (not dropped to estimated), flagged as old build
        var r24old = rows3.Find(x => x.Stage.StageId == "2-4 HELL");
        Check("[fp] old-build data shown as measured", r24old.Measured && r24old.MeasuredFromOldBuild, r24old.Measured + "/" + r24old.MeasuredFromOldBuild);
    }

    static EfficiencyRow R2(System.Collections.Generic.List<EfficiencyRow> rows, string id) => rows.Find(x => x.Stage.StageId == id);

    // ================= Farm exp divisor (benched-hero dilution) =================
    static void FarmDivisorTests()
    {
        Console.WriteLine("\n-- Farm exp divisor --");
        // benched party members earn no exp and must NOT dilute exp/hero: priest+mage benched, ranger fielded
        var r = new RunRecord { StageId = "3-10 TORMENT", Duration = 8f, ExpGained = 10240 };
        r.Party.Add(new CharacterSnapshot { Captured = true, Character = "401" });                  // priest, ExpGained 0
        r.Party.Add(new CharacterSnapshot { Captured = true, Character = "301" });                  // mage,   ExpGained 0
        r.Party.Add(new CharacterSnapshot { Captured = true, Character = "201", ExpGained = 10240 }); // ranger fielded
        Check("[farm] divisor = exp-earning heroes (1, not 3)", FarmPlanner.EffectivePartyForExp(r) == 1, FarmPlanner.EffectivePartyForExp(r));

        var r2 = new RunRecord { ExpGained = 300 };
        r2.Party.Add(new CharacterSnapshot { Captured = true, Character = "101", ExpGained = 100 });
        r2.Party.Add(new CharacterSnapshot { Captured = true, Character = "201", ExpGained = 100 });
        r2.Party.Add(new CharacterSnapshot { Captured = true, Character = "301", ExpGained = 100 });
        Check("[farm] all-fielded divisor = 3", FarmPlanner.EffectivePartyForExp(r2) == 3, FarmPlanner.EffectivePartyForExp(r2));

        // legacy run: no per-hero exp captured -> fall back to full party (even split, old behaviour)
        var r3 = new RunRecord { ExpGained = 300 };
        r3.Party.Add(new CharacterSnapshot { Captured = true, Character = "101" });
        r3.Party.Add(new CharacterSnapshot { Captured = true, Character = "201" });
        Check("[farm] legacy no-per-hero-exp falls back to party count (2)", FarmPlanner.EffectivePartyForExp(r3) == 2, FarmPlanner.EffectivePartyForExp(r3));

        Check("[farm] empty party divisor 0", FarmPlanner.EffectivePartyForExp(new RunRecord()) == 0, FarmPlanner.EffectivePartyForExp(new RunRecord()));
    }

    // ================= ClearTimeSim (DPS/speed what-if simulator) =================
    static void ClearTimeSimTests()
    {
        Console.WriteLine("\n-- ClearTimeSim --");

        // F = Σ share×mult: sorcerer(301) 60% ×1.5 + knight(101) 40% ×1.0 = 1.30
        var shares = new System.Collections.Generic.Dictionary<int, double> { { 301, 0.6 }, { 101, 0.4 } };
        var mult = new System.Collections.Generic.Dictionary<int, double> { { 301, 1.5 } };
        Check("[sim] F = Σ share×mult = 1.30", Near(ClearTimeSim.PartyDpsFactor(shares, mult), 1.30), ClearTimeSim.PartyDpsFactor(shares, mult));

        // a benched (0%-share) hero with a huge mult must NOT change F
        var sharesB = new System.Collections.Generic.Dictionary<int, double> { { 101, 1.0 }, { 401, 0.0 } };
        var multB = new System.Collections.Generic.Dictionary<int, double> { { 401, 5.0 } };
        Check("[sim] benched 0%-share hero doesn't move F", Near(ClearTimeSim.PartyDpsFactor(sharesB, multB), 1.0), ClearTimeSim.PartyDpsFactor(sharesB, multB));

        // no fight yet (empty shares) -> F clamps to 1 (no change)
        Check("[sim] empty shares -> F=1", Near(ClearTimeSim.PartyDpsFactor(new System.Collections.Generic.Dictionary<int, double>(), mult), 1.0), ClearTimeSim.PartyDpsFactor(new System.Collections.Generic.Dictionary<int, double>(), mult));

        // --- SimulateSplit: real measured 有效輸出/停輸出. DPS compresses active, speed compresses idle ---
        var sd = ClearTimeSim.SimulateSplit(100, 0, 2.0, 1.0);   // pure active -> DPS×2 halves; speed irrelevant
        Check("[sim] pure-active DPS×2 halves", Near(sd.NewClear, 50) && Near(sd.SavedPct, 0.5), sd.NewClear);
        var ssp = ClearTimeSim.SimulateSplit(0, 100, 5.0, 2.0);  // pure idle -> speed×2 halves; DPS irrelevant
        Check("[sim] pure-idle speed×2 halves", Near(ssp.NewClear, 50), ssp.NewClear);
        var sm = ClearTimeSim.SimulateSplit(60, 40, 2.0, 1.0);   // 60/2 + 40 = 70
        Check("[sim] split mixed newClear = 70", Near(sm.NewClear, 70) && Near(sm.SavedSec, 30), sm.NewClear);
        Check("[sim] split dpsFrac = active/total = 0.6", Near(sm.DpsFrac, 0.6), sm.DpsFrac);
        var sz = ClearTimeSim.SimulateSplit(0, 0, 2.0, 2.0);
        Check("[sim] split zero -> nothing", Near(sz.NewClear, 0) && Near(sz.SavedSec, 0), sz.NewClear);
        // the real 1-4 numbers (active 90.5 / idle 55.5), F=2.11: 38% is movement DPS can't touch -> ~33%, NOT 52%
        var s14 = ClearTimeSim.SimulateSplit(90.5, 55.5, 2.11, 1.0);
        Check("[sim] 1-4 real split saves ~33% (not 52%)", s14.SavedPct > 0.30 && s14.SavedPct < 0.36, s14.SavedPct);

        // --- SimulateFloor: floor (0.8/wave + 4.25) fixed; everything above is DPS-bound ---
        Check("[sim] FixedFloor(29) = 4.25 + 0.8×29 = 27.45", Near(ClearTimeSim.FixedFloor(29), 27.45), ClearTimeSim.FixedFloor(29));
        var sf = ClearTimeSim.SimulateFloor(100, 29, 2.0);       // floor 27.45; (100-27.45)/2 + 27.45 = 63.725
        Check("[sim] floor model 100s/29w/×2 = 63.7", Near(sf.NewClear, 63.725, 0.01), sf.NewClear);
        var sf1 = ClearTimeSim.SimulateFloor(100, 29, 1.0);      // no DPS change -> unchanged
        Check("[sim] floor model ×1 unchanged", Near(sf1.NewClear, 100), sf1.NewClear);
        var sft = ClearTimeSim.SimulateFloor(3, 1, 5.0);         // tiny stage already at floor (5.05 > 3) -> clamps, no save
        Check("[sim] floor clamps tiny stage", Near(sft.NewClear, 3), sft.NewClear);

        // --- SimulateFallback: an uncleared stage uses an average active-fraction ---
        var fb = ClearTimeSim.SimulateFallback(100, 0.6, 2.0, 1.0);   // 100*(0.6/2 + 0.4/1) = 70
        Check("[sim] fallback mixed = 70", Near(fb.NewClear, 70), fb.NewClear);
        var fb2 = ClearTimeSim.SimulateFallback(100, 1.0, 1.0, 5.0);  // all-active -> speed alone saves 0
        Check("[sim] fallback dpsFrac=1: speed alone saves nothing", Near(fb2.SavedSec, 0), fb2.SavedSec);
        var fb0 = ClearTimeSim.SimulateFallback(0, 0.6, 2.0, 2.0);
        Check("[sim] fallback unknown clear (0) -> nothing", Near(fb0.NewClear, 0) && Near(fb0.SavedSec, 0), fb0.NewClear);

        // --- AggregateTiming: median active/idle over a stage's runs that captured a split ---
        var runs = new System.Collections.Generic.List<RunRecord>
        {
            new RunRecord { StageId = "3-6 TORMENT", ActiveSeconds = 90f, IdleSeconds = 50f },
            new RunRecord { StageId = "3-6 TORMENT", ActiveSeconds = 110f, IdleSeconds = 60f },
            new RunRecord { StageId = "3-6 TORMENT", ActiveSeconds = 0f, IdleSeconds = 0f },   // no split -> excluded
            new RunRecord { StageId = "1-1 NORMAL", ActiveSeconds = 20f, IdleSeconds = 10f },
        };
        var tA = ClearTimeSim.AggregateTiming(runs, "3-6 TORMENT", null);
        Check("[sim] aggregate has data, 2 samples", tA.HasData && tA.Samples == 2, tA.Samples);
        Check("[sim] aggregate median active = 100", Near(tA.ActiveSec, 100), tA.ActiveSec);
        Check("[sim] aggregate median idle = 55", Near(tA.IdleSec, 55), tA.IdleSec);
        var tB = ClearTimeSim.AggregateTiming(runs, "9-9 HELL", null);
        Check("[sim] aggregate no runs -> no data", !tB.HasData, tB.HasData);

        // summary: avg savedPct over known rows; best by savedSec; 0-clear excluded
        var rows = new System.Collections.Generic.List<SimRow>
        {
            ClearTimeSim.SimulateSplit(100, 0, 2.0, 1.0), // saved 50, 50%
            ClearTimeSim.SimulateSplit(200, 0, 2.0, 1.0), // saved 100, 50%
            ClearTimeSim.SimulateSplit(0, 0, 2.0, 1.0),   // excluded
        };
        var sum = ClearTimeSim.Summarize(rows, 2.0);
        Check("[sim] summary counts only known-clear rows (2)", sum.Counted == 2, sum.Counted);
        Check("[sim] summary avg savedPct = 0.5", Near(sum.AvgSavedPct, 0.5), sum.AvgSavedPct);
        Check("[sim] summary best index = 1 (saved 100s)", sum.BestIndex == 1, sum.BestIndex);
        Check("[sim] summary carries party DPS factor", Near(sum.PartyDpsFactor, 2.0), sum.PartyDpsFactor);

        // --- BuildStat: sum a run's gear stat (stable key e.g. "AoE"=range), per-hero or whole party ---
        var run = new RunRecord();
        var mage = new CharacterSnapshot { Character = "301" };
        var mg1 = new GearItem(); mg1.Affixes.Add(new Affix("AoE", 100)); mg1.Affixes.Add(new Affix("attack", 50));
        var mg2 = new GearItem(); mg2.Affixes.Add(new Affix("AoE", 60));
        mage.Equipment.Add(mg1); mage.Equipment.Add(mg2);
        var knight = new CharacterSnapshot { Character = "101" };
        var kg1 = new GearItem(); kg1.Affixes.Add(new Affix("AoE", 30));
        knight.Equipment.Add(kg1);
        run.Party.Add(mage); run.Party.Add(knight);
        Check("[sim] BuildStat per-hero range (mage AoE = 160)", Near(ClearTimeSim.BuildStat(run, "AoE", "301"), 160), ClearTimeSim.BuildStat(run, "AoE", "301"));
        Check("[sim] BuildStat whole-party range (190)", Near(ClearTimeSim.BuildStat(run, "AoE", null), 190), ClearTimeSim.BuildStat(run, "AoE", null));
        Check("[sim] BuildStat per-hero attack (mage = 50)", Near(ClearTimeSim.BuildStat(run, "attack", "301"), 50), ClearTimeSim.BuildStat(run, "attack", "301"));
        Check("[sim] BuildStat missing stat -> 0", Near(ClearTimeSim.BuildStat(run, "mspd", "301"), 0), ClearTimeSim.BuildStat(run, "mspd", "301"));
        Check("[sim] BuildStat unknown hero -> 0", Near(ClearTimeSim.BuildStat(run, "AoE", "999"), 0), ClearTimeSim.BuildStat(run, "AoE", "999"));
    }

    // ================= DamageFormula (slot for the native-decompiled combat formula) =================
    static void DamageFormulaTests()
    {
        Console.WriteLine("\n-- DamageFormula --");
        // expected crit multiplier on average damage = 1 + chance×(critDamage−1)
        Check("[dmg] crit 12% ×3.57 -> 1.308", Near(DamageFormula.CritMultiplier(0.12, 3.57), 1.3084, 0.001), DamageFormula.CritMultiplier(0.12, 3.57));
        Check("[dmg] 0% crit -> 1.0", Near(DamageFormula.CritMultiplier(0, 5), 1.0), DamageFormula.CritMultiplier(0, 5));
        Check("[dmg] 100% crit ×2 -> 2.0", Near(DamageFormula.CritMultiplier(1.0, 2.0), 2.0), DamageFormula.CritMultiplier(1.0, 2.0));
        Check("[dmg] critDamage<1 clamped (no negative crit)", Near(DamageFormula.CritMultiplier(0.5, 0.5), 1.0), DamageFormula.CritMultiplier(0.5, 0.5));
        Check("[dmg] chance>1 clamped to 1", Near(DamageFormula.CritMultiplier(2.0, 2.0), 2.0), DamageFormula.CritMultiplier(2.0, 2.0));
        // placeholder ExpectedDps folds crit in: attack × aspd × critMult (real formula replaces this body)
        var cs = new CombatStats { AttackDamage = 1000, AttackSpeed = 2, CritChance = 0, CritDamage = 3 };
        Check("[dmg] placeholder dps = atk×aspd (no crit) = 2000", Near(DamageFormula.ExpectedDps(cs), 2000), DamageFormula.ExpectedDps(cs));
    }

    // ================= FitEngine (gear DB + stat aggregation) =================
    static void FitEngineTests()
    {
        Console.WriteLine("\n-- FitEngine --");
        GearDatabase.Reset();
        string gj = "[{\"k\":301041,\"t\":\"BOW\",\"g\":\"RARE\",\"gg\":\"WEAPON\",\"l\":20,\"p\":\"MAIN_WEAPON\",\"n\":\"bow1\",\"s\":[[\"AttackDamage\",\"FLAT\",7],[\"AttackSpeed\",\"FLAT\",20],[\"AttackDamage\",\"ADDITIVE\",221]]}," +
            "{\"k\":401001,\"t\":\"RING\",\"g\":\"COMMON\",\"gg\":\"ACCESSORY\",\"l\":1,\"p\":\"RING\",\"n\":\"ring1\",\"s\":[[\"CriticalChance\",\"FLAT\",5]]}]";
        GearDatabase.LoadGear(gj);
        Check("[fit] gear count 2", GearDatabase.Count == 2, GearDatabase.Count);
        var bow = GearDatabase.ByKey(301041);
        Check("[fit] bow type/slot", bow != null && bow.Type == "BOW" && bow.Slot == "MAIN_WEAPON", bow != null ? bow.Type : "null");
        Check("[fit] bow 3 stats", bow != null && bow.Stats.Count == 3, bow != null ? bow.Stats.Count : -1);
        Check("[fit] bySlot MAIN_WEAPON=1", GearDatabase.BySlot("MAIN_WEAPON").Count == 1, GearDatabase.BySlot("MAIN_WEAPON").Count);
        Check("[fit] bySlot RING=1", GearDatabase.BySlot("RING").Count == 1, GearDatabase.BySlot("RING").Count);

        // material catalog: matKey -> type + per-gear-group effect
        string mj = "{\"110001\":{\"t\":\"D\",\"n\":\"mat1\",\"w\":[\"AttackDamage\",\"FLAT\",10,10,2],\"a\":[\"Armor\",\"FLAT\",5,5,1]}}";
        MatCatalog.Load(mj);
        var sm = MatCatalog.Get(110001);
        Check("[fit] mat type D", sm != null && sm.Type == 'D', sm != null ? sm.Type.ToString() : "null");
        Check("[fit] mat weapon effect AttackDamage+10", sm != null && sm.HasW && sm.W.Stat == "AttackDamage" && Near(sm.W.Value, 10), sm != null && sm.HasW ? sm.W.Value : -1);
        Check("[fit] mat weapon tier 2", sm != null && sm.WTier == 2, sm != null ? sm.WTier : -1);
        Check("[fit] mat no accessory effect", sm != null && !sm.HasFor("ACCESSORY"), sm != null ? sm.HasFor("ACCESSORY") : true);
        Check("[fit] catalog byType D = 1", MatCatalog.ByType('D').Count == 1, MatCatalog.ByType('D').Count);

        // socket counts per grade
        SocketDb.Load("{\"BEYOND\":[2,2,1],\"COMMON\":[0,0,0]}");
        var bc = SocketDb.Counts("BEYOND");
        Check("[fit] BEYOND sockets 2/2/1", bc[0] == 2 && bc[1] == 2 && bc[2] == 1, string.Join(",", bc));

        // aggregation: percent mods are ×10, so ADDITIVE 221 = 22.1% -> 7 × (1 + 221/1000) = 8.547 ; AttackSpeed FLAT 20
        var agg = StatAggregator.Aggregate(bow.Stats);
        Check("[fit] agg AttackDamage = 7×1.221 = 8.547", Near(agg["AttackDamage"], 8.547, 0.01), agg.ContainsKey("AttackDamage") ? agg["AttackDamage"] : -1);
        Check("[fit] agg AttackSpeed = 20", Near(agg["AttackSpeed"], 20), agg.ContainsKey("AttackSpeed") ? agg["AttackSpeed"] : -1);
        var agg2 = StatAggregator.Aggregate(new System.Collections.Generic.List<GearStat> { new GearStat("AttackDamage", "FLAT", 10), new GearStat("AttackDamage", "MULTIPLICATIVE", 50) });
        Check("[fit] agg mult 10×1.05 = 10.5", Near(agg2["AttackDamage"], 10.5), agg2.ContainsKey("AttackDamage") ? agg2["AttackDamage"] : -1);

        // socket folds into loadout stats: bow (slot 0) + weapon-deco AttackDamage+10 (FLAT) → (7+10)×1.221
        var gear = new int[] { 301041, 0, 0, 0, 0, 0, 0, 0, 0, 0 };
        var socks = new System.Collections.Generic.Dictionary<int, int[]> { { 0, new int[] { 110001 } } };
        var aggS = FitCalc.LoadoutStats(gear, socks);
        Check("[fit] socket effect folds in (17×1.221=20.757)", aggS.ContainsKey("AttackDamage") && Near(aggS["AttackDamage"], 20.757, 0.01), aggS.ContainsKey("AttackDamage") ? aggS["AttackDamage"] : -1);

        // flat/percent split: a percent-only stat (no flat base, the 範圍 case) keeps its factor instead of
        // collapsing to 0. AreaOfEffect ADDITIVE 189 -> flat 0, pct 1.189 ; with live base 2.9 -> 2.9×1.189 = 3.448
        var fpFlat = new System.Collections.Generic.Dictionary<string, double>();
        var fpPct = new System.Collections.Generic.Dictionary<string, double>();
        StatAggregator.AggregateFP(new System.Collections.Generic.List<GearStat> { new GearStat("AreaOfEffect", "ADDITIVE", 189) }, fpFlat, fpPct);
        Check("[fit] FP percent-only flat = 0", fpFlat.ContainsKey("AreaOfEffect") && Near(fpFlat["AreaOfEffect"], 0), fpFlat.ContainsKey("AreaOfEffect") ? fpFlat["AreaOfEffect"] : -1);
        Check("[fit] FP percent-only pct = 1.189", fpPct.ContainsKey("AreaOfEffect") && Near(fpPct["AreaOfEffect"], 1.189, 0.001), fpPct.ContainsKey("AreaOfEffect") ? fpPct["AreaOfEffect"] : -1);
        // collapsed Aggregate would lose it (0 × 1.189 = 0) — this is the bug the FP split fixes
        var collapsed = StatAggregator.Aggregate(new System.Collections.Generic.List<GearStat> { new GearStat("AreaOfEffect", "ADDITIVE", 189) });
        Check("[fit] collapsed loses percent-only (=0)", Near(collapsed.ContainsKey("AreaOfEffect") ? collapsed["AreaOfEffect"] : 0, 0), collapsed.ContainsKey("AreaOfEffect") ? collapsed["AreaOfEffect"] : -1);
    }

    // ================= RunRetention (per-stage history) =================
    static void RunRetentionTests()
    {
        Console.WriteLine("\n-- RunRetention --");
        // two stages farmed in alternation; per-stage cap must NOT let one stage evict the other's history
        var chrono = new System.Collections.Generic.List<(string, string)>();
        for (int i = 0; i < 50; i++) { chrono.Add(("A" + i, "3-6")); chrono.Add(("B" + i, "4-1")); }
        var del = RunRetention.SelectExpired(chrono, 30, 1000);
        var dset = new System.Collections.Generic.HashSet<string>(del);
        Check("[store] deletes oldest 20 of each stage (40)", del.Count == 40, del.Count);
        Check("[store] keeps newest of stage A (A49)", !dset.Contains("A49"), "A49 deleted");
        Check("[store] keeps newest of stage B (B49)", !dset.Contains("B49"), "B49 deleted");
        Check("[store] per-stage: A20/A30 survive despite B volume", !dset.Contains("A20") && !dset.Contains("A30"), "A recent deleted");
        Check("[store] deletes A0 (oldest beyond cap)", dset.Contains("A0"), "A0 kept");

        // under cap -> nothing deleted
        var few = new System.Collections.Generic.List<(string, string)> { ("a", "3-6"), ("b", "3-6"), ("c", "4-1") };
        Check("[store] under cap deletes nothing", RunRetention.SelectExpired(few, 30, 1000).Count == 0, RunRetention.SelectExpired(few, 30, 1000).Count);

        // global ceiling trims the globally-oldest survivors (5 stages x 30 = 150, cap 100 -> 100 kept)
        var many = new System.Collections.Generic.List<(string, string)>();
        for (int s = 0; s < 5; s++) for (int i = 0; i < 30; i++) many.Add(("s" + s + "_" + i, "stage" + s));
        var delG = RunRetention.SelectExpired(many, 30, 100);
        Check("[store] global ceiling keeps exactly 100", many.Count - delG.Count == 100, many.Count - delG.Count);
    }

    // ================= WavDecoder =================
    // Build a one-channel WAV with the given container so we can prove the decoder handles the
    // real-world formats users actually export (16/24/32-bit PCM, 32-bit float, and the
    // WAVE_FORMAT_EXTENSIBLE wrapper many DAWs emit).
    static byte[] BuildWav(int bits, int fmtCode, bool extensible, float[] s, int channels = 1)
    {
        var data = new System.IO.MemoryStream();
        var dw = new System.IO.BinaryWriter(data);
        foreach (var x in s)
        {
            if (bits == 8) dw.Write((byte)Math.Max(0, Math.Min(255, (int)(x * 127) + 128)));   // unsigned 8-bit
            else if (bits == 16) dw.Write((short)Math.Max(-32768, Math.Min(32767, (int)(x * 32767))));
            else if (bits == 24)
            {
                int v = Math.Max(-8388608, Math.Min(8388607, (int)(x * 8388607)));
                dw.Write((byte)(v & 0xFF)); dw.Write((byte)((v >> 8) & 0xFF)); dw.Write((byte)((v >> 16) & 0xFF));
            }
            else if (bits == 32 && fmtCode == 3) dw.Write(x);                         // IEEE float
            else if (bits == 32 && fmtCode == 1) dw.Write((int)(x * 2147483647.0));   // 32-bit PCM int
        }
        byte[] dataBytes = data.ToArray();
        int ch = channels, rate = 44100, blockAlign = ch * bits / 8, byteRate = rate * blockAlign;

        var fmt = new System.IO.MemoryStream();
        var fw = new System.IO.BinaryWriter(fmt);
        if (extensible)
        {
            fw.Write((ushort)0xFFFE); fw.Write((ushort)ch); fw.Write((uint)rate); fw.Write((uint)byteRate);
            fw.Write((ushort)blockAlign); fw.Write((ushort)bits);
            fw.Write((ushort)22); fw.Write((ushort)bits); fw.Write((uint)0);          // cbSize, validBits, channelMask
            fw.Write((ushort)fmtCode);                                                // SubFormat: real tag
            fw.Write(new byte[] { 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 });
        }
        else
        {
            fw.Write((ushort)fmtCode); fw.Write((ushort)ch); fw.Write((uint)rate); fw.Write((uint)byteRate);
            fw.Write((ushort)blockAlign); fw.Write((ushort)bits);
        }
        byte[] fmtBytes = fmt.ToArray();

        var ms = new System.IO.MemoryStream();
        var w = new System.IO.BinaryWriter(ms);
        w.Write(new byte[] { (byte)'R', (byte)'I', (byte)'F', (byte)'F' });
        w.Write((uint)(4 + (8 + fmtBytes.Length) + (8 + dataBytes.Length)));
        w.Write(new byte[] { (byte)'W', (byte)'A', (byte)'V', (byte)'E' });
        w.Write(new byte[] { (byte)'f', (byte)'m', (byte)'t', (byte)' ' }); w.Write((uint)fmtBytes.Length); w.Write(fmtBytes);
        w.Write(new byte[] { (byte)'d', (byte)'a', (byte)'t', (byte)'a' }); w.Write((uint)dataBytes.Length); w.Write(dataBytes);
        return ms.ToArray();
    }

    static double Rms(float[] dec, float[] src)
    {
        if (dec == null || dec.Length == 0) return 1.0;
        int n = Math.Min(dec.Length, src.Length); double e = 0;
        for (int i = 0; i < n; i++) { double d = dec[i] - src[i]; e += d * d; }
        return Math.Sqrt(e / n);
    }

    static void WavDecoderTests()
    {
        Console.WriteLine("\n-- WavDecoder --");
        int N = 2048;
        var sine = new float[N];
        for (int i = 0; i < N; i++) sine[i] = 0.9f * (float)Math.Sin(2 * Math.PI * 440 * i / 44100);

        // every format a user could realistically feed the box-pickup sound must round-trip cleanly
        void Ok(string name, int bits, int fmt, bool ext)
        {
            var dec = WavDecoder.Decode(BuildWav(bits, fmt, ext, sine), out _, out _, out var err);
            double rms = Rms(dec, sine);
            Check("[wav] " + name + " decodes correctly", dec != null && rms < 0.02, dec == null ? ("null: " + err) : ("rms=" + rms.ToString("0.000")));
        }
        Ok("16-bit PCM", 16, 1, false);
        Ok("24-bit PCM", 24, 1, false);                  // was SILENT before the fix
        Ok("32-bit float", 32, 3, false);
        Ok("32-bit float EXTENSIBLE", 32, 3, true);       // was NOISE before the fix
        Ok("16-bit PCM EXTENSIBLE", 16, 1, true);
        Ok("24-bit PCM EXTENSIBLE", 24, 1, true);         // was SILENT before the fix
        Ok("32-bit PCM int", 32, 1, false);

        // unsupported / non-WAV input must be REJECTED (null + reason), not silently mis-decoded
        var notwav = WavDecoder.Decode(new byte[] { (byte)'I', (byte)'D', (byte)'3', 4, 0, 0, 0, 0, 1, 2, 3, 4, 5, 6, 7, 8 }, out _, out _, out var e1);
        Check("[wav] non-WAV rejected with reason", notwav == null && !string.IsNullOrEmpty(e1), e1);

        // a compressed codec (e.g. ADPCM/MP3-in-WAV) must be rejected, not read as garbage PCM
        var adpcm = BuildWav(16, 2, false, sine);         // claim format tag 2 (ADPCM)
        var decAd = WavDecoder.Decode(adpcm, out _, out _, out var e2);
        Check("[wav] compressed codec rejected", decAd == null && !string.IsNullOrEmpty(e2), decAd == null ? e2 : "decoded garbage");

        // a real decoded clip must not be empty
        var d16 = WavDecoder.Decode(BuildWav(16, 1, false, sine), out _, out _, out _);
        Check("[wav] decoded sample count matches", d16 != null && d16.Length == N, d16 == null ? "null" : d16.Length.ToString());

        // ---- deep checks: numeric edge cases & malformed input ----

        // 24-bit must sign-extend negatives (the branch that used to be all-zero silence)
        var d24n = WavDecoder.Decode(BuildWav(24, 1, false, new float[] { -0.5f, -0.5f, -0.5f, -0.5f }), out _, out _, out _);
        Check("[wav] 24-bit sign-extends negatives", d24n != null && d24n[0] < -0.45f && d24n[0] > -0.55f, d24n == null ? "null" : d24n[0].ToString("0.000"));

        // full-scale extremes stay clamped in [-1,1]
        var d24e = WavDecoder.Decode(BuildWav(24, 1, false, new float[] { 1.0f, -1.0f, 1.0f, -1.0f }), out _, out _, out _);
        Check("[wav] 24-bit extremes in range", d24e != null && d24e[0] <= 1.0001f && d24e[1] >= -1.0001f, d24e == null ? "null" : (d24e[0] + "/" + d24e[1]));

        // stereo: channel count + L/R interleaving preserved (frames = total/channels in BoxSound)
        var dst2 = WavDecoder.Decode(BuildWav(16, 1, false, new float[] { 0.3f, -0.7f, 0.3f, -0.7f }, 2), out int sch, out _, out _);
        Check("[wav] stereo channels=2", sch == 2, sch);
        Check("[wav] stereo interleave L/R", dst2 != null && dst2.Length == 4 && dst2[0] > 0.25f && dst2[1] < -0.65f, dst2 == null ? "null" : (dst2[0] + "/" + dst2[1]));

        // 8-bit unsigned PCM round-trips (coarse, so looser tolerance)
        var d8 = WavDecoder.Decode(BuildWav(8, 1, false, sine), out _, out _, out _);
        Check("[wav] 8-bit decodes", d8 != null && Rms(d8, sine) < 0.05, d8 == null ? "null" : Rms(d8, sine).ToString("0.000"));

        // a legit all-silence file must NOT be rejected — zeros are valid audio
        var dsil = WavDecoder.Decode(BuildWav(16, 1, false, new float[64]), out _, out _, out var esil);
        Check("[wav] silence not rejected", dsil != null && esil == null, esil);

        // malformed: a "fmt " id positioned so its body runs past the buffer must reject, not throw/over-read
        var bad = new byte[44];
        bad[0] = (byte)'R'; bad[1] = (byte)'I'; bad[2] = (byte)'F'; bad[3] = (byte)'F';
        bad[8] = (byte)'W'; bad[9] = (byte)'A'; bad[10] = (byte)'V'; bad[11] = (byte)'E';
        bad[12] = (byte)'J'; bad[13] = (byte)'U'; bad[14] = (byte)'N'; bad[15] = (byte)'K'; bad[16] = 16;   // JUNK chunk, sz=16 -> p advances to 36
        bad[36] = (byte)'f'; bad[37] = (byte)'m'; bad[38] = (byte)'t'; bad[39] = (byte)' ';                 // fmt id at 36; body would read past 44
        bool threw = false; float[] dtr = null; string etr = null;
        try { dtr = WavDecoder.Decode(bad, out _, out _, out etr); } catch (Exception ex) { threw = true; etr = ex.GetType().Name; }
        Check("[wav] truncated fmt rejected without throwing", !threw && dtr == null, threw ? "THREW " + etr : (dtr == null ? "ok" : "decoded"));

        // malformed: a corrupt (huge) chunk size must not run the walker off the end
        var bad2 = new byte[64];
        bad2[0] = (byte)'R'; bad2[1] = (byte)'I'; bad2[2] = (byte)'F'; bad2[3] = (byte)'F';
        bad2[8] = (byte)'W'; bad2[9] = (byte)'A'; bad2[10] = (byte)'V'; bad2[11] = (byte)'E';
        bad2[12] = (byte)'x'; bad2[13] = (byte)'x'; bad2[14] = (byte)'x'; bad2[15] = (byte)'x';
        bad2[16] = 0xFF; bad2[17] = 0xFF; bad2[18] = 0xFF; bad2[19] = 0x7F;   // size ~2GB
        bool threw2 = false;
        try { WavDecoder.Decode(bad2, out _, out _, out _); } catch { threw2 = true; }
        Check("[wav] corrupt chunk size handled without throwing", !threw2, threw2 ? "THREW" : "ok");
    }
}
