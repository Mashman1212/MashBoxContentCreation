#if UNITY_EDITOR
using System;
using System.IO;
using System.Collections.Generic;
using System.Text.RegularExpressions;
#if UNITY_EDITOR_WIN
using Microsoft.Win32;
#endif

namespace ContentTools.Editor.SteamDetect
{
    public static class SteamLocator
    {
        public static string GetSteamRoot()
        {
#if UNITY_EDITOR_WIN
            try
            {
                using (var key = Registry.CurrentUser.OpenSubKey(@"Software\Valve\Steam"))
                {
                    var p = key?.GetValue("SteamPath") as string;
                    if (!string.IsNullOrEmpty(p) && Directory.Exists(p)) return p.Replace('/', '\\');
                }
            } catch {}
            var candidates = new[]
            {
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Steam"),
                Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Steam")
            };
            foreach (var c in candidates) if (Directory.Exists(c)) return c;
            return null;
#elif UNITY_EDITOR_OSX
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            var p = Path.Combine(home, "Library/Application Support/Steam");
            return Directory.Exists(p) ? p : null;
#else
            var home = Environment.GetFolderPath(Environment.SpecialFolder.Personal);
            var primary = Path.Combine(home, ".local/share/Steam");
            if (Directory.Exists(primary)) return primary;
            var flatpak = Path.Combine(home, ".var/app/com.valvesoftware.Steam/.local/share/Steam");
            return Directory.Exists(flatpak) ? flatpak : null;
#endif
        }

        public static IEnumerable<string> GetSteamAppsFolders()
        {
            var root = GetSteamRoot();
            if (string.IsNullOrEmpty(root)) yield break;

            var primary = Path.Combine(root, "steamapps");
            if (Directory.Exists(primary)) yield return primary;

            var libVdf = Path.Combine(primary, "libraryfolders.vdf");
            if (!File.Exists(libVdf)) yield break;

            foreach (var lib in ParseLibraryFoldersVdf(libVdf))
            {
                var sa = Path.Combine(lib, "steamapps");
                if (Directory.Exists(sa)) yield return sa;
            }
        }

        public static string TryGetGameInstallPath(long appId)
        {
            foreach (var steamapps in GetSteamAppsFolders())
            {
                var manifest = Path.Combine(steamapps, $"appmanifest_{appId}.acf");
                if (!File.Exists(manifest)) continue;

                var installdir = ParseKeyFromAcf(manifest, "installdir");
                if (string.IsNullOrEmpty(installdir)) continue;

                var full = Path.Combine(steamapps, "common", installdir);
                if (Directory.Exists(full)) return full;
            }
            return null;
        }

        public static Dictionary<long, string> TryGetGameInstallPaths(IEnumerable<long> appIds)
        {
            var result = new Dictionary<long, string>();
            foreach (var id in appIds)
            {
                var p = TryGetGameInstallPath(id);
                if (!string.IsNullOrEmpty(p)) result[id] = p;
            }
            return result;
        }

        private static IEnumerable<string> ParseLibraryFoldersVdf(string filePath)
        {
            var text = File.ReadAllText(filePath);
            var paths = new List<string>();
            var pathMatches = Regex.Matches(text, "\"path\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            foreach (Match m in pathMatches)
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\").Replace("/", Path.DirectorySeparatorChar.ToString());
                if (Directory.Exists(p)) paths.Add(p);
            }
            var flatMatches = Regex.Matches(text, "^\\s*\"\\d+\"\\s*\"([^\"]+)\"\\s*$", RegexOptions.Multiline);
            foreach (Match m in flatMatches)
            {
                var p = m.Groups[1].Value.Replace("\\\\", "\\").Replace("/", Path.DirectorySeparatorChar.ToString());
                if (Directory.Exists(p)) paths.Add(p);
            }
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in paths) if (seen.Add(p)) yield return p;
        }

        private static string ParseKeyFromAcf(string filePath, string key)
        {
            var text = File.ReadAllText(filePath);
            var m = Regex.Match(text, $"\"{Regex.Escape(key)}\"\\s*\"([^\"]+)\"", RegexOptions.IgnoreCase);
            return m.Success ? m.Groups[1].Value : null;
        }
    }
}
#endif
