#if UNITY_EDITOR
using System;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Text;
using UnityEditor;
using UnityEngine;
using System.Collections.Generic;
using ContentTools.Editor; // to access ContentPackManifest

namespace ContentCooking
{
    public class UserContentUploaderWindow : EditorWindow
    {
        private string functionUrl = "https://modio-proxy-cgf2e7hvc6fggsh6.centralus-01.azurewebsites.net/ugc/request-upload";


        private string selectedFilePath = "";
        private string statusMessage = "";
        private bool isUploading = false;

        private const string RequiredExt = ".unitypackage";

        // Manifest location (Unity path)
        private const string ManifestUnityFolder = "Assets/Content/PackageManifests";
        private string _lastGeneratedManifestAbsolutePath = null; // still useful for local copies/logging

        [SerializeField] private List<UnityEngine.Object> assets = new List<UnityEngine.Object>();
        [SerializeField] private bool resolveDependencies = true;
        [SerializeField] private bool excludeScripts = true;
        [SerializeField] private bool excludeEditorAssets = true;

        [MenuItem("Tools/Remote Cook Uploader")]
        public static void ShowWindow() => GetWindow<UserContentUploaderWindow>("UGC Remote Cook");

        void OnGUI()
        {
            GUILayout.Label("Upload Content to Remote Cooker", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            GUILayout.Label("1) Upload an existing .unitypackage", EditorStyles.boldLabel);

            if (GUILayout.Button("Select .unitypackage File"))
            {
                string path = EditorUtility.OpenFilePanel("Select .unitypackage to Upload", "", "unitypackage");
                if (!string.IsNullOrEmpty(path) && IsUnityPackage(path))
                {
                    selectedFilePath = path;
                    statusMessage = $"Selected package: {Path.GetFileName(selectedFilePath)}";
                }
                else
                {
                    EditorUtility.DisplayDialog("Invalid File", $"Please select a {RequiredExt} file.", "OK");
                }
            }

            if (!string.IsNullOrEmpty(selectedFilePath))
                GUILayout.Label($"Selected: {Path.GetFileName(selectedFilePath)}");
            else
                EditorGUILayout.HelpBox($"No file selected. Choose a {RequiredExt}, or create one below.", MessageType.Info);

            using (new EditorGUI.DisabledScope(isUploading || string.IsNullOrEmpty(selectedFilePath) || !IsUnityPackage(selectedFilePath)))
            {
                if (GUILayout.Button("Upload to Remote Cooker"))
                    UploadSelectedFile();
            }

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

            GUILayout.Label("2) Build a .unitypackage from Assets (Prefabs, etc.)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Drag prefabs here. Dependencies (materials, meshes, etc.) will be gathered automatically.\n" +
                "A manifest will be generated into Assets/Content/PackageManifests and included in the package.",
                MessageType.Info);

            using (var so = new SerializedObject(this))
            {
                var listProp = so.FindProperty("assets");
                EditorGUILayout.PropertyField(listProp, true);
                so.ApplyModifiedProperties();
            }

            resolveDependencies   = EditorGUILayout.ToggleLeft("Resolve Dependencies", resolveDependencies);
            excludeScripts        = EditorGUILayout.ToggleLeft("Exclude Scripts (.cs)", excludeScripts);
            excludeEditorAssets   = EditorGUILayout.ToggleLeft("Exclude Editor Assets (/Editor/)", excludeEditorAssets);

            if (GUILayout.Button("Create UnityPackage (with dependencies + manifest) & Select for Upload", GUILayout.Height(26)))
                CreatePackageAndSelectForUpload();

            EditorGUILayout.Space();
            GUILayout.Label(statusMessage, EditorStyles.helpBox);
        }

        private static bool IsUnityPackage(string path) =>
            string.Equals(Path.GetExtension(path), RequiredExt, StringComparison.OrdinalIgnoreCase);

        // ---------- Upload ONLY the .unitypackage ----------
        private async void UploadSelectedFile()
        {
            isUploading = true;
            statusMessage = "Requesting upload URL...";
            Repaint();

            try
            {
                using (HttpClient client = new HttpClient())
                {
                    var content = new StringContent("{}", Encoding.UTF8, "application/json");
                    var response = await client.PostAsync(functionUrl, content);
                    response.EnsureSuccessStatusCode();

                    string json = await response.Content.ReadAsStringAsync();
                    var data = JsonUtility.FromJson<UploadResponse>(json);

                    if (data == null || string.IsNullOrEmpty(data.uploadUrl))
                        throw new Exception("Invalid response from upload function.");

                    // Upload ONLY the .unitypackage
                    statusMessage = "Uploading .unitypackage...";
                    Repaint();

                    byte[] fileBytes = File.ReadAllBytes(selectedFilePath);
                    var fileContent = new ByteArrayContent(fileBytes);
                    fileContent.Headers.Add("x-ms-blob-type", "BlockBlob");
                    var uploadResponse = await client.PutAsync(data.uploadUrl, fileContent);

                    if (!uploadResponse.IsSuccessStatusCode)
                        throw new Exception($"Package upload failed: {uploadResponse.StatusCode}");

                    statusMessage = "✅ Upload complete! (Manifest is inside the package)";
                    Debug.Log("[Uploader] Package uploaded. Manifest is included within the .unitypackage.");
                }
            }
            catch (Exception ex)
            {
                statusMessage = $"❌ Error: {ex.Message}";
                Debug.LogError(statusMessage);
            }

            isUploading = false;
            Repaint();
        }

        [Serializable]
        private class UploadResponse
        {
            public string jobId;
            public string uploadUrl;
            // NOTE: no manifestUploadUrl — we do not upload manifest separately anymore
        }

        // ---------- Build Package + Manifest (into Assets/Content/PackageManifests), include manifest in package ----------
        private void CreatePackageAndSelectForUpload()
        {
            var roots = GetAssetPaths(assets);
            if (roots.Count == 0)
            {
                EditorUtility.DisplayDialog("No Assets", "Add at least one prefab to export.", "OK");
                return;
            }

            string[] exportPaths = resolveDependencies
                ? BuildDependencySet(roots, excludeScripts, excludeEditorAssets).ToArray()
                : roots.Distinct().ToArray();

            try
            {
                // 1) Ensure manifest folder exists (disk + Unity)
                string projectRoot = GetProjectRoot();
                string manifestFolderAbs = Path.Combine(projectRoot, ManifestUnityFolder.Replace('/', Path.DirectorySeparatorChar));
                EnsureDirectoryExists(manifestFolderAbs);

                // 2) Generate manifest JSON (named by first prefab or timestamp)
                string baseName = TryGetBaseNameFromRoots(roots, "content_pack");
                string manifestFileName = $"{baseName}_manifest.json";
                string manifestAbsPath = Path.Combine(manifestFolderAbs, manifestFileName);

                var prefabGuids = roots
                    .Where(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase))
                    .Select(AssetDatabase.AssetPathToGUID)
                    .ToList();

                ContentPackManifest.GenerateManifest(prefabGuids, manifestAbsPath);

                // 3) Import manifest as Unity asset and include it
                string manifestUnityPath = $"{ManifestUnityFolder}/{manifestFileName}".Replace("\\", "/");
                AssetDatabase.ImportAsset(manifestUnityPath, ImportAssetOptions.ForceUpdate);

                var exportSet = new LinkedHashSet<string>(exportPaths);
                exportSet.Add(manifestUnityPath);
                var finalExportPaths = exportSet.ToList();

                // 4) Export package silently to Temp
                string tempPackagePath = GetProjectTempPackagePath();
                EnsureDirectoryExists(Path.GetDirectoryName(tempPackagePath));

                EditorUtility.DisplayProgressBar("Exporting Package",
                    $"Creating .unitypackage ({finalExportPaths.Count} items, incl. manifest)...", 0.6f);

                AssetDatabase.ExportPackage(finalExportPaths.ToArray(), tempPackagePath, ExportPackageOptions.Default);
                AssetDatabase.Refresh();

                if (!File.Exists(tempPackagePath))
                    throw new FileNotFoundException("Export failed — package file not found.", tempPackagePath);

                // 5) Copy package to Desktop (optional convenience copy)
                string desktopPackage = CopyPackageToDesktop(tempPackagePath, "RemoteCook_");

                // Remember manifest path (useful for debugging/local inspection)
                _lastGeneratedManifestAbsolutePath = manifestAbsPath;

                EditorUtility.ClearProgressBar();

                // 6) Auto-select package for upload
                selectedFilePath = desktopPackage;
                statusMessage = $"Package created & selected (manifest included): {Path.GetFileName(selectedFilePath)}";
                EditorUtility.DisplayDialog("Export Complete",
                    $"Saved and selected for upload:\n{desktopPackage}\nManifest included: {manifestUnityPath}",
                    "Reveal");
                EditorUtility.RevealInFinder(desktopPackage);
                Repaint();
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[UserContentUploaderWindow] Export failed: {ex.Message}");
                EditorUtility.DisplayDialog("Export Failed", ex.Message, "OK");
            }
        }

        // ---------- Utilities ----------
        private static List<string> BuildDependencySet(List<string> rootPaths, bool excludeScripts, bool excludeEditor)
        {
            var deps = AssetDatabase.GetDependencies(rootPaths.ToArray(), true);
            IEnumerable<string> filtered = deps.Where(p => p.StartsWith("Assets", StringComparison.OrdinalIgnoreCase));

            if (excludeScripts)
                filtered = filtered.Where(p => !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase));
            if (excludeEditor)
                filtered = filtered.Where(p => !p.Contains("/Editor/"));

            return new LinkedHashSet<string>(filtered).ToList();
        }

        private static List<string> GetAssetPaths(List<UnityEngine.Object> items)
        {
            var paths = new List<string>();
            foreach (var a in items)
            {
                if (a == null) continue;
                string p = AssetDatabase.GetAssetPath(a);
                if (!string.IsNullOrEmpty(p)) paths.Add(p);
            }
            return paths;
        }

        private static string GetProjectRoot() =>
            Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string GetProjectTempPackagePath()
        {
            string projectRoot = GetProjectRoot();
            string tempDir = Path.Combine(projectRoot, "Temp");
            return Path.Combine(tempDir, "RemoteCook_TempUpload.unitypackage");
        }

        private static void EnsureDirectoryExists(string dir)
        {
            if (!Directory.Exists(dir))
                Directory.CreateDirectory(dir);
        }

        private static string CopyPackageToDesktop(string sourceFile, string prefix)
        {
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory);
            string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string dest = Path.Combine(desktop, $"{prefix}{stamp}.unitypackage");
            File.Copy(sourceFile, dest, true);
            return dest;
        }

        private static string TryGetBaseNameFromRoots(List<string> roots, string fallback)
        {
            string prefabPath = roots.FirstOrDefault(p => p.EndsWith(".prefab", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(prefabPath))
                return Path.GetFileNameWithoutExtension(prefabPath);

            if (roots.Count > 0)
            {
                var lastSegment = Path.GetFileNameWithoutExtension(roots[0]);
                if (!string.IsNullOrEmpty(lastSegment))
                    return lastSegment;
            }

            return $"{fallback}_{DateTime.Now:yyyyMMdd_HHmmss}";
        }

        private class LinkedHashSet<T> : IEnumerable<T>
        {
            private readonly HashSet<T> _set = new HashSet<T>();
            private readonly List<T> _list = new List<T>();
            public LinkedHashSet() { }
            public LinkedHashSet(IEnumerable<T> items) { foreach (var i in items) Add(i); }
            public bool Add(T item)
            {
                if (_set.Add(item)) { _list.Add(item); return true; }
                return false;
            }
            public IEnumerator<T> GetEnumerator() => _list.GetEnumerator();
            System.Collections.IEnumerator System.Collections.IEnumerable.GetEnumerator() => _list.GetEnumerator();
            public List<T> ToList() => new List<T>(_list);
        }
    }
}
#endif
