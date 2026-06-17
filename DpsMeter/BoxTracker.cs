using System;
using System.Collections.Generic;
using Il2CppInterop.Runtime;
using TaskbarHero;

namespace TbhDpsMeter
{
    /// <summary>Detects box pickups by subscribing to the game's own StageManager.OnGetBox event
    /// (Action&lt;int&gt; — a READABLE, non-obfuscated name, so it survives game updates). Records the
    /// stage + wall-clock time + box type, beeps, and keeps a session log for the F5 panel.</summary>
    public static class BoxTracker
    {
        public static readonly List<BoxEvent> Events = new List<BoxEvent>();
        public static int Version;   // bumped on each new event so the F5 panel can refresh

        private static StageManager _sm;
        private static bool _subscribed;
        private static Il2CppSystem.Action<int> _handler;

        /// <summary>Subscribe our handler to the current StageManager's OnGetBox (once per instance).
        /// Call every frame with the polled StageManager; cheap no-op when already subscribed.</summary>
        public static void Tick(StageManager sm)
        {
            if (sm == null) return;
            if (ReferenceEquals(sm, _sm) && _subscribed) return;
            try
            {
                if (_handler == null)
                    _handler = DelegateSupport.ConvertDelegate<Il2CppSystem.Action<int>>((System.Action<int>)OnGetBox);
                var combined = Il2CppSystem.Delegate.Combine(sm.OnGetBox, _handler);
                sm.OnGetBox = combined.Cast<Il2CppSystem.Action<int>>();   // IL2CPP-aware cast
                _sm = sm; _subscribed = true;
                Plugin.Logger?.LogInfo("[box] subscribed to StageManager.OnGetBox");
            }
            catch (Exception e) { _subscribed = false; Plugin.Logger?.LogWarning("[box] subscribe failed: " + e.Message); }
        }

        private static void OnGetBox(int arg)
        {
            try
            {
                string stage = "";
                try { stage = CharacterReader.CurrentStageId(); } catch { }
                var ev = new BoxEvent { Stage = stage, Time = DateTime.Now, Arg = arg, Type = DecodeType(arg) };
                Events.Add(ev);   // no cap: keep the full session log (persisted in BoxStore too)
                BoxStore.Append(ev);
                Version++;
                Plugin.Logger?.LogInfo($"[box] GOT BOX arg={arg} type={ev.Type} stage={stage} at {ev.Time:HH:mm:ss}");
                BoxSound.Play();
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[box] handler: " + e.Message); }
        }

        // OnGetBox fires with the box item key (the 910xxx STAGEBOX range); resolve its name from the
        // bundled wiki item table (verified in-game: 910651 -> "Normal Monster Box Lv65").
        private static string DecodeType(int arg)
        {
            string nm = ItemNameStore.Get(arg);
            return string.IsNullOrEmpty(nm) ? ("Box " + arg) : nm;
        }
    }
}
