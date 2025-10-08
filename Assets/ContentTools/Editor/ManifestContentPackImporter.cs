#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// If you move ContentPackDefinition/AddressablesPackBuilder namespaces, adjust these:
using ContentTools;                     // ContentPackDefinition
using ContentTools.Editor;
using UnityEditor.AddressableAssets; // AddressablesPackBuilder (optional)
using Object = UnityEngine.Object;

namespace ContentTools.Editor
{
    /// <summary>
    /// Imports content packs from simple manifest JSON files found under:
    ///     Assets/Content/PackageManifests
    ///
    /// Manifest format (minimal):
    /// {
    ///   "items": [ { "guid": "xxxxxxxxxxxxxxxxxxxxxxxxxxxxxxxx", "type":"prefab" }, ... ]
    /// }
    ///
    /// Creates/updates ContentPackDefinition assets in: Assets/ContentPacks
    /// </summary>
    public static class ManifestContentPackImporter
    {
        // Where we look for *.json manifests:
        private const string ManifestFolder = "Assets/Content/PackageManifests";

        // Where we write/maintain ContentPackDefinition assets:
        // (kept consistent with your ContentPackBuilderWindow) 
        private const string PacksFolder = "Assets/ContentPacks";

        // Toggle this to automatically build with AddressablesPackBuilder after import
        private const bool AutoBuildAfterImport = false;

        // ===== DTO just for reading the manifest JSON =====
        [Serializable]
        private class ManifestDTO
        {
            [Serializable] public class ItemRef { public string guid; public string type; } // type ignored (always "prefab")
            public List<ItemRef> items = new List<ItemRef>();
        }

        [MenuItem("Tools/MashBox/Content/Import Packs From Manifests")]
        public static void ImportAllFromMenu()
        {
            var count = ImportAll();
            EditorUtility.DisplayDialog("Manifest Import",
                count > 0 ? $"Imported/updated {count} pack(s) from manifests." : "No manifests found.",
                "OK");
        }

        [MenuItem("Tools/MashBox/Content/Reimport All Manifests (Force)")]
        public static void ReimportAllFromMenu()
        {
            var count = ImportAll(force:true);
            EditorUtility.DisplayDialog("Manifest Import",
                count > 0 ? $"Reimported {count} pack(s)." : "No manifests found.",
                "OK");
        }

        /// <summary>Scans the manifest folder and imports all manifests found.</summary>
        public static int ImportAll(bool force = false)
        {
            EnsureFolder(ManifestFolder);
            EnsureFolder(PacksFolder);

            var manifestPaths = Directory.Exists(ManifestFolder)
                ? Directory.GetFiles(ManifestFolder, "*.json", SearchOption.TopDirectoryOnly)
                : Array.Empty<string>();

            int imported = 0;
            foreach (var path in manifestPaths)
            {
                if (ImportOne(path, force)) imported++;
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            return imported;
        }

        /// <summary>Imports one manifest file into a ContentPackDefinition.</summary>
        public static bool ImportOne(string manifestPath, bool force = false)
        {
            if (string.IsNullOrEmpty(manifestPath) || !File.Exists(manifestPath)) return false;

            // Pack name from file name (e.g., "Test Car_manifest.json" -> "Test Car_manifest")
            var packName = Path.GetFileNameWithoutExtension(manifestPath);
            if (string.IsNullOrWhiteSpace(packName)) return false;

            // Load manifest JSON (Unity's JsonUtility requires matching DTO shape)
            ManifestDTO dto = null;
            try
            {
                var json = File.ReadAllText(manifestPath);
                dto = JsonUtility.FromJson<ManifestDTO>(json);
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ManifestImporter] Failed reading '{manifestPath}': {ex.Message}");
                return false;
            }

            if (dto == null)
            {
                Debug.LogError($"[ManifestImporter] '{manifestPath}' is not valid JSON.");
                return false;
            }

            // Resolve item GUIDs -> prefab assets
            var prefabs = new List<GameObject>();
            if (dto.items != null)
            {
                foreach (var r in dto.items)
                {
                    if (r == null || string.IsNullOrEmpty(r.guid)) continue;
                    var assetPath = AssetDatabase.GUIDToAssetPath(r.guid);
                    if (string.IsNullOrEmpty(assetPath) || !assetPath.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    {
                        Debug.LogWarning($"[ManifestImporter] GUID missing/not a prefab in '{packName}': {r.guid}");
                        continue;
                    }
                    var go = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                    if (go != null) prefabs.Add(go);
                    else Debug.LogWarning($"[ManifestImporter] Could not load prefab at '{assetPath}' for GUID {r.guid}");
                }
            }

            // Create or load the pack asset
            EnsureFolder(PacksFolder);
            var assetPathOut = $"{PacksFolder}/{Sanitize(packName)}.asset";
            var pack = AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(assetPathOut);
            bool created = false;

            if (pack == null)
            {
                pack = ScriptableObject.CreateInstance<ContentPackDefinition>();
                AssetDatabase.CreateAsset(pack, assetPathOut);
                created = true;
            }

            // Replace items
            Undo.RecordObject(pack, "Update Content Pack From Manifest");
            pack._items = prefabs.Distinct().Where(x => x != null).ToList();
            EditorUtility.SetDirty(pack);

            // Name & save (ContentPackDefinition uses its asset name as PackName)
            var currentName = Path.GetFileNameWithoutExtension(assetPathOut);
            if (pack.name != currentName)
                AssetDatabase.RenameAsset(assetPathOut, currentName);

            AssetDatabase.SaveAssets();

            // Sync to Addressables (uses your implementation)
            pack.SyncToAddressables(); // creates/updates Addressables groups & entries :contentReference[oaicite:3]{index=3}

            // Optional build step, per pack
            if (AutoBuildAfterImport)
            {
                try
                {
                    var opts = new AddressablesPackBuilder.BuildOptions
                    {
                        profileId = AddressableAssetSettingsDefaultObject.Settings?.activeProfileId,
                        rebuildPlayerContent = true,
                        enableRemoteCatalog = true,
                        disableOtherGroups = true,
                        writeManifestJson = true,
                        setPlayerVersionOverride = true,

                        // If you want to write under StreamingAssets, set this to your chosen absolute folder.
                        // Otherwise builder will validate the path before building. :contentReference[oaicite:4]{index=4}
                        sessionRemoteBuildRootOverride = null,
                        sessionRemoteLoadRootOverride = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}",
                    };
                    AddressablesPackBuilder.BuildPack(pack, opts); // :contentReference[oaicite:5]{index=5}
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[ManifestImporter] Build failed for '{packName}': {ex.Message}");
                }
            }

            Debug.Log($"[ManifestImporter] {(created ? "Created" : "Updated")} pack '{pack.name}' from '{manifestPath}'.");
            return true;
        }

        // ===== Utilities =====

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder)) return;
            var parts = folder.Split('/');
            string cur = "Assets";
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{cur}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(cur, parts[i]);
                cur = next;
            }
        }

        private static string Sanitize(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "Pack";
            var invalid = Path.GetInvalidFileNameChars();
            return new string(raw.Select(c => invalid.Contains(c) ? '_' : c).ToArray());
        }
    }

    /// <summary>
    /// Auto-import on manifest add/change/delete inside Assets/Content/PackageManifests
    /// </summary>
    public class ManifestJsonPostprocessor : AssetPostprocessor
    {
        private const string WatchRoot = "Assets/Content/PackageManifests";

        static void OnPostprocessAllAssets(
            string[] importedAssets, string[] deletedAssets, string[] movedAssets, string[] movedFromAssetPaths)
        {
            bool touched = false;

            foreach (var p in importedAssets)
                if (IsManifestPath(p)) { ManifestContentPackImporter.ImportOne(p, force:true); touched = true; }

            foreach (var p in movedAssets)
                if (IsManifestPath(p)) { ManifestContentPackImporter.ImportOne(p, force:true); touched = true; }

            // If any manifests were deleted, you may want to delete their packs too.
            // (Skipping automatic deletion to be safe.)

            if (touched)
            {
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }

        private static bool IsManifestPath(string path) =>
            !string.IsNullOrEmpty(path)
            && path.Replace("\\", "/").StartsWith(WatchRoot, StringComparison.OrdinalIgnoreCase)
            && path.EndsWith(".json", StringComparison.OrdinalIgnoreCase);
    }
}
#endif
