#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEngine;

namespace ContentTools.Editor
{
    /// <summary>
    /// Headless/static build bridge so automation can use the exact same flow
    /// as ContentPackBuilderWindow (icon capture, icon registration, options).
    /// </summary>
    public static class ContentPackBuildBridge
    {
        // Keep these keys/subpaths identical to the window
        private const string PREF_KEY_BUILD_LOCATION = "ContentPackBuilder.BuildLocation";
        private const string DEFAULT_BUILD_FOLDER_REL = "Assets/StreamingAssets/Addressables/Customization";


        private static string ToProjectAbsolutePath(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return null;
            path = path.Replace("\\", "/");
            if (Path.IsPathRooted(path)) return Path.GetFullPath(path).Replace("\\", "/");

            // project relative "Assets/...":
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(path, "Assets", StringComparison.OrdinalIgnoreCase))
            {
                var projectAssets = Application.dataPath;                 // <project>/Assets
                var projectRoot   = Directory.GetParent(projectAssets)!.FullName;
                var abs           = Path.Combine(projectRoot, path);
                return Path.GetFullPath(abs).Replace("\\", "/");
            }

            // If it's something else (like "./..."), normalize relative to project root
            var root2 = Directory.GetParent(Application.dataPath)!.FullName;
            return Path.GetFullPath(Path.Combine(root2, path)).Replace("\\", "/");
        }

        private static bool EnsureValidBuildFolder(string absPath, out string error)
        {
            error = null;
            if (string.IsNullOrWhiteSpace(absPath))
            {
                error = "Build output folder is empty.";
                return false;
            }

            try
            {
                if (!Path.IsPathRooted(absPath))
                {
                    error = $"Build output must be an absolute path:\n{absPath}";
                    return false;
                }

                Directory.CreateDirectory(absPath);

                // Write probe to confirm we have access
                var probe = Path.Combine(absPath, ".write_probe.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (Exception ex)
            {
                error = $"Build output folder is not writable:\n{absPath}\n\n{ex.Message}";
                return false;
            }
        }

        /// <summary>
        /// Returns the absolute build folder the window would use:
        /// EditorPrefs("ContentPackBuilder.BuildLocation") or the default StreamingAssets path.
        /// </summary>
        public static string GetConfiguredBuildLocationOrDefault()
        {
            var chosen = EditorPrefs.GetString(PREF_KEY_BUILD_LOCATION, DEFAULT_BUILD_FOLDER_REL);
            if (string.IsNullOrWhiteSpace(chosen)) chosen = DEFAULT_BUILD_FOLDER_REL;

            chosen = DEFAULT_BUILD_FOLDER_REL;
            return ToProjectAbsolutePath(chosen);
        }

        /// <summary>
        /// Build by asset paths (useful from automation code that starts from strings).
        /// </summary>
// NEW: path-driven entry point (keep this signature used by InboxAutomation)
    public static int BuildPacksByAssetPaths(IEnumerable<string> assetPaths, bool cleanMissing = true, string overrideOutputAbs = null)
    {
        var paths = (assetPaths ?? Enumerable.Empty<string>())
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.Replace("\\", "/"))
            .Distinct()
            .ToList();

        if (paths.Count == 0)
        {
            Debug.LogWarning("[ContentPackBuildBridge] No pack asset paths provided.");
            return 0;
        }

        var settings = AddressableAssetSettingsDefaultObject.Settings;
        if (!settings)
        {
            Debug.LogError("[ContentPackBuildBridge] Addressables settings not found.");
            return 0;
        }

        var outAbs = string.IsNullOrWhiteSpace(overrideOutputAbs)
            ? GetConfiguredBuildLocationOrDefault()
            : overrideOutputAbs.Replace("\\", "/");

        if (!EnsureValidBuildFolder(outAbs, out var buildErr))
        {
            Debug.LogError("[ContentPackBuildBridge] " + buildErr);
            return 0;
        }

        var opts = new AddressablesPackBuilder.BuildOptions
        {
            profileId = settings.activeProfileId,
            rebuildPlayerContent = true,
            enableRemoteCatalog = true,
            disableOtherGroups = true,
            writeManifestJson = true,
            manifestFileName = null,
            setPlayerVersionOverride = true,
            sessionRemoteBuildRootOverride = outAbs, // absolute
            sessionRemoteLoadRootOverride = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}",
        };

        int built = 0;

        foreach (var assetPath in paths)
        {
            var displayName = System.IO.Path.GetFileNameWithoutExtension(assetPath);

            try
            {
                // Always load fresh — never trust a cached reference between imports.
                var pack = AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(assetPath);
                if (!pack)
                {
                    Debug.LogWarning($"[ContentPackBuildBridge] Skipping (not found): {assetPath}");
                    continue;
                }

                // 1) Optional cleanup (can trigger Addressables group changes)
                if (cleanMissing && pack._items != null && pack._items.Count > 0)
                {
                    int before = pack._items.Count;
                    pack._items.RemoveAll(x => x == null);
                    if (pack._items.Count != before)
                    {
                        EditorUtility.SetDirty(pack);
                        pack.SyncToAddressables();
                        AssetDatabase.SaveAssets();
                    }
                }

                // 2) ICON CAPTURE — may import new assets (invalidates references), so reload after.
                {
                    var items = (pack._items ?? new List<GameObject>()).Where(x => x != null).ToList();
                    if (items.Count > 0)
                    {
                        Content_Icon_Capture.Editor.ContentIconCaptureUtility.CaptureIconsForPrefabs(
                            items,
                            renderSize: 2048,
                            outputSize: 2048,
                            imageType: Content_Icon_Capture.Editor.ContentIconCaptureUtility.ImageType.PNG
                        );
                    }
                }

                // Reload pack after imports to avoid MissingReferenceException.
                pack = AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(assetPath);
                if (!pack)
                {
                    Debug.LogWarning($"[ContentPackBuildBridge] Pack was reimported and is now missing, skipping: {assetPath}");
                    continue;
                }

                // 3) REGISTER ICONS back onto the pack and Addressables.
                {
                    if (pack._icons == null) pack._icons = new List<Texture2D>();

                    bool changedIcons = false;
                    var items = (pack._items ?? new List<GameObject>()).Where(x => x != null);
                    foreach (var go in items)
                    {
                        string iconFolder;
                        var iconPath = ComputeIconPathForPrefab(go, out iconFolder);
                        if (string.IsNullOrEmpty(iconPath)) continue;

                        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
                        if (tex && !pack._icons.Contains(tex))
                        {
                            pack._icons.Add(tex);
                            changedIcons = true;
                        }
                    }

                    if (changedIcons)
                    {
                        EditorUtility.SetDirty(pack);
                        AssetDatabase.SaveAssets();
                    }

                    // Keep Addressables groups in sync (could touch assets/groups, so reload after).
                    pack.SyncToAddressables();
                    AssetDatabase.SaveAssets();
                }

                EditorPrefs.SetString("modio_access_token",pack.modioUserToken);
                EditorPrefs.SetString("gamebuildname",pack.gameName);
                // Reload again in case Sync changed/rewrote the asset.
                pack = AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(assetPath);
                if (!pack)
                {
                    Debug.LogWarning($"[ContentPackBuildBridge] Pack was reimported and is now missing post-sync, skipping: {assetPath}");
                    continue;
                }

                // 4) BUILD
                AddressablesPackBuilder.BuildPack(pack, opts);
                Debug.Log($"[ContentPackBuildBridge] Built pack: {displayName}");
                built++;
            }
            catch (Exception ex)
            {
                // Never touch pack.name here — use displayName/path only.
                Debug.LogError($"[ContentPackBuildBridge] Failed building '{displayName}': {ex}");
            }
        }

        Debug.Log(built > 0
            ? $"[ContentPackBuildBridge] Built {built} content pack(s)."
            : "[ContentPackBuildBridge] Nothing built.");

        return built;
    }

    // unchanged helper – keep identical logic to the window so icon paths match
    private static string ComputeIconPathForPrefab(GameObject prefab, out string folder)
    {
        folder = null;
        if (!prefab) return null;
        var prefabPath = AssetDatabase.GetAssetPath(prefab);
        if (string.IsNullOrEmpty(prefabPath)) return null;

        var dir = System.IO.Path.GetDirectoryName(prefabPath)?.Replace("\\", "/") ?? "Assets";
        folder = dir.Replace("/Prefabs", "/Icons");
        var fileNameNoExt = System.IO.Path.GetFileNameWithoutExtension(prefabPath) + "_Icon";
        return System.IO.Path.Combine(folder, fileNameNoExt + ".png").Replace("\\", "/");
    }

        /// <summary>
        /// The headless version of the window’s BuildPacks: 
        /// - cleans nulls (optional)
        /// - captures icons (2K)
        /// - registers icons into the *_Icons Addressables group
        /// - builds via AddressablesPackBuilder with same options
        /// </summary>
        public static int BuildPacksLikeWindow(IEnumerable<ContentPackDefinition> packs, bool cleanMissing = true, string overrideOutputAbs = null)
        {
            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null)
            {
                Debug.LogError("[ContentPackBuildBridge] Addressables settings not found.");
                return 0;
            }

            var list = packs?.Where(p => p != null).ToList() ?? new List<ContentPackDefinition>();
            if (list.Count == 0)
            {
                Debug.LogWarning("[ContentPackBuildBridge] No packs to build.");
                return 0;
            }

            // Resolve build folder (absolute) exactly like the window does
            var outAbs = string.IsNullOrWhiteSpace(overrideOutputAbs) ? GetConfiguredBuildLocationOrDefault() : overrideOutputAbs.Replace("\\", "/");

            if (!EnsureValidBuildFolder(outAbs, out var buildErr))
            {
                Debug.LogError("[ContentPackBuildBridge] " + buildErr);
                return 0;
            }

            // Optional cleanup: strip null items & sync groups
            if (cleanMissing)
            {
                foreach (var p in list)
                {
                    if (p?._items == null) continue;
                    int before = p._items.Count;
                    if (before == 0) continue;

                    Undo.RecordObject(p, "Clean Missing Items");
                    p._items.RemoveAll(x => x == null);
                    if (p._items.Count != before)
                    {
                        EditorUtility.SetDirty(p);
                        p.SyncToAddressables();
                    }
                }

                AssetDatabase.SaveAssets();
            }

            var opts = new AddressablesPackBuilder.BuildOptions
            {
                profileId = settings.activeProfileId,
                rebuildPlayerContent = true,
                enableRemoteCatalog = true,
                disableOtherGroups = true,
                writeManifestJson = true,
                manifestFileName = null,
                setPlayerVersionOverride = true,

                sessionRemoteBuildRootOverride = outAbs, // absolute
                sessionRemoteLoadRootOverride = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}",
            };

            int built = 0;
            foreach (var pack in list)
            {
                try
                {
                    // 1) Icon capture (2K) – same utility as the window
                    var items = (pack._items ?? new List<GameObject>()).Where(x => x != null);
                    Content_Icon_Capture.Editor.ContentIconCaptureUtility.CaptureIconsForPrefabs(
                        items,
                        renderSize: 2048,
                        outputSize: 2048,
                        imageType: Content_Icon_Capture.Editor.ContentIconCaptureUtility.ImageType.PNG
                    );

                    // 2) Register icons onto the pack & Addressables
                    if (pack._icons == null) pack._icons = new List<Texture2D>();
                    bool changedIcons = false;

                    foreach (var go in items)
                    {
                        string iconFolder;
                        var iconPath = ComputeIconPathForPrefab(go, out iconFolder);
                        if (string.IsNullOrEmpty(iconPath)) continue;

                        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
                        if (tex != null && !pack._icons.Contains(tex))
                        {
                            pack._icons.Add(tex);
                            changedIcons = true;
                        }
                    }

                    if (changedIcons)
                    {
                        EditorUtility.SetDirty(pack);
                        AssetDatabase.SaveAssets();
                    }

                    // Ensure the *_Icons group is current before the pack build
                    pack.SyncToAddressables();

                    // 3) AddressablesPackBuilder build (same options)
                    AddressablesPackBuilder.BuildPack(pack, opts);
                    Debug.Log($"[ContentPackBuildBridge] Built pack '{pack.name}'.");
                    built++;
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ContentPackBuildBridge] Failed building '{pack?.name}': {ex}");
                }
            }

            Debug.Log(built > 0 ? $"[ContentPackBuildBridge] Built {built} content pack(s)." : "[ContentPackBuildBridge] Nothing built.");
            return built;
        }
        
    }
}
#endif
