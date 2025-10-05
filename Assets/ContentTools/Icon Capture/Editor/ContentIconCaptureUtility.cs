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
        public static void CaptureAndSaveIcon(string outputPath, Camera captureCamera, int renderSize, int outputSize, ImageType imageType)
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
        public static void CaptureIconsForPrefabs(IEnumerable<GameObject> prefabs, int renderSize = 2048, int outputSize = 2048, ImageType imageType = ImageType.PNG)
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
                    instance.transform.SetParent(captureLocation, false);
                    instance.SetActive(true);

                    SetToDisplayMesh(instance);
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
            go.transform.position = captureLocation.position - (center - go.transform.position) * scale;
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

            importer.textureType = TextureImporterType.Sprite;                  // 2D and UI
            importer.maxTextureSize = 2048;                                     // keep 2K “as is”
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed;

            importer.SaveAndReimport();
        }
#endif
    }
}
