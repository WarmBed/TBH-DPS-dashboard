using System;
using System.Collections.Generic;
using System.Text;

namespace TbhDpsMeter
{
    /// <summary>
    /// Pure, Unity-independent DPS accumulator. All time values are seconds
    /// supplied by the caller (so it can be unit-tested with a fake clock).
    /// Single-threaded: every method is expected to be called from the game's
    /// main thread (Harmony hooks + OnGUI both run there).
    /// </summary>
    public sealed class DpsTracker
    {
        // EDamageType values from the game (powers of two).
        public static readonly (int flag, string name)[] TypeOrder = new[]
        {
            (1,  "Melee"),
            (2,  "Projectile"),
            (4,  "AOE"),
            (8,  "Summon"),
            (16, "DOT"),
            (32, "Trap"),
        };

        private readonly float _windowSeconds;
        // each windowed event carries the dealing character's key (ck) for per-character live DPS.
        private readonly Queue<(float t, float dmg, int ck)> _window = new Queue<(float, float, int)>();
        private readonly Dictionary<int, double> _byChar = new Dictionary<int, double>();

        private float _startTime;
        private float _lastDamageTime;
        private bool _active;

        private double _total;
        private long _hits;
        private long _crits;
        private double _critDamage;
        private float _peakDps;
        private readonly Dictionary<int, double> _byType = new Dictionary<int, double>();

        public DpsTracker(float windowSeconds = 5f)
        {
            _windowSeconds = windowSeconds <= 0 ? 5f : windowSeconds;
        }

        /// <summary>True once StartEncounter has been called and not yet reset to empty.</summary>
        public bool HasData => _hits > 0;

        public float LastDamageTime => _lastDamageTime;

        /// <summary>Begin a fresh encounter; clears all accumulated stats.</summary>
        public void StartEncounter(float now)
        {
            _window.Clear();
            _byType.Clear();
            _byChar.Clear();
            _startTime = now;
            _lastDamageTime = now;
            _active = true;
            _total = 0;
            _hits = 0;
            _crits = 0;
            _critDamage = 0;
            _peakDps = 0;
        }

        /// <summary>Stop accumulating but keep the numbers for display.</summary>
        public void EndEncounter() => _active = false;

        /// <summary>Record one damage instance dealt by the player side. <paramref name="charKey"/> is the
        /// dealing hero's key (0 = unknown), used for the per-character breakdown.</summary>
        public void Record(float amount, bool isCritical, int damageTypeFlag, float now, int charKey = 0)
        {
            if (amount <= 0) return;
            if (!_active)
            {
                // auto-start an encounter if damage arrives without an explicit boundary
                StartEncounter(now);
            }

            _total += amount;
            _hits++;
            if (isCritical) { _crits++; _critDamage += amount; }

            if (_byType.TryGetValue(damageTypeFlag, out var cur)) _byType[damageTypeFlag] = cur + amount;
            else _byType[damageTypeFlag] = amount;

            if (_byChar.TryGetValue(charKey, out var cc)) _byChar[charKey] = cc + amount;
            else _byChar[charKey] = amount;

            _lastDamageTime = now;
            _window.Enqueue((now, amount, charKey));
            Trim(now);

            float live = LiveDps(now);
            if (live > _peakDps) _peakDps = live;
        }

        private void Trim(float now)
        {
            float cutoff = now - _windowSeconds;
            while (_window.Count > 0 && _window.Peek().t < cutoff)
                _window.Dequeue();
        }

        /// <summary>Instantaneous DPS over the trailing sliding window.</summary>
        public float LiveDps(float now)
        {
            Trim(now);
            double sum = 0;
            foreach (var e in _window) sum += e.dmg;
            float elapsed = now - _startTime;
            float divisor = elapsed < _windowSeconds ? elapsed : _windowSeconds;
            // Floor at 1s so the first hits of an encounter (elapsed ~ 0) don't
            // divide by a near-zero number and produce an absurd peak spike.
            if (divisor < 1f) divisor = 1f;
            return (float)(sum / divisor);
        }

        public Snapshot GetSnapshot(float now)
        {
            float duration = _hits > 0 ? Math.Max(0f, _lastDamageTime - _startTime) : 0f;
            float avg = duration > 0.001f ? (float)(_total / duration) : 0f;

            var parts = new List<TypePart>();
            if (_total > 0)
            {
                foreach (var kv in _byType)
                {
                    if (kv.Value <= 0) continue;
                    parts.Add(new TypePart
                    {
                        Flag = kv.Key,
                        Name = DecodeName(kv.Key),
                        Amount = kv.Value,
                        Share = (float)(kv.Value / _total),
                    });
                }
                parts.Sort((x, y) => y.Amount.CompareTo(x.Amount)); // largest share first
            }

            // per-character rollup: total + share, plus live DPS from the windowed events (same divisor as LiveDps).
            var chars = new List<CharPart>();
            if (_total > 0 && _byChar.Count > 0)
            {
                Trim(now);
                var winByChar = new Dictionary<int, double>();
                foreach (var e in _window)
                    winByChar[e.ck] = (winByChar.TryGetValue(e.ck, out var w) ? w : 0) + e.dmg;
                float elapsed = now - _startTime;
                float divisor = elapsed < _windowSeconds ? elapsed : _windowSeconds;
                if (divisor < 1f) divisor = 1f;
                foreach (var kv in _byChar)
                {
                    if (kv.Value <= 0) continue;
                    winByChar.TryGetValue(kv.Key, out var win);
                    chars.Add(new CharPart
                    {
                        CharKey = kv.Key,
                        Amount = kv.Value,
                        Share = (float)(kv.Value / _total),
                        Dps = (float)(win / divisor),
                    });
                }
                chars.Sort((x, y) => y.Amount.CompareTo(x.Amount));
            }

            return new Snapshot
            {
                Active = _active,
                LiveDps = LiveDps(now),
                PeakDps = _peakDps,
                AvgDps = avg,
                Total = _total,
                DurationSeconds = duration,
                Hits = _hits,
                CritRate = _hits > 0 ? (float)_crits / _hits : 0f,
                CritDamageShare = _total > 0 ? (float)(_critDamage / _total) : 0f,
                ByType = parts,
                ByChar = chars,
            };
        }

        /// <summary>Human-readable English name for a (possibly combined) EDamageType flag value.</summary>
        public static string DecodeName(int flag)
        {
            if (flag == 0) return "None";
            // exact single-flag match
            foreach (var (f, name) in TypeOrder) if (f == flag) return name;
            // combined flags -> join components
            var sb = new StringBuilder();
            foreach (var (f, name) in TypeOrder)
            {
                if ((flag & f) != 0)
                {
                    if (sb.Length > 0) sb.Append('+');
                    sb.Append(name);
                }
            }
            return sb.Length > 0 ? sb.ToString() : ("Type" + flag);
        }

        public struct TypePart
        {
            public int Flag;
            public string Name;
            public double Amount;
            public float Share;
        }

        /// <summary>Per-character damage rollup: total, share of run, and live (windowed) DPS.</summary>
        public struct CharPart
        {
            public int CharKey;
            public double Amount;
            public float Share;
            public float Dps;
        }

        public struct Snapshot
        {
            public bool Active;
            public float LiveDps;
            public float PeakDps;
            public float AvgDps;
            public double Total;
            public float DurationSeconds;
            public long Hits;
            public float CritRate;
            public float CritDamageShare;
            public List<TypePart> ByType;
            public List<CharPart> ByChar;
        }
    }
}
