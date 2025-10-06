using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Content_Icon_Capture.Editor
{
    public static class ContentIconCaptureUtility
    {
        public enum ImageType
        {
            PNG,
            JPG,
            TGA
        }

        /// <summary>
        /// Renders a square icon from the given camera and writes to outputPath (without extension).
        /// The file extension is chosen from imageType.
        /// </summary>
        public static void CaptureAndSaveIcon(string outputPath, Camera captureCamera, int renderSize, int outputSize,
            ImageType imageType)
        {
            if (captureCamera == null)
            {
                Debug.LogError("[ContentIconCaptureUtility] CaptureAndSaveIcon: No capture camera provided.");
                return;
            }

            // 1) Render full-res
            var rt = new RenderTexture(renderSize, renderSize, 24, RenderTextureFormat.ARGB32);
            captureCamera.targetTexture = rt;
            captureCamera.depthTextureMode = DepthTextureMode.Depth;
            captureCamera.Render();

            var fullTex = new Texture2D(renderSize, renderSize, TextureFormat.RGBA32, false);
            RenderTexture.active = rt;
            fullTex.ReadPixels(new Rect(0, 0, renderSize, renderSize), 0, 0);
            fullTex.Apply();

            // 2) Scale to output
            var scaledRT = new RenderTexture(outputSize, outputSize, 24, RenderTextureFormat.ARGB32);
            Graphics.Blit(fullTex, scaledRT);
            var scaledTex = new Texture2D(outputSize, outputSize, TextureFormat.RGBA32, false);
            RenderTexture.active = scaledRT;
            scaledTex.ReadPixels(new Rect(0, 0, outputSize, outputSize), 0, 0);
            scaledTex.Apply();

            // Cleanup render targets
            RenderTexture.active = null;
            captureCamera.targetTexture = null;
            rt.Release();
            scaledRT.Release();

            // 3) Write file
            try
            {
                outputPath = outputPath + "." + imageType.ToString().ToLower();
                if (imageType == ImageType.JPG)
                    File.WriteAllBytes(outputPath, scaledTex.EncodeToJPG());
                else if (imageType == ImageType.PNG)
                    File.WriteAllBytes(outputPath, scaledTex.EncodeToPNG());
                else // TGA
                    File.WriteAllBytes(outputPath, scaledTex.EncodeToTGA());

                Debug.Log($"[ContentIconCaptureUtility] Icon saved to: {outputPath}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ContentIconCaptureUtility] Failed to save icon: {ex.Message}");
            }

#if UNITY_EDITOR
            // 4) Import with desired settings (Sprite 2D/UI, Max 2048)
            ApplyIconImportSettings(outputPath);
#endif

            // 5) Cleanup textures
            Object.DestroyImmediate(fullTex);
            Object.DestroyImmediate(scaledTex);
        }

        /// <summary>
        /// Ensures the output directory exists.
        /// </summary>
        public static bool PrepareDirectory(string directory)
        {
            if (Directory.Exists(directory)) return true;
            try
            {
                Directory.CreateDirectory(directory);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ContentIconCaptureUtility] Failed to create directory '{directory}': {ex.Message}");
                return false;
            }
        }

        // ===================== Batch Capture for Content Packs =====================

        /// <summary>
        /// Opens "Capture Scene" in Single mode and captures icons for the list of prefabs.
        /// Icons are saved alongside a mirrored "Icons" folder (e.g., Prefabs/Foo.prefab -> Icons/Foo_Icon.png).
        /// </summary>
        public static void CaptureIconsForPrefabs(IEnumerable<GameObject> prefabs, int renderSize = 2048,
            int outputSize = 2048, ImageType imageType = ImageType.PNG)
        {
            if (prefabs == null) return;

            RunInCaptureScene(() =>
            {
                var cam = Object.FindObjectOfType<Camera>();
                if (!cam)
                {
                    Debug.LogError("[IconCapture] No Camera found in Capture Scene.");
                    return;
                }

                var captureLocation = GameObject.Find("contentIconCaptureLocation")?.transform;
                if (!captureLocation)
                {
                    Debug.LogError("[IconCapture] 'contentIconCaptureLocation' not found in Capture Scene.");
                    return;
                }

                foreach (var prefab in prefabs)
                {
                    if (!prefab) continue;

                    var prefabPath = AssetDatabase.GetAssetPath(prefab);
                    if (string.IsNullOrEmpty(prefabPath)) continue; // not an asset

                    // Instantiate under the capture location
                    var instance = PrefabUtility.InstantiatePrefab(prefab) as GameObject;
                    if (!instance) continue;
                    instance.transform.SetParent(captureLocation.GetChild(0), false);
                    instance.SetActive(true);

                    captureLocation.GetChild(0).localPosition = Vector3.zero;
                    captureLocation.GetChild(0).localRotation = Quaternion.identity;
                    var entry = FindOffsetFor(prefab.name);
                    if (entry != null)
                    {
                        // Optional scale override first (so position offset is in final local scale)
                        if (entry.scale != null && entry.scale.Length >= 3)
                            instance.transform.localScale = V3(entry.scale, instance.transform.localScale);

                        // Local position/rotation OFFSET relative to the capture root
                        var posOff = V3(entry.position, Vector3.zero);
                        var eulOff = V3(entry.euler, Vector3.zero);

                        captureLocation.GetChild(0).localPosition = posOff;
                        captureLocation.GetChild(0).localRotation = Quaternion.Euler(eulOff);
                    }
                    
                    EncapuslateObjectToBounds(instance, captureLocation);
                    
                    // Save path: mirror Prefabs -> Icons and append "_Icon"
                    string dir = prefabPath.Replace("\\", "/");
                    var fileName = Path.GetFileNameWithoutExtension(dir) + "_Icon";
                    var folder = Path.GetDirectoryName(dir)?.Replace("\\", "/") ?? "Assets";
                    folder = folder.Replace("/Prefabs", "/Icons"); // simple mirror
                    if (string.IsNullOrEmpty(folder)) folder = "Assets/Icons";
                    PrepareDirectory(folder);

                    var finalPathNoExt = Path.Combine(folder, fileName).Replace("\\", "/");
                    CaptureAndSaveIcon(finalPathNoExt, cam, renderSize, outputSize, imageType);

                    Object.DestroyImmediate(instance);
                }

                AssetDatabase.Refresh();
            });
        }

        // ===================== Helpers =====================

        private static void SetToDisplayMesh(GameObject go)
        {
            if (!go) return;
            // Show only children that contain "Display_Mesh" in the name (per your pipeline)
            for (int i = 0; i < go.transform.childCount; ++i)
            {
                var t = go.transform.GetChild(i).gameObject;
                bool show = t.name.Contains("Display_Mesh");
                t.SetActive(show);
            }
        }

        private static void EncapuslateObjectToBounds(GameObject go, Transform captureLocation)
        {
            if (go == null || captureLocation == null) return;

            Debug.Log("EncapuslateObjectToBounds");
            var meshFilters = go.GetComponentsInChildren<MeshFilter>();
            var skinned = go.GetComponentsInChildren<SkinnedMeshRenderer>();
            Bounds worldBounds = new Bounds(go.transform.position, Vector3.zero);

            foreach (var mf in meshFilters)
            {
                var m = mf.sharedMesh;
                if (!m) continue;
                var verts = m.vertices;
                for (int i = 0; i < verts.Length; i++)
                    worldBounds.Encapsulate(mf.transform.TransformPoint(verts[i]));
            }

            foreach (var smr in skinned)
            {
                var m = new Mesh();
                smr.BakeMesh(m);
                var verts = m.vertices;
                for (int i = 0; i < verts.Length; i++)
                    worldBounds.Encapsulate(smr.transform.TransformPoint(verts[i]));
                Object.DestroyImmediate(m);
            }

            if (worldBounds.size == Vector3.zero)
            {
                go.transform.localPosition = Vector3.zero;
                go.transform.localRotation = Quaternion.identity;
                return;
            }

            var center = worldBounds.center;
            float maxDim = Mathf.Max(worldBounds.size.x, worldBounds.size.y);
            float scale = (maxDim > 0f) ? (1f / maxDim) : 1f;

            go.transform.localScale = Vector3.one * scale;
            Vector3 newPos = captureLocation.position - (center - go.transform.position) * scale;
            newPos.x = go.transform.position.x;
            newPos.z = go.transform.position.z;
            go.transform.position = newPos;
            go.transform.localRotation = Quaternion.identity;
        }

        /// <summary>Open ONLY the capture scene to avoid double lighting, run the action, then restore scenes.</summary>
        private static void RunInCaptureScene(System.Action action)
        {
            const string captureSceneName = "Capture Scene";

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            var previousSetup = EditorSceneManager.GetSceneManagerSetup();

            // Find the scene by name anywhere in the project
            string captureScenePath = null;
            foreach (var guid in AssetDatabase.FindAssets($"t:Scene {captureSceneName}"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetFileNameWithoutExtension(path) == captureSceneName)
                {
                    captureScenePath = path;
                    break;
                }
            }

            if (string.IsNullOrEmpty(captureScenePath))
            {
                EditorUtility.DisplayDialog(
                    "Capture Scene Required",
                    $"Could not find a scene named \"{captureSceneName}\".\n\n" +
                    "Please create it (camera + 'contentIconCaptureLocation') and try again.",
                    "OK"
                );
                return;
            }

            try
            {
                EditorSceneManager.OpenScene(captureScenePath, OpenSceneMode.Single);
                action?.Invoke();
            }
            finally
            {
                EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
            }
        }

#if UNITY_EDITOR
        /// <summary>
        /// Post-write import settings for crisp UI icons. 2D/UI sprite + Max 2048 to keep the saved 2K resolution.
        /// </summary>
        private static void ApplyIconImportSettings(string absoluteOrAssetPath)
        {
            // Convert absolute path -> project-relative "Assets/..." if needed
            string assetPath = absoluteOrAssetPath;
            if (Path.IsPathRooted(assetPath))
            {
                var dataPath = Application.dataPath.Replace('\\', '/');
                assetPath = assetPath.Replace('\\', '/');
                if (assetPath.StartsWith(dataPath))
                    assetPath = "Assets" + assetPath.Substring(dataPath.Length);
            }

            // Reimport so Unity picks it up
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[ContentIconCaptureUtility] Importer not found for {assetPath}");
                return;
            }

            importer.textureType = TextureImporterType.Sprite; // 2D and UI
            importer.maxTextureSize = 2048; // keep 2K “as is”
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            importer.SaveAndReimport();
        }

        // ===== Offsets config (JSON) =====
        [System.Serializable]
        private class OffsetConfig
        {
            public OffsetEntry[] entries = System.Array.Empty<OffsetEntry>();
        }

        [System.Serializable]
        private class OffsetEntry
        {
            // Simple wildcard pattern that matches prefab name (case-insensitive), e.g. "Scooter_*", "BMX_Forks_*"
            public string match = "*";

            // Arrays in JSON: [x,y,z]
            public float[] position = new float[3]; // localPosition offset to apply after placement/encapsulation
            public float[] euler = new float[3]; // localRotation offset (Euler degrees)
            public float[] scale = null; // optional localScale override (3 floats) — optional nicety
        }

        private static OffsetConfig _cachedOffsets;

// Try to load IconCaptureOffsets.json from Editor Default Resources, Resources, or anywhere in project.
        private static OffsetConfig LoadOffsetsConfig()
        {
#if UNITY_EDITOR
            //if (_cachedOffsets != null) return _cachedOffsets;

            TextAsset ta = null;

            // 1) Editor Default Resources (path-less load)
            // Put file at: Assets/Editor Default Resources/IconCaptureOffsets.json
            var edr = UnityEditor.EditorGUIUtility.Load("IconCaptureOffsets.json") as TextAsset;
            if (edr != null) ta = edr;

            // 2) Resources/IconCaptureOffsets (Assets/**/Resources/IconCaptureOffsets.json -> name "IconCaptureOffsets")
            if (ta == null) ta = Resources.Load<TextAsset>("IconCaptureOffsets");

            // 3) Fallback: search anywhere in project for the asset by name
            if (ta == null)
            {
                foreach (var guid in UnityEditor.AssetDatabase.FindAssets("IconCaptureOffsets t:TextAsset"))
                {
                    var p = UnityEditor.AssetDatabase.GUIDToAssetPath(guid);
                    var maybe = UnityEditor.AssetDatabase.LoadAssetAtPath<TextAsset>(p);
                    if (maybe != null && (p.EndsWith(".json", System.StringComparison.OrdinalIgnoreCase)))
                    {
                        ta = maybe;
                        break;
                    }
                }
            }

            if (ta == null)
                return _cachedOffsets = new OffsetConfig(); // empty, no entries

            try
            {
                // Unity can’t JsonUtility arrays-of-arrays directly for Vector3, so we store float[3] in JSON.
                var cfg = JsonUtility.FromJson<OffsetConfig>(ta.text);
                return _cachedOffsets = (cfg ?? new OffsetConfig());
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ContentIconCaptureUtility] Failed to parse IconCaptureOffsets.json: {ex.Message}");
                return _cachedOffsets = new OffsetConfig();
            }
#else
    return new OffsetConfig();
#endif
        }

// Simple wildcard match (*) → regex, case-insensitive
        private static bool WildcardMatch(string input, string pattern)
        {
            if (string.IsNullOrEmpty(pattern)) return true;
            pattern = System.Text.RegularExpressions.Regex.Escape(pattern).Replace("\\*", ".*");
            return System.Text.RegularExpressions.Regex.IsMatch(input ?? string.Empty, "^" + pattern + "$",
                System.Text.RegularExpressions.RegexOptions.IgnoreCase);
        }

        private static OffsetEntry FindOffsetFor(string prefabName)
        {
            var cfg = LoadOffsetsConfig();
            if (cfg?.entries == null || cfg.entries.Length == 0) return null;

            // First entry that matches wins (top-down)
            foreach (var e in cfg.entries)
            {
                if (e == null || string.IsNullOrWhiteSpace(e.match)) continue;
                if (WildcardMatch(prefabName, e.match)) return e;
            }

            return null;
        }

        private static Vector3 V3(float[] arr, Vector3 fallback)
        {
            if (arr == null || arr.Length < 3) return fallback;
            return new Vector3(arr[0], arr[1], arr[2]);
        }


#endif
    }
}
