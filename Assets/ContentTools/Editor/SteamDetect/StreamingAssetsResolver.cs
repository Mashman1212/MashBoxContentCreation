#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;

namespace ContentTools.Editor.SteamDetect
{
    public static class StreamingAssetsResolver
    {
        // Return the StreamingAssets directory inside a Unity game install folder.
        // Tries common layouts on Win/Linux and macOS .app bundles, and falls back to a scan.
        public static string TryResolve(string installRoot)
        {
            if (string.IsNullOrEmpty(installRoot) || !Directory.Exists(installRoot))
                return null;

            // 1) Common Win/Linux: <Install>/<Game>_Data/StreamingAssets
            var dataDir = Directory.GetDirectories(installRoot, "*_Data").FirstOrDefault();
            if (dataDir != null)
            {
                var sa = Path.Combine(dataDir, "StreamingAssets");
                if (Directory.Exists(sa)) return Normalize(sa);
            }

#if UNITY_EDITOR_OSX
            // 2) macOS: <Install>/<Game>.app/Contents/Resources/Data/StreamingAssets
            var app = Directory.GetFiles(installRoot, "*.app").FirstOrDefault() ??
                      Directory.GetDirectories(installRoot, "*.app").FirstOrDefault();
            if (app != null)
            {
                var sa = Path.Combine(app, "Contents/Resources/Data/StreamingAssets");
                if (Directory.Exists(sa)) return Normalize(sa);
            }
            // Also handle no top-level .app but game folder with .app inside
            var appDir = Directory.GetDirectories(installRoot, "*.app").FirstOrDefault();
            if (appDir != null)
            {
                var sa = Path.Combine(appDir, "Contents/Resources/Data/StreamingAssets");
                if (Directory.Exists(sa)) return Normalize(sa);
            }
#endif

            // 3) Fallback: depth-limited scan for any *_Data/StreamingAssets
            try
            {
                var hits = Directory.GetDirectories(installRoot, "StreamingAssets", SearchOption.AllDirectories)
                                    .Where(p => p.EndsWith("StreamingAssets", StringComparison.OrdinalIgnoreCase) &&
                                                p.IndexOf("_Data", StringComparison.OrdinalIgnoreCase) >= 0);
                var first = hits.FirstOrDefault();
                if (first != null) return Normalize(first);
            }
            catch {}

            return null;
        }

        public static string AppendSubfolder(string streamingAssets, string subRelative)
        {
            if (string.IsNullOrEmpty(streamingAssets)) return null;
            var p = Path.Combine(streamingAssets, subRelative.Replace('/', Path.DirectorySeparatorChar));
            return Normalize(p);
        }

        private static string Normalize(string p) => Path.GetFullPath(p).Replace("\\", "/");
    }
}
#endif
