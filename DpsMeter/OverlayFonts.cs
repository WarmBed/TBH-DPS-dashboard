using System;
using System.Collections.Generic;
using System.IO;
using BepInEx;
using BepInEx.Logging;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>Shared font resolver for IMGUI overlays. CrossOver/Wine bottles can have an empty or
    /// stale system-font registry, so the overlays should not depend solely on Unity's default font.</summary>
    internal static class OverlayFonts
    {
        private const int ProbeSize = 16;
        private const string Auto = "Auto";

        private static readonly string[] AutoFamilies =
        {
            "Source Han Sans TC",
            "Source Han Sans",
            "Microsoft JhengHei UI",
            "Microsoft JhengHei",
            "Microsoft YaHei UI",
            "Microsoft YaHei",
            "SimSun",
            "Arial Unicode MS",
            "Segoe UI",
            "Arial",
        };

        private static readonly string[] PreferredFiles =
        {
            "SourceHanSansTC-Regular.otf",
            "SourceHanSansTC-Normal.otf",
            "SourceHanSans-Regular.otf",
            "SourceHanSans-Normal.otf",
            "msjh.ttc",
            "msyh.ttc",
            "simsun.ttc",
            "arialuni.ttf",
            "segoeui.ttf",
            "arial.ttf",
        };

        private static ManualLogSource _log;
        private static Font _activeFont;
        private static bool _loggedDefault;

        public static Font ActiveFont => _activeFont;

        public static void Init(string configuredFamily, string configuredPath, ManualLogSource log)
        {
            _log = log;
            _activeFont = null;
            _loggedDefault = false;

            try
            {
                if (TryConfiguredPath(configuredPath, out _activeFont)) return;
                if (TryConfiguredFamily(configuredFamily, out _activeFont)) return;
                if (TryAutoFamilies(out _activeFont)) return;
                if (TryFontDirectories(out _activeFont)) return;

                LogDefault("No overlay font fallback resolved; using Unity default. If CrossOver text is invisible, install Asian Fonts Component or set UI.FontPath to a .ttf/.otf/.ttc file.");
            }
            catch (Exception ex)
            {
                _activeFont = null;
                LogWarning("Overlay font resolver failed; using Unity default. " + ex.Message);
            }
        }

        public static void Apply(params GUIStyle[] styles)
        {
            if (_activeFont == null) return;
            try { if (GUI.skin != null) GUI.skin.font = _activeFont; } catch { }
            if (styles == null) return;
            for (int i = 0; i < styles.Length; i++)
            {
                try { if (styles[i] != null) styles[i].font = _activeFont; } catch { }
            }
        }

        private static bool TryConfiguredPath(string configuredPath, out Font font)
        {
            font = null;
            string path = Normalize(configuredPath);
            if (string.IsNullOrEmpty(path)) return false;

            foreach (string candidate in ConfiguredPathCandidates(path))
            {
                if (TryFontFile(candidate, "configured path", out font)) return true;
            }

            LogWarning("UI.FontPath was set but no readable font file was found: " + path);
            return false;
        }

        private static bool TryConfiguredFamily(string configuredFamily, out Font font)
        {
            font = null;
            string family = Normalize(configuredFamily);
            if (string.IsNullOrEmpty(family) || Same(family, Auto)) return false;

            string matched = MatchInstalledFamily(family);
            if (TryOsFont(string.IsNullOrEmpty(matched) ? family : matched, "configured family", out font)) return true;

            LogWarning("UI.FontFamily was set but Unity could not load it: " + family);
            return false;
        }

        private static bool TryAutoFamilies(out Font font)
        {
            font = null;
            for (int i = 0; i < AutoFamilies.Length; i++)
            {
                string matched = MatchInstalledFamily(AutoFamilies[i]);
                if (string.IsNullOrEmpty(matched)) continue;
                if (TryOsFont(matched, "installed family", out font)) return true;
            }
            return false;
        }

        private static bool TryFontDirectories(out Font font)
        {
            font = null;
            var dirs = new List<string>();
            try { dirs.Add(Path.Combine(Paths.PluginPath, "fonts")); } catch { }
            dirs.Add(@"C:\windows\Fonts");

            for (int i = 0; i < dirs.Count; i++)
            {
                if (TryPreferredFontFile(dirs[i], out font)) return true;
            }
            return false;
        }

        private static bool TryPreferredFontFile(string dir, out Font font)
        {
            font = null;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir)) return false;

            for (int i = 0; i < PreferredFiles.Length; i++)
            {
                string path = Path.Combine(dir, PreferredFiles[i]);
                if (TryFontFile(path, "font file", out font)) return true;
            }

            try
            {
                foreach (string path in Directory.GetFiles(dir))
                {
                    if (IsFontFile(path) && TryFontFile(path, "font file", out font)) return true;
                }
            }
            catch { }
            return false;
        }

        private static bool TryOsFont(string family, string source, out Font font)
        {
            font = null;
            if (string.IsNullOrEmpty(family)) return false;
            try
            {
                font = Font.CreateDynamicFontFromOSFont(family, ProbeSize);
                if (font == null) return false;
                LogInfo("Overlay font resolved from " + source + ": " + family);
                return true;
            }
            catch
            {
                font = null;
                return false;
            }
        }

        private static bool TryFontFile(string path, string source, out Font font)
        {
            font = null;
            if (string.IsNullOrEmpty(path) || !File.Exists(path) || !IsFontFile(path)) return false;
            try
            {
                font = new Font(path);
                if (font == null) return false;
                LogInfo("Overlay font resolved from " + source + ": " + path);
                return true;
            }
            catch
            {
                font = null;
                return false;
            }
        }

        private static string MatchInstalledFamily(string requested)
        {
            if (string.IsNullOrEmpty(requested)) return null;
            string[] names = InstalledFontNames();
            for (int i = 0; i < names.Length; i++) if (Same(names[i], requested)) return names[i];
            for (int i = 0; i < names.Length; i++) if (StartsWith(names[i], requested)) return names[i];
            for (int i = 0; i < names.Length; i++) if (Contains(names[i], requested)) return names[i];
            return null;
        }

        private static string[] InstalledFontNames()
        {
            var result = new List<string>();
            try
            {
                var names = Font.GetOSInstalledFontNames();
                if (names != null)
                {
                    foreach (string raw in names)
                    {
                        string name = Normalize(raw);
                        if (!string.IsNullOrEmpty(name) && !ContainsExact(result, name)) result.Add(name);
                    }
                }
            }
            catch { }
            return result.ToArray();
        }

        private static IEnumerable<string> ConfiguredPathCandidates(string path)
        {
            yield return path;
            if (Path.IsPathRooted(path)) yield break;

            string pluginPath = null;
            try { pluginPath = Paths.PluginPath; } catch { }
            if (string.IsNullOrEmpty(pluginPath)) yield break;

            yield return Path.Combine(pluginPath, path);
            yield return Path.Combine(pluginPath, "fonts", path);
        }

        private static bool IsFontFile(string path)
        {
            return path.EndsWith(".ttf", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".otf", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".ttc", StringComparison.OrdinalIgnoreCase);
        }

        private static string Normalize(string value)
        {
            return string.IsNullOrEmpty(value) ? "" : value.Trim().Trim('"');
        }

        private static bool Same(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
        private static bool StartsWith(string a, string b) => a != null && b != null && a.StartsWith(b, StringComparison.OrdinalIgnoreCase);
        private static bool Contains(string a, string b) => a != null && b != null && a.IndexOf(b, StringComparison.OrdinalIgnoreCase) >= 0;

        private static bool ContainsExact(List<string> values, string value)
        {
            for (int i = 0; i < values.Count; i++) if (Same(values[i], value)) return true;
            return false;
        }

        private static void LogInfo(string message) { try { _log?.LogInfo(message); } catch { } }
        private static void LogWarning(string message) { try { _log?.LogWarning(message); } catch { } }

        private static void LogDefault(string message)
        {
            if (_loggedDefault) return;
            _loggedDefault = true;
            LogWarning(message);
        }
    }
}
