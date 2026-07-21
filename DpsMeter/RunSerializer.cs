using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace TbhDpsMeter
{
    /// <summary>Pure (no Unity / no BepInEx) text serialization for RunRecord, so it can be unit-tested.
    /// Format is line-based "key=value". version=2 adds stage-compare fields; version 1 / missing loads fine.</summary>
    public static class RunSerializer
    {
        private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
        // field separator inside a single value (gear/skill rows); chosen to not appear in names
        private const char FS = '\t';

        public static string Serialize(RunRecord r)
        {
            var sb = new StringBuilder();
            sb.Append("version=2\n");
            sb.Append("title=").Append(Clean(r.Title)).Append('\n');
            sb.Append("stageid=").Append(Clean(r.StageId)).Append('\n');
            sb.Append("total=").Append(r.Total.ToString(Inv)).Append('\n');
            sb.Append("duration=").Append(r.Duration.ToString(Inv)).Append('\n');
            sb.Append("peak=").Append(r.Peak.ToString(Inv)).Append('\n');
            sb.Append("avg=").Append(r.Avg.ToString(Inv)).Append('\n');
            sb.Append("crit=").Append(r.CritRate.ToString(Inv)).Append('\n');
            sb.Append("critshare=").Append(r.CritShare.ToString(Inv)).Append('\n');
            sb.Append("waves=").Append(r.Waves).Append('\n');
            sb.Append("active=").Append(r.ActiveSeconds.ToString(Inv)).Append('\n');
            sb.Append("idle=").Append(r.IdleSeconds.ToString(Inv)).Append('\n');
            sb.Append("gold=").Append(r.GoldGained.ToString(Inv)).Append('\n');
            sb.Append("exp=").Append(r.ExpGained.ToString(Inv)).Append('\n');
            foreach (var b in r.Boxes)
                sb.Append("box=").Append(Clean(b.Type)).Append(':').Append(b.Count).Append('\n');

            for (int i = 0; i < r.TypeFlags.Count; i++)
                sb.Append("type=").Append(r.TypeFlags[i]).Append(':').Append(r.TypeAmounts[i].ToString(Inv)).Append('\n');

            if (r.WaveDurations.Count > 0)
            {
                sb.Append("wavedur=");
                for (int i = 0; i < r.WaveDurations.Count; i++)
                {
                    if (i > 0) sb.Append(',');
                    sb.Append(r.WaveDurations[i].ToString("0.###", Inv));
                }
                sb.Append('\n');
            }

            var hs = new StringBuilder();
            foreach (var s in r.Samples)
            {
                if (hs.Length > 0) hs.Append(',');
                hs.Append(s.Dps.ToString("0.#", Inv)).Append(':').Append(s.Wave);
            }
            sb.Append("hist=").Append(hs).Append('\n');

            // damage-taken side
            sb.Append("taken_total=").Append(r.TakenTotal.ToString(Inv)).Append('\n');
            sb.Append("taken_peak=").Append(r.TakenPeak.ToString(Inv)).Append('\n');
            sb.Append("taken_avg=").Append(r.TakenAvg.ToString(Inv)).Append('\n');
            sb.Append("taken_biggest=").Append(r.TakenBiggestHit.ToString(Inv)).Append('\n');
            sb.Append("taken_crit=").Append(r.TakenCritRate.ToString(Inv)).Append('\n');
            sb.Append("taken_hits=").Append(r.TakenHits).Append('\n');
            for (int i = 0; i < r.TakenAttrValues.Count; i++)
                sb.Append("taken_attr=").Append(r.TakenAttrValues[i]).Append(':').Append(r.TakenAttrAmounts[i].ToString(Inv)).Append('\n');
            for (int i = 0; i < r.TakenTypeFlags.Count; i++)
                sb.Append("taken_type=").Append(r.TakenTypeFlags[i]).Append(':').Append(r.TakenTypeAmounts[i].ToString(Inv)).Append('\n');

            var ths = new StringBuilder();
            foreach (var s in r.TakenSamples)
            {
                if (ths.Length > 0) ths.Append(',');
                ths.Append(s.Dps.ToString("0.#", Inv)).Append(':').Append(s.Wave);
            }
            sb.Append("taken_hist=").Append(ths).Append('\n');

            // character snapshots (whole party); each starts with a "char=" delimiter line
            foreach (var snap in r.Party)
            {
                if (snap == null || (!snap.Captured && !snap.HasAny)) continue;
                sb.Append("char=").Append(Clean(snap.Character)).Append(FS).Append(Clean(snap.CharacterName)).Append('\n');
                if (snap.ExpGained != 0) sb.Append("cexp=").Append(snap.ExpGained.ToString(Inv)).Append('\n');
                if (snap.Level != 0) sb.Append("clevel=").Append(snap.Level).Append('\n');
                if (snap.DamageDealt != 0) sb.Append("cdmg=").Append(snap.DamageDealt.ToString(Inv)).Append('\n');
                foreach (var st in snap.Stats)
                    sb.Append("stat=").Append(Clean(st.Key)).Append(':').Append(st.Value.ToString(Inv)).Append('\n');
                foreach (var g in snap.Equipment)
                {
                    sb.Append("gear=").Append(Clean(g.Slot)).Append(FS).Append(Clean(g.Name));
                    foreach (var a in g.Affixes)
                        sb.Append(FS).Append(Clean(a.Name)).Append('=').Append(a.Value.ToString(Inv));
                    sb.Append('\n');
                }
                foreach (var sk in snap.Skills)
                    sb.Append("skill=").Append(Clean(sk.Name)).Append(FS).Append(sk.Level).Append(FS).Append(sk.Key).Append('\n');
            }

            return sb.ToString();
        }

        public static RunRecord Deserialize(IEnumerable<string> lines)
        {
            var r = new RunRecord();
            CharacterSnapshot snap = null;
            CharacterSnapshot Snap()
            {
                if (snap == null) { snap = new CharacterSnapshot { Captured = true }; r.Party.Add(snap); }
                return snap;
            }
            void NewChar(string v)
            {
                int tab = v.IndexOf(FS);
                string id = tab >= 0 ? v.Substring(0, tab) : v;
                string name = tab >= 0 ? v.Substring(tab + 1) : "";
                snap = new CharacterSnapshot { Captured = true, Character = id, CharacterName = name };
                r.Party.Add(snap);
            }

            foreach (var raw in lines)
            {
                var line = raw == null ? "" : raw.TrimEnd('\r');
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq);
                string v = line.Substring(eq + 1);
                switch (k)
                {
                    case "version": break;
                    case "title": r.Title = v; break;
                    case "stageid": r.StageId = v; break;
                    case "total": r.Total = D(v); break;
                    case "duration": r.Duration = F(v); break;
                    case "peak": r.Peak = F(v); break;
                    case "avg": r.Avg = F(v); break;
                    case "crit": r.CritRate = F(v); break;
                    case "critshare": r.CritShare = F(v); break;
                    case "waves": r.Waves = (int)D(v); break;
                    case "active": r.ActiveSeconds = F(v); break;
                    case "idle": r.IdleSeconds = F(v); break;
                    case "gold": r.GoldGained = D(v); break;
                    case "exp": r.ExpGained = D(v); break;
                    case "box":
                    {
                        int c = v.LastIndexOf(':');
                        if (c > 0) r.Boxes.Add(new BoxDrop(v.Substring(0, c), (int)D(v.Substring(c + 1))));
                        break;
                    }
                    case "cexp": Snap().ExpGained = D(v); break;
                    case "clevel": Snap().Level = (int)D(v); break;
                    case "cdmg": Snap().DamageDealt = D(v); break;
                    case "type":
                    {
                        int c = v.IndexOf(':');
                        if (c > 0) { r.TypeFlags.Add((int)D(v.Substring(0, c))); r.TypeAmounts.Add(D(v.Substring(c + 1))); }
                        break;
                    }
                    case "wavedur":
                        if (v.Length > 0)
                            foreach (var p in v.Split(','))
                                r.WaveDurations.Add(F(p));
                        break;
                    case "hist":
                        if (v.Length > 0)
                            foreach (var p in v.Split(','))
                            {
                                int cc = p.IndexOf(':');
                                if (cc > 0) r.Samples.Add(new Sample { Dps = F(p.Substring(0, cc)), Wave = (int)D(p.Substring(cc + 1)) });
                            }
                        break;
                    case "taken_total": r.TakenTotal = D(v); break;
                    case "taken_peak": r.TakenPeak = F(v); break;
                    case "taken_avg": r.TakenAvg = F(v); break;
                    case "taken_biggest": r.TakenBiggestHit = F(v); break;
                    case "taken_crit": r.TakenCritRate = F(v); break;
                    case "taken_hits": r.TakenHits = (long)D(v); break;
                    case "taken_attr":
                    {
                        int ca = v.IndexOf(':');
                        if (ca > 0) { r.TakenAttrValues.Add((int)D(v.Substring(0, ca))); r.TakenAttrAmounts.Add(D(v.Substring(ca + 1))); }
                        break;
                    }
                    case "taken_type":
                    {
                        int ct = v.IndexOf(':');
                        if (ct > 0) { r.TakenTypeFlags.Add((int)D(v.Substring(0, ct))); r.TakenTypeAmounts.Add(D(v.Substring(ct + 1))); }
                        break;
                    }
                    case "taken_hist":
                        if (v.Length > 0)
                            foreach (var p in v.Split(','))
                            {
                                int cc = p.IndexOf(':');
                                if (cc > 0) r.TakenSamples.Add(new Sample { Dps = F(p.Substring(0, cc)), Wave = (int)D(p.Substring(cc + 1)) });
                            }
                        break;
                    case "char": NewChar(v); break;
                    case "snap": Snap(); break;   // legacy v2 single-character marker
                    case "stat":
                    {
                        int c = v.IndexOf(':');
                        if (c > 0) Snap().Stats.Add(new StatEntry(v.Substring(0, c), D(v.Substring(c + 1))));
                        break;
                    }
                    case "gear":
                    {
                        var parts = v.Split(FS);
                        if (parts.Length >= 1)
                        {
                            var g = new GearItem { Slot = parts.Length > 0 ? parts[0] : "", Name = parts.Length > 1 ? parts[1] : "" };
                            for (int i = 2; i < parts.Length; i++)
                            {
                                int e = parts[i].LastIndexOf('=');
                                if (e > 0) g.Affixes.Add(new Affix(parts[i].Substring(0, e), D(parts[i].Substring(e + 1))));
                            }
                            Snap().Equipment.Add(g);
                        }
                        break;
                    }
                    case "skill":
                    {
                        var parts = v.Split(FS);
                        if (parts.Length >= 1)
                            Snap().Skills.Add(new SkillEntry(parts[0],
                                parts.Length > 1 ? (int)D(parts[1]) : 0,
                                parts.Length > 2 ? (int)D(parts[2]) : 0));
                        break;
                    }
                }
            }

            return r;
        }

        private static string Clean(string s) => string.IsNullOrEmpty(s) ? "" : s.Replace('\t', ' ').Replace('\n', ' ').Replace('\r', ' ');
        private static float F(string s) { float.TryParse(s, NumberStyles.Any, Inv, out var f); return f; }
        private static double D(string s) { double.TryParse(s, NumberStyles.Any, Inv, out var d); return d; }
    }
}
