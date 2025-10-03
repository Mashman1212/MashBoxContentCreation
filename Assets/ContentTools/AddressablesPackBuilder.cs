#if UNITY_EDITOR
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Build;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace ContentTools.Editor
{
    /// <summary>
    /// Builds a single content pack to the chosen Build Location:
    /// - ALWAYS writes bundles and catalog to a physical folder: <BuildLocation>/<pack>
    /// - Before build, simplifies each selected group's entry addresses to the bare file name (unique across all selected groups)
    /// - Uses non-dynamic Build/Load during the build to avoid SBP mismatches
    /// - AFTER the build, if BuildLocation is under StreamingAssets, rewrites catalog*.json
    ///   so internal paths become JSON-escaped:
    ///   {Application.streamingAssetsPath}\\<relative-after-SA>\\<pack>\\...
    ///   and recomputes catalog.hash accordingly (or deletes it on failure).
    /// - Restores Addressables settings afterward
    /// Works by variable NAME (no GUID APIs) for broad Addressables compatibility.
    /// </summary>
    public static class AddressablesPackBuilder
    {
        [System.Serializable]
        private class PackBuildManifest
        {
            public string packName;
            public string version;
            public string buildTarget;
            public string profileName;
            public string playerVersionOverride;
            public string catalogRemoteUrl;
            public string catalogLocalPath;
            public string bundlesRemoteRoot;
            public string bundlesLocalPath;
        }

        private class GroupState
        {
            public AddressableAssetGroup group;
            public BundledAssetGroupSchema schema;
            public string origBuildVarId;   // original variable (ID) reference from the group
            public string origLoadVarId;    // original variable (ID) reference from the group
            public bool includeInBuild;
        }

        private class ProfileOverrideBackup
        {
            // We store the variable NAME here; SetValue accepts names.
            public string varId;            // (name)
            public string previousValue;
        }

        public struct BuildOptions
        {
            public string profileId;                       // Addressables profile to build with
            public bool rebuildPlayerContent;              // reserved
            public bool enableRemoteCatalog;               // force remote catalog on
            public bool disableOtherGroups;                // include only selected pack's groups
            public bool writeManifestJson;                 // write manifest JSON
            public string manifestFileName;                // default: {pack}.manifest.json
            public bool setPlayerVersionOverride;          // sets OverridePlayerVersion = {pack}_{version}

            // From the window:
            public string sessionRemoteBuildRootOverride;  // Build Location (filesystem folder) - authoritative
            public string sessionRemoteLoadRootOverride;   // ignored here during build (we unify to non-dynamic)

            // Keep groups on Local-style schemas before rewire (so report looks sane)
            public bool forceLocalPaths;
        }

        public static void BuildPack(ContentPackDefinition def, BuildOptions opts)
        {
            if (def == null) { Debug.LogError("ContentPackDefinition is null"); return; }

            var settings = AddressableAssetSettingsDefaultObject.Settings;
            if (settings == null) { Debug.LogError("Addressables settings not found."); return; }

            //if (def.groupNames == null || def.groupNames.Length == 0)
            //{
            //    Debug.LogError($"Pack {def.packName}: No groups specified.");
            //    return;
            //}

            //foreach (var g in def.groupNames)
            //{
            //    if (settings.FindGroup(g) == null)
            //    {
            //        Debug.LogError($"Pack {def.packName}: Group not found: {g}");
            //        return;
            //    }
            //}

            if (string.IsNullOrEmpty(opts.profileId))
                opts.profileId = settings.activeProfileId;

            var prof = settings.profileSettings;
            var profileName = prof.GetProfileName(opts.profileId);

            string sub = def.PackName;

            // --- Backups ---
            bool prevRemoteCatalog = settings.BuildRemoteCatalog;
            string prevOverridePlayerVersion = settings.OverridePlayerVersion;
            string prevRemoteCatalogBuildVarId = settings.RemoteCatalogBuildPath != null ? settings.RemoteCatalogBuildPath.Id : null;
            string prevRemoteCatalogLoadVarId  = settings.RemoteCatalogLoadPath  != null ? settings.RemoteCatalogLoadPath.Id  : null;

            if (opts.enableRemoteCatalog) settings.BuildRemoteCatalog = true;
            if (opts.setPlayerVersionOverride) settings.OverridePlayerVersion = $"{def.PackName}";

            // Track groups + originals
            var groupSet = new HashSet<string>();
            groupSet.Add(def.PackName);
            var tracked = new List<GroupState>();

            foreach (var g in settings.groups.Where(x => x != null && groupSet.Contains(x.Name)))
            {
                var s = g.GetSchema<BundledAssetGroupSchema>();
                if (s == null) continue;

                string ob = s.BuildPath != null ? s.BuildPath.Id : null;
                string ol = s.LoadPath  != null ? s.LoadPath.Id  : null;

                if (opts.forceLocalPaths)
                {
                    s.BuildPath.SetVariableById(settings, AddressableAssetSettings.kLocalBuildPath);
                    s.LoadPath.SetVariableById(settings, AddressableAssetSettings.kLocalLoadPath);
                    EditorUtility.SetDirty(g);

                    ob = AddressableAssetSettings.kLocalBuildPath;
                    ol = AddressableAssetSettings.kLocalLoadPath;
                }

                tracked.Add(new GroupState
                {
                    group = g,
                    schema = s,
                    origBuildVarId = ob,
                    origLoadVarId = ol,
                    includeInBuild = s.IncludeInBuild
                });
            }

            if (tracked.Count == 0)
            {
                Debug.LogError($"Pack {def.PackName}: No valid groups with BundledAssetGroupSchema.");
                return;
            }

            // --------- NEW: Simplify addresses across all selected groups ----------
            SimplifyAddressesForSelectedGroups(tracked.Select(t => t.group));

            // Resolve Build Location (authoritative) and pack folder
            string buildRoot = (opts.sessionRemoteBuildRootOverride ?? "").Replace("\\", "/");
            if (string.IsNullOrEmpty(buildRoot))
            {
                Debug.LogError("Build Location is empty.");
                return;
            }

            string packBuildFolder = Path.Combine(buildRoot, sub).Replace("\\", "/");
            try { Directory.CreateDirectory(packBuildFolder); } catch {}

            // During build: make BOTH Build & Load non-dynamic and pointing to the same folder
            string buildPathValue = packBuildFolder;            // filesystem path
            string loadPathValue  = ToFileURL(packBuildFolder); // file:/// url

            // Create pack-scoped profile vars (by NAME) and set values
            string packBuildVarName = EnsureProfileVar(prof, $"Pack_{def.PackName}_BuildPath", buildPathValue);
            string packLoadVarName  = EnsureProfileVar(prof, $"Pack_{def.PackName}_LoadPath",  loadPathValue);

            var backups = new List<ProfileOverrideBackup>();
            foreach (var (name, val) in new[] { (packBuildVarName, buildPathValue), (packLoadVarName, loadPathValue) })
            {
                string prev = prof.GetValueByName(opts.profileId, name);
                backups.Add(new ProfileOverrideBackup { varId = name, previousValue = prev });
                prof.SetValue(opts.profileId, name, val);
            }

            // Point global Remote Catalog paths to our per-pack vars for the build output location
            if (settings.RemoteCatalogBuildPath != null)
                settings.RemoteCatalogBuildPath.SetVariableByName(settings, packBuildVarName);
            if (settings.RemoteCatalogLoadPath != null)
                settings.RemoteCatalogLoadPath.SetVariableByName(settings,  packLoadVarName);

            // Rewire ONLY selected groups to pack vars (by NAME)
            foreach (var t in tracked)
            {
                t.schema.BuildPath.SetVariableByName(settings, packBuildVarName);
                t.schema.LoadPath.SetVariableByName(settings,  packLoadVarName);
                EditorUtility.SetDirty(t.group);
            }

            // Optionally isolate IncludeInBuild
            if (opts.disableOtherGroups)
            {
                foreach (var g in settings.groups)
                {
                    if (g == null) continue;
                    var s = g.GetSchema<BundledAssetGroupSchema>();
                    if (s == null) continue;
                    s.IncludeInBuild = groupSet.Contains(g.Name);
                    EditorUtility.SetDirty(g);
                }
            }

            // If Build Location is under StreamingAssets, compute the dynamic base we will JSON-inject post-build
            bool underSA = TryGetRelativeUnderStreamingAssets(buildRoot, out string relAfterSA); // e.g., "Addressables/Customization"
            string dynamicBaseForCatalog = null; // e.g., {Application.streamingAssetsPath}\Addressables\Customization
            if (underSA)
            {
                string after = string.IsNullOrEmpty(relAfterSA) ? "" : relAfterSA.Trim('/').Replace("/", "\\");
                dynamicBaseForCatalog = "{Application.streamingAssetsPath}" + (after.Length > 0 ? ("\\" + after) : "");
                // final per-pack prefix will be dynamicBaseForCatalog + "\\" + sub + "\\"
            }

            try
            {
                Debug.Log($"[PackBuilder] Profile '{profileName}' ({opts.profileId})");
                Debug.Log($"[PackBuilder] BuildPath var '{packBuildVarName}' → {buildPathValue}");
                Debug.Log($"[PackBuilder] LoadPath  var '{packLoadVarName}'  → {loadPathValue}");

                AddressableAssetSettings.BuildPlayerContent();

                // Locate outputs
                string serverData = packBuildFolder;
                string[] catalogs = Directory.Exists(serverData)
                    ? Directory.GetFiles(serverData, "catalog*.json", SearchOption.AllDirectories)
                    : new string[0];

                if (catalogs.Length == 0 && Directory.Exists(serverData))
                {
                    var deep = Directory.GetFiles(serverData, "catalog*.json", SearchOption.AllDirectories);
                    if (deep.Length > 0) catalogs = deep;
                }

                string catalogLocal = catalogs.Length > 0 ? catalogs[0] : "";
                string catalogRemoteUrl = GuessCatalogRemoteUrl(loadPathValue, catalogLocal);

                // If under StreamingAssets, rewrite catalog to JSON-escaped dynamic tokenized paths AND recompute/replace .hash
                if (underSA && !string.IsNullOrEmpty(catalogLocal))
                {
                    string hashPath = Path.Combine(
                        Path.GetDirectoryName(catalogLocal) ?? serverData,
                        Path.GetFileNameWithoutExtension(catalogLocal) + ".hash"
                    );

                    try
                    {
                        // physical prefix variants to replace
                        string physicalPrefixFwd = (serverData + "/").Replace("\\", "/");
                        string physicalPrefixBwd = (serverData + "\\").Replace("/", "\\");

                        // 1) dynamic prefix with backslashes (runtime form you want)
                        string dynamicPerPackRaw  = (dynamicBaseForCatalog ?? "{Application.streamingAssetsPath}") + "\\" + sub + "\\";

                        // 2) JSON-safe escaped version (backslashes doubled)
                        string dynamicPerPackJson = dynamicPerPackRaw.Replace("\\", "\\\\");

                        // 3) rewrite JSON using the JSON-escaped value
                        string json = File.ReadAllText(catalogLocal);
                        json = json.Replace(physicalPrefixFwd, dynamicPerPackJson);
                        json = json.Replace(physicalPrefixBwd, dynamicPerPackJson);

                        File.WriteAllText(catalogLocal, json);

                        // 4) recompute catalog.hash so it matches the updated JSON
                        string newHash = ComputeCatalogHash(json);
                        File.WriteAllText(hashPath, newHash);

                        // 5) a URL-friendly value (forward slashes) for logs/manifest
                        catalogRemoteUrl = GuessCatalogRemoteUrl(dynamicPerPackRaw.Replace("\\", "/"), catalogLocal);

                        Debug.Log($"[PackBuilder] Rewrote catalog to dynamic (JSON-escaped) and updated hash → {hashPath}");
                    }
                    catch (System.Exception ex)
                    {
                        Debug.LogWarning($"[PackBuilder] Failed to rewrite catalog or update hash: {ex.Message}");

                        // Fallback: delete the now-stale .hash so it can't mismatch the JSON
                        try { if (File.Exists(hashPath)) File.Delete(hashPath); } catch { }
                    }
                }

                // Manifest
                if (opts.writeManifestJson)
                {
                    var manifest = new PackBuildManifest
                    {
                        packName = def.PackName,
                        version = "1.0.0",
                        buildTarget = EditorUserBuildSettings.activeBuildTarget.ToString(),
                        profileName = profileName,
                        playerVersionOverride = settings.OverridePlayerVersion,
                        catalogRemoteUrl = catalogRemoteUrl,
                        catalogLocalPath = catalogLocal,
                        bundlesRemoteRoot = underSA && dynamicBaseForCatalog != null
                            ? (dynamicBaseForCatalog.Replace("\\", "/") + "/" + sub)
                            : loadPathValue,
                        bundlesLocalPath = serverData,
                    };
                    string fileName = string.IsNullOrEmpty(opts.manifestFileName) ? $"{def.PackName}.manifest.json" : opts.manifestFileName;
                    string manifestPath = Path.Combine(serverData, fileName);
                    Directory.CreateDirectory(Path.GetDirectoryName(manifestPath) ?? serverData);
                    File.WriteAllText(manifestPath, JsonUtility.ToJson(manifest, true));
                    Debug.Log($"Wrote pack manifest → {manifestPath}");
                }

                if (!string.IsNullOrEmpty(catalogLocal))
                {
                    Debug.Log($"Pack '{def.PackName}' built.\nCatalog local: {catalogLocal}\nLoad this at runtime: {catalogRemoteUrl}");

                }
                else
                {
                    Debug.LogWarning($"Build finished but no catalog*.json was found under: {serverData}\n" +
                                     $"Check that 'Build Remote Catalog' is enabled and paths are bound.");
                }
            }
            finally
            {
                // Restore groups & IncludeInBuild
                foreach (var t in tracked)
                {
                    if (!string.IsNullOrEmpty(t.origBuildVarId))
                        t.schema.BuildPath.SetVariableById(settings, t.origBuildVarId);
                    if (!string.IsNullOrEmpty(t.origLoadVarId))
                        t.schema.LoadPath.SetVariableById(settings, t.origLoadVarId);
                    t.schema.IncludeInBuild = t.includeInBuild;
                    EditorUtility.SetDirty(t.group);
                }

                // Restore per-pack profile values (by NAME)
                foreach (var b in backups)
                    prof.SetValue(opts.profileId, b.varId, b.previousValue);

                // Restore Remote Catalog variable bindings
                if (settings.RemoteCatalogBuildPath != null && !string.IsNullOrEmpty(prevRemoteCatalogBuildVarId))
                    settings.RemoteCatalogBuildPath.SetVariableById(settings, prevRemoteCatalogBuildVarId);
                if (settings.RemoteCatalogLoadPath != null && !string.IsNullOrEmpty(prevRemoteCatalogLoadVarId))
                    settings.RemoteCatalogLoadPath.SetVariableById(settings, prevRemoteCatalogLoadVarId);

                // Restore globals
                settings.BuildRemoteCatalog = prevRemoteCatalog;
                settings.OverridePlayerVersion = prevOverridePlayerVersion;
                AssetDatabase.SaveAssets();
            }
        }

        // --- Helpers ---

        /// <summary>
        /// For each entry in the given groups, set address = file name (no path/no extension),
        /// ensuring uniqueness across all selected groups by appending _2, _3, ...
        /// Skips folders/missing GUIDs. Only touches entries whose address differs.
        /// </summary>
        private static void SimplifyAddressesForSelectedGroups(IEnumerable<AddressableAssetGroup> groups)
        {
            var used = new HashSet<string>();
            foreach (var g in groups)
            {
                if (g == null) continue;

                // Get all entries currently in the group
                // Prefer GatherAllAssets if available; otherwise fall back to g.entries
                var entries = new List<AddressableAssetEntry>();
                try
                {
                    // includeSubObjects=true, includeSelf=true, includeInactive=true — conservative gather
                    g.GatherAllAssets(entries, true, true, true);
                }
                catch
                {
                    // Fallback if API surface differs
                    entries.AddRange(g.entries);
                }

                bool anyChanged = false;

                foreach (var e in entries)
                {
                    if (e == null || string.IsNullOrEmpty(e.guid)) continue;

                    var path = AssetDatabase.GUIDToAssetPath(e.guid);
                    if (string.IsNullOrEmpty(path)) continue;
                    if (AssetDatabase.IsValidFolder(path)) continue; // skip folders

                    var name = Path.GetFileNameWithoutExtension(path);
                    if (string.IsNullOrEmpty(name)) continue;

                    // ensure global uniqueness
                    string candidate = name;
                    int n = 2;
                    while (used.Contains(candidate))
                        candidate = $"{name}_{n++}";

                    // If address already equals our candidate, don't touch it
                    if (e.address != candidate)
                    {
                        Undo.RecordObject(g, "Simplify Addressable Name");
                        e.SetAddress(candidate);
                        anyChanged = true;
                    }
                    used.Add(candidate);
                }

                if (anyChanged)
                    EditorUtility.SetDirty(g);
            }
        }

        private static bool TryGetRelativeUnderStreamingAssets(string absolute, out string relAfterSA)
        {
            relAfterSA = null;
            if (string.IsNullOrEmpty(absolute)) return false;
            string norm = absolute.Replace("\\", "/");
            string marker = "/StreamingAssets/";
            int idx = norm.IndexOf(marker, System.StringComparison.OrdinalIgnoreCase);
            if (idx < 0) return false;
            relAfterSA = norm.Substring(idx + marker.Length); // may be empty
            return true;
        }

        private static string ToFileURL(string absolutePath)
        {
            if (string.IsNullOrEmpty(absolutePath)) return absolutePath;
            string p = absolutePath.Replace("\\", "/");
            if (!p.StartsWith("file://")) return "file:///" + p.TrimStart('/');
            return p;
        }

        private static string CombineUrl(string baseUrl, string segment)
        {
            if (string.IsNullOrEmpty(baseUrl)) return segment;
            if (baseUrl.EndsWith("/")) return baseUrl + segment;
            return baseUrl + "/" + segment;
        }

        private static string GuessCatalogRemoteUrl(string loadRoot, string catalogLocalPath)
        {
            string fileName = string.IsNullOrEmpty(catalogLocalPath) ? "catalog.json" : Path.GetFileName(catalogLocalPath);
            // Normalize to forward slashes for URL concatenation
            string baseUrl = (loadRoot ?? "").Replace("\\", "/");
            return CombineUrl(baseUrl, fileName);
        }

        /// <summary>
        /// Ensure a profile variable exists with the given NAME; set default value if newly created.
        /// Returns the NAME. We work entirely by name to avoid GUID API differences.
        /// </summary>
        private static string EnsureProfileVar(AddressableAssetProfileSettings prof, string name, string defaultValueIfCreated)
        {
            try { prof.CreateValue(name, defaultValueIfCreated); } catch {}
            return name;
        }

        private static string NameFromGroupVarId(string varId)
        {
            if (string.IsNullOrEmpty(varId)) return null;
            if (varId == AddressableAssetSettings.kLocalBuildPath)  return AddressableAssetSettings.kLocalBuildPath;
            if (varId == AddressableAssetSettings.kLocalLoadPath)   return AddressableAssetSettings.kLocalLoadPath;
            if (varId == AddressableAssetSettings.kRemoteBuildPath) return AddressableAssetSettings.kRemoteBuildPath;
            if (varId == AddressableAssetSettings.kRemoteLoadPath)  return AddressableAssetSettings.kRemoteLoadPath;
            return null;
        }

        private static string ComputeCatalogHash(string json)
        {
            // Preferred: Unity's Hash128 (available in most editor versions)
            try
            {
                return Hash128.Compute(json).ToString();
            }
            catch
            {
                // Fallback: MD5 (Addressables just needs a stable hash string)
                using (var md5 = MD5.Create())
                {
                    var bytes = Encoding.UTF8.GetBytes(json);
                    var hashBytes = md5.ComputeHash(bytes);
                    var sb = new StringBuilder(hashBytes.Length * 2);
                    for (int i = 0; i < hashBytes.Length; i++)
                        sb.Append(hashBytes[i].ToString("x2"));
                    return sb.ToString();
                }
            }
        }
    }
}
#endif
