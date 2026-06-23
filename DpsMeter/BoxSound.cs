using System;
using System.IO;
using System.Runtime.InteropServices;
using Il2CppInterop.Runtime.InteropTypes.Arrays;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>Box-pickup sound for the F5 panel. Plays through a Unity <see cref="AudioSource"/> so the
    /// volume is adjustable (kernel32 Beep cannot do that). The default is a procedurally-synthesized beep
    /// (no file needed); if <c>BoxUI/SoundFile</c> points at a .wav it plays that instead. Volume + on/off
    /// come live from config. Falls back to kernel32 Beep only if the AudioSource never initialized.</summary>
    public static class BoxSound
    {
        private static AudioSource _src;
        private static AudioClip _beep;     // built-in synthesized tone
        private static AudioClip _custom;   // optional user .wav
        private static bool _ready;

        /// <summary>Set by the file picker (background thread); the F5 behaviour applies it on the main thread.</summary>
        public static volatile string PendingCustomPath;

        /// <summary>Why the configured custom .wav couldn't be loaded (shown in the F5 panel), or null when fine.</summary>
        public static volatile string LastError;

        /// <summary>Wire up the AudioSource (call once, on the main thread, from a MonoBehaviour).
        /// Builds the built-in beep and tries to load the custom .wav if one is configured.</summary>
        public static void Init(AudioSource src)
        {
            if (_ready || src == null) return;
            try
            {
                _src = src;
                _src.playOnAwake = false;
                _src.spatialBlend = 0f;          // 2D
                _src.bypassEffects = true;
                _src.bypassListenerEffects = true;
                _src.ignoreListenerVolume = true; // a notification — not affected by the game's master volume
                _src.ignoreListenerPause = true;  // still audible while the game is paused
                _src.volume = 1f;
                _beep = BuildBeep();
                ReloadCustom();
                _ready = true;
                Plugin.Logger?.LogInfo("[box] sound ready (AudioSource)");
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[box] sound init failed: " + e.Message); }
        }

        /// <summary>(Re)load the custom .wav named in config, or clear it when the path is blank/invalid.</summary>
        public static void ReloadCustom()
        {
            _custom = null;
            LastError = null;
            try
            {
                string path = Plugin.BoxSoundFile?.Value;
                if (string.IsNullOrEmpty(path)) return;
                if (!File.Exists(path)) { LastError = "file not found"; Plugin.Logger?.LogWarning("[box] sound file not found: " + path); return; }
                _custom = LoadWav(path);   // sets LastError on a decode failure
                if (_custom != null) Plugin.Logger?.LogInfo("[box] custom sound loaded: " + path);
            }
            catch (Exception e) { LastError = e.Message; Plugin.Logger?.LogWarning("[box] custom sound load failed: " + e.Message); }
        }

        /// <summary>Play the pickup sound at the configured volume (no-op when muted).</summary>
        public static void Play()
        {
            try
            {
                if (Plugin.BoxSoundEnabled != null && !Plugin.BoxSoundEnabled.Value) return;
                float vol = Plugin.BoxSoundVolume != null ? Mathf.Clamp01(Plugin.BoxSoundVolume.Value) : 0.6f;
                if (_ready && _src != null)
                {
                    var clip = _custom != null ? _custom : _beep;
                    if (clip != null)
                    {
                        _src.PlayOneShot(clip, vol);
                        Plugin.Logger?.LogInfo($"[box] play sound (custom={_custom != null}, vol={vol:0.00})");
                        return;
                    }
                }
                Plugin.Logger?.LogWarning("[box] AudioSource not ready — kernel32 beep fallback");
                FallbackBeep();   // AudioSource never came up — last resort
            }
            catch { try { FallbackBeep(); } catch { } }
        }

        // --- built-in tone -------------------------------------------------

        // A clear two-note "ding-dong" chime (E6 then A6) — short, but unmistakable as a notification.
        private static AudioClip BuildBeep()
        {
            const int rate = 44100;
            float[] notes = { 1318.5f, 1760f };   // E6, A6
            const float noteSec = 0.16f;
            int per = (int)(rate * noteSec);
            int n = per * notes.Length;
            var data = new Il2CppStructArray<float>(n);
            int attack = (int)(rate * 0.005f);
            int release = (int)(rate * 0.060f);
            for (int k = 0; k < notes.Length; k++)
            {
                float f = notes[k];
                for (int i = 0; i < per; i++)
                {
                    float env = 1f;
                    if (i < attack) env = i / (float)attack;
                    else if (i > per - release) env = (per - i) / (float)release;
                    // add a soft 2nd harmonic so it carries over game audio
                    float s = Mathf.Sin(2f * Mathf.PI * f * i / rate)
                            + 0.35f * Mathf.Sin(4f * Mathf.PI * f * i / rate);
                    data[k * per + i] = s * 0.5f * env;
                }
            }
            var clip = AudioClip.Create("box_beep", n, 1, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // --- WAV → AudioClip ------------------------------------------------
        // The byte→float decode lives in the Unity-free WavDecoder (unit-tested in TrackerTests for
        // 16/24/32-bit PCM, 32-bit float, and WAVE_FORMAT_EXTENSIBLE); here we only wrap it in a clip.

        private static AudioClip LoadWav(string path)
        {
            byte[] b = File.ReadAllBytes(path);
            float[] samples = WavDecoder.Decode(b, out int channels, out int rate, out string error);
            if (samples == null || samples.Length == 0)
            {
                LastError = error ?? "could not decode WAV";
                Plugin.Logger?.LogWarning("[box] custom sound rejected: " + LastError + " (" + path + ")");
                return null;
            }
            var data = new Il2CppStructArray<float>(samples.Length);
            for (int i = 0; i < samples.Length; i++) data[i] = samples[i];
            int frames = samples.Length / Math.Max(1, channels);
            var clip = AudioClip.Create("box_custom", frames, channels, rate, false);
            clip.SetData(data, 0);
            return clip;
        }

        // --- kernel32 fallback ---------------------------------------------

        [DllImport("kernel32.dll")] private static extern bool Beep(uint dwFreq, uint dwDuration);
        private static void FallbackBeep()
        {
            System.Threading.Tasks.Task.Run(() => { try { Beep(880, 130); } catch { } });
        }
    }
}
