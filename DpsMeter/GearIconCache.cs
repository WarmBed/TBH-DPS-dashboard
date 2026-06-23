using System.Collections.Generic;
using System.Net.Http;
using System.Threading.Tasks;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>Lazily fetches item icons from the wiki by ItemKey and caches the resulting Texture2D.
    /// Gear lives under /game/gear/&lt;path&gt;; non-gear (materials/boxes) under /game/&lt;path&gt; — the base
    /// is chosen by the item's category. Bytes are fetched on a background Task; the Texture2D is created on
    /// the main thread (Unity requirement) the next time Get() is called from OnGUI.</summary>
    internal static class GearIconCache
    {
        private const string GearBase = "https://taskbarhero.wiki/game/gear/";
        private const string ItemBase = "https://taskbarhero.wiki/game/";

        private static readonly Dictionary<int, Texture2D> _tex = new Dictionary<int, Texture2D>(); // resolved (null = failed)
        private static readonly Dictionary<int, byte[]> _bytes = new Dictionary<int, byte[]>();      // downloaded, awaiting upload
        private static readonly HashSet<int> _pending = new HashSet<int>();
        private static readonly object _lock = new object();
        private static HttpClient _http;

        /// <summary>Cached icon texture for an item, or null if unavailable/still loading. Call from OnGUI.</summary>
        public static Texture2D Get(int itemKey)
        {
            if (itemKey <= 0) return null;
            if (_tex.TryGetValue(itemKey, out var cached)) return cached;   // resolved (texture or null=failed)

            byte[] data = null;
            lock (_lock) { _bytes.TryGetValue(itemKey, out data); if (data != null) _bytes.Remove(itemKey); }
            if (data != null)
            {
                Texture2D tex = null;
                try { var t = new Texture2D(2, 2, TextureFormat.RGBA32, false); if (LoadPng(t, data)) tex = t; }
                catch { }
                _tex[itemKey] = tex;   // cache success or failure (null) so we don't retry
                return tex;
            }

            lock (_lock)
            {
                if (_pending.Contains(itemKey)) return null;
                _pending.Add(itemKey);
            }
            string path = ItemMetaStore.IconPath(itemKey);
            if (string.IsNullOrEmpty(path)) { _tex[itemKey] = null; lock (_lock) { _pending.Remove(itemKey); } return null; }
            // gear icon paths are relative to /game/gear/; non-gear (materials/boxes) to /game/ root.
            string url = (ItemMetaStore.Category(itemKey) == "GEAR" ? GearBase : ItemBase) + path;
            Task.Run(async () =>
            {
                try
                {
                    if (_http == null) _http = new HttpClient { Timeout = System.TimeSpan.FromSeconds(20) };
                    var bytes = await _http.GetByteArrayAsync(url);
                    lock (_lock) { _bytes[itemKey] = bytes; }
                }
                catch { _tex[itemKey] = null; }   // network failure -> mark failed (no retry storm)
                finally { lock (_lock) { _pending.Remove(itemKey); } }
            });
            return null;
        }

        // UnityEngine.ImageConversion.LoadImage(Texture2D, byte[]) isn't in the referenced interop
        // assemblies, so resolve + invoke it reflectively. The Il2Cpp wrapper takes an Il2Cpp byte array.
        private static System.Reflection.MethodInfo _loadImage;
        private static bool _loadImageTried;
        private static bool LoadPng(Texture2D tex, byte[] data)
        {
            try
            {
                if (!_loadImageTried)
                {
                    _loadImageTried = true;
                    var t = Refl.FindType("UnityEngine.ImageConversion");
                    if (t != null)
                    {
                        // prefer the 2-arg LoadImage(Texture2D, byte[]); accept the 3-arg overload too.
                        System.Reflection.MethodInfo best = null;
                        foreach (var m in t.GetMethods(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static))
                        {
                            if (m.Name != "LoadImage") continue;
                            var ps = m.GetParameters();
                            if (ps.Length < 2 || ps[0].ParameterType.Name != "Texture2D") continue;
                            if (best == null || ps.Length < best.GetParameters().Length) best = m;
                        }
                        _loadImage = best;
                    }
                    Plugin.Logger?.LogInfo("[gearicon] ImageConversion type=" + (t != null ? "ok" : "null")
                        + " LoadImage=" + (_loadImage != null ? _loadImage.GetParameters().Length + "-arg" : "MISSING"));
                }
                if (_loadImage == null) return false;
                var arr = new Il2CppInterop.Runtime.InteropTypes.Arrays.Il2CppStructArray<byte>(data);
                var ps2 = _loadImage.GetParameters();
                object[] args = ps2.Length == 3 ? new object[] { tex, arr, false } : new object[] { tex, arr };
                var r = _loadImage.Invoke(null, args);
                return !(r is bool b) || b;
            }
            catch (System.Exception e) { Plugin.Logger?.LogWarning("[gearicon] LoadPng: " + e.Message); return false; }
        }
    }
}
