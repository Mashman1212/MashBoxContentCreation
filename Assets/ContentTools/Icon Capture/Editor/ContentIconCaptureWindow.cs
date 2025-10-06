using System.Collections.Generic;
using System.IO;
using Content_Icon_Capture.Editor;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace ContentTools.Icon_Capture.Editor
{
    public class ContentIconCaptureWindow : EditorWindow
    {
        private Camera _captureCamera;
        private string _saveDirectory; // Save directory only
        private const string DefaultFilename = "DepthWithPyramidStacked"; // Fixed filename

        // Enum for predefined square resolutions
        private enum SquareResolution
        {
            _64 = 64,
            _128 = 128,
            _256 = 256,
            _512 = 512,
            _1024 = 1024,
            _2048 = 2048,
            _4096 = 4096
        }

        private SquareResolution _renderResolution = SquareResolution._2048; // Default render resolution
        private SquareResolution _outputResolution = SquareResolution._2048;  // Default output resolution
        
        private ContentIconCaptureUtility.ImageType _outputType = ContentIconCaptureUtility.ImageType.PNG;  // Default output resolution

        // Keys for saving preferences
        private const string PrefSaveDirectory = "ContentIconCapture_SaveDirectory";
        private const string PrefRenderResolution = "ContentIconCapture_RenderResolution";
        private const string PrefOutputResolution = "ContentIconCapture_OutputResolution";

        class CaptureObj
        {
            public CaptureObj(string prefabPath, GameObject instantiatedObj)
            {
                InstantiatedObj = instantiatedObj;
                PrefabPath = prefabPath;
            }
            
            public GameObject InstantiatedObj;
            public string PrefabPath;
        }
        
        private readonly Queue<CaptureObj> _instantiatedObjects = new Queue<CaptureObj>();
        private GameObject _captureLocationGo;

        [MenuItem("Tools/MashBox/Create Content Icons")]
        private static void Init() => GetWindow<ContentIconCaptureWindow>("Content Icon Creator").Show();

        private void OnEnable()
        {
            // Use the generic method to find the camera and location
            _captureCamera = FindObjectOfType<Camera>();//FindCaptureObject<Camera>("contentIconCaptureCamera");
            _captureLocationGo = FindCaptureObject<Transform>("contentIconCaptureLocation")?.gameObject;

            // Load previous preferences
            LoadPreferences();
        }

        private void OnDisable()
        {
            // Save settings when the window closes
            SavePreferences();
        }

        private void OnGUI()
        {
            // Automatically find objects if not assigned
            if (_captureCamera == null)
            {
                _captureCamera = FindObjectOfType<Camera>();
            }

            if (_captureLocationGo == null)
            {
                _captureLocationGo = FindCaptureObject<Transform>("contentIconCaptureLocation")?.gameObject;
            }
            
            if (GUILayout.Button("Capture Selection"))
            {
                RunInCaptureScene(() =>
                {
                    // Refresh references now that the capture scene is loaded
                    _captureCamera = FindObjectOfType<Camera>();
                    _captureLocationGo = FindCaptureObject<Transform>("contentIconCaptureLocation")?.gameObject;

                    InstantiateSelected();
                    CaptureAll();
                });
            }
        }

        void CaptureAndSave()
        {
            CaptureAndSave(_saveDirectory,DefaultFilename,ContentIconCaptureUtility.ImageType.PNG);
        }

        void CaptureAndSave(string directory,string fileName,ContentIconCaptureUtility.ImageType imageType)
        {
            if (_captureCamera == null)
            {

                Debug.LogError("[ContentIconCaptureUtility] No camera assigned to capture depth.");
                Debug.LogError("Please assign a Camera in the Capture Camera field.");
                return;
            }

            if (!ContentIconCaptureUtility.PrepareDirectory(directory)) return;

            string fullSavePath = Path.Combine(directory, fileName);
            try
            {
                // Using the render and output resolutions as integer values
                int renderSize = (int)_renderResolution;
                int outputSize = (int)_outputResolution;
                ContentIconCaptureUtility.CaptureAndSaveIcon(fullSavePath, _captureCamera, renderSize, outputSize,imageType);
                Debug.Log($"[ContentIconCaptureUtility] Pyramid-stacked depth saved to {fullSavePath}");
                Debug.Log("[ContentIconCaptureUtility] Icon capture process is complete.");
            }
            catch (System.Exception ex)
            {

                Debug.LogError($"[ContentIconCaptureUtility] Error during capture: {ex.Message}");
                Debug.LogError("Check the camera and directory settings for issues.");
            }
        }
        
        public void DeleteInstantiated()
        {
            while(_instantiatedObjects.Count > 0)
            {
                CaptureObj cpObj = _instantiatedObjects.Dequeue();
                if (cpObj.InstantiatedObj != null)
                {
                    DestroyImmediate(cpObj.InstantiatedObj);
                }
            }
        }

        private CaptureObj _currentCaptureObject;
        void StageNextObject()
        {
            if (_instantiatedObjects.Count > 0)
            {
                while (_instantiatedObjects.Count > 0)
                {
                    _currentCaptureObject = _instantiatedObjects.Dequeue();
                    if (_currentCaptureObject != null)
                    {
                        break;
                    }
                }
                
                if (_currentCaptureObject.InstantiatedObj)
                {
                    _currentCaptureObject.InstantiatedObj.SetActive(true);
                    //SetToDisplayMesh(_currentCaptureObject.InstantiatedObj);

                    EncapuslateObjectToBounds(_currentCaptureObject.InstantiatedObj);
                }
            }
        }

        void CaptureNext()
        {
            StageNextObject();
            string dir = _currentCaptureObject.PrefabPath.Replace("Prefabs","Icons");
            dir = dir.Replace(Path.GetFileName(dir), "");
            Debug.Log(dir);
            CaptureAndSave(dir,_currentCaptureObject.InstantiatedObj.name + "_Icon", _outputType);
            DestroyCurrentObject();

            AssetDatabase.Refresh();
        }
        
        void CaptureAll()
        {
            while (_instantiatedObjects.Count > 0)
            {
                CaptureNext();
            }
        }
        
        void DestroyCurrentObject()
        {
            if (_currentCaptureObject.InstantiatedObj)
            {
                DestroyImmediate(_currentCaptureObject.InstantiatedObj);
            }
        }

        
        void SetToDisplayMesh(GameObject go)
        {
            //Enable Display_Mesh Only
            for (int i = 0; i < go.transform.childCount; ++i)
            {
                Transform trans = go.transform.GetChild(i);
                trans.gameObject.SetActive(false);

                if (trans.name.Contains("Display_Mesh"))
                {
                    trans.gameObject.SetActive(true);
                }
            }
        }


        void EncapuslateObjectToBounds(GameObject go)
        {
            if (go == null)
            {
                Debug.LogError("GameObject is null. Can't encapsulate null object to bounds.");
                return;
            }

            // Calculate the bounds of the GameObject
            MeshFilter[] meshFilters = go.GetComponentsInChildren<MeshFilter>();
            SkinnedMeshRenderer[] skinnedMeshRenderers = go.GetComponentsInChildren<SkinnedMeshRenderer>();
            Bounds worldBounds = new Bounds(go.transform.position, Vector3.zero);
            foreach (MeshFilter mf in meshFilters)
            {
                foreach (Vector3 vertex in mf.sharedMesh.vertices)
                {
                    Vector3 worldVertex = mf.transform.TransformPoint(vertex);
                    worldBounds.Encapsulate(worldVertex);
                }
            }

            foreach (SkinnedMeshRenderer smr in skinnedMeshRenderers)
            {
                Mesh mesh = new Mesh();
                smr.BakeMesh(mesh);
                foreach (Vector3 vertex in mesh.vertices)
                {
                    Vector3 worldVertex = smr.transform.TransformPoint(vertex);
                    worldBounds.Encapsulate(worldVertex);
                }
            }

            Renderer[] renderers = go.GetComponentsInChildren<Renderer>();
            if (renderers.Length > 0)
            {
                Debug.Log("Encapsulating object to bounds...");
                // Get the center of the bounds
                Vector3 center = worldBounds.center;
                // Calculate max dimension of the bounds
                float maxDimension = Mathf.Max(worldBounds.size.x, worldBounds.size.y);
                // Calculate scale factor
                float scaleFactor = 1.0f / maxDimension;
                // Scale the GameObject
                go.transform.localScale = new Vector3(scaleFactor, scaleFactor, scaleFactor);
                // Set the position of the new GameObject to align its center with its parent's pivot
                go.transform.position = _captureLocationGo.transform.position -
                                        (center - go.transform.position) * scaleFactor;
                Debug.Log($"Object {go.name} has been encapsulated to bounds with scale factor {scaleFactor}.");
            }
            else
            {
                Debug.Log("No renderers found on the GameObject. Setting position to zero and rotation to identity...");
                go.transform.localPosition = Vector3.zero;
            }

            go.transform.localRotation = Quaternion.identity;
            Debug.Log(
                $"Final position: {go.transform.position}, scale: {go.transform.localScale}, rotation: {go.transform.localRotation} for the object {go.name}.");
        }


        public void InstantiateSelected()
        {
            // Get the selected objects
            Object[] selectedObjects = Selection.objects;
            if (selectedObjects.Length == 0)
            {
                Debug.LogWarning("[ContentIconCaptureUtility] No objects selected!");
                Debug.LogWarning("Please select a prefab to instantiate.");
                return;
            }
            
            foreach (Object obj in selectedObjects)
            {
                // Check if the object is a prefab asset
                if (PrefabUtility.IsPartOfPrefabAsset(obj))
                {
                    Debug.Log(AssetDatabase.GetAssetPath(obj));
                    
                    // Instantiate the prefab in the scene
                    GameObject prefabInstance = (GameObject)PrefabUtility.InstantiatePrefab(obj);
                    
                    _instantiatedObjects.Enqueue(new CaptureObj(AssetDatabase.GetAssetPath(obj),prefabInstance));

                    if (_captureLocationGo != null)
                    {
                        prefabInstance.transform.parent = _captureLocationGo.transform;
                        prefabInstance.transform.localPosition = Vector3.zero;
                        prefabInstance.transform.localRotation = Quaternion.identity;

                        //SetToDisplayMesh(prefabInstance);
                        
                        //disable so we dont capture all items in list at same time
                        prefabInstance.SetActive(false);

                    }
                    else
                    {
                        Debug.LogError("[ContentIconCaptureUtility] No capture location set!");
                        Debug.LogError("Specify a valid capture location in Capture Location field.");
                    }

                    if (prefabInstance != null)
                    {
                        Debug.Log($"[ContentIconCaptureUtility] Instantiated prefab: {obj.name}");
                        Debug.Log("[ContentIconCaptureUtility] Prefab successfully added to the scene.");
                    }
                    else
                    {
                        Debug.LogError($"[ContentIconCaptureUtility] Failed to instantiate prefab: {obj.name}");
                        Debug.LogError("Ensure the prefab is valid and compatible for instantiation.");
                    }
                }
                else
                {
                    Debug.LogWarning($"[ContentIconCaptureUtility] Selected object '{obj.name}' is not a prefab!");
                    Debug.LogWarning("Only prefab assets can be instantiated.");
                }
            }
        }

        // Generalized method to find an object by name and type
        private T FindCaptureObject<T>(string objectName) where T : Component
        {
            // Find an object by name in the scene
            GameObject targetObject = GameObject.Find(objectName);
            if (targetObject != null)
            {
                T component = targetObject.GetComponent<T>();
                if (component != null)
                {
                    Debug.Log($"[ContentIconCaptureUtility] Found {typeof(T).Name}: {objectName}");
                    Debug.Log("[ContentIconCaptureUtility] Capture object discovery complete.");
                    return component;
                }
                else
                {
                    Debug.LogWarning($"[ContentIconCaptureUtility] GameObject '{objectName}' does not have a {typeof(T).Name} component!");
                }
            }
            else
            {
                Debug.LogWarning($"[ContentIconCaptureUtility] Could not find a GameObject named '{objectName}' in the current scene.");
            }
            return null;
        }
        
        /// Opens "Capture Scene" in Single mode, runs action, then restores prior scenes.
        private static void RunInCaptureScene(System.Action action)
        {
            const string captureSceneName = "Capture Scene";

            // Save dirty scenes if user agrees
            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo())
                return;

            // Snapshot current scene setup so we can restore later
            var previousSetup = EditorSceneManager.GetSceneManagerSetup();

            // Locate the scene asset by name (robust to folder moves)
            string captureScenePath = null;
            foreach (var guid in AssetDatabase.FindAssets($"t:Scene {captureSceneName}"))
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == captureSceneName)
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
                // Open ONLY the capture scene to avoid double-lighting
                EditorSceneManager.OpenScene(captureScenePath, OpenSceneMode.Single);

                // Run the caller’s work (capture, etc.)
                action?.Invoke();
            }
            finally
            {
                // Always restore the user's scenes, even if something throws
                EditorSceneManager.RestoreSceneManagerSetup(previousSetup);
            }
        }

        
        /// <summary>
        /// Ensures the "Capture Scene" is loaded. Returns false if it can't be found.
        /// </summary>
        private static bool EnsureCaptureSceneLoaded()
        {
            const string captureSceneName = "Capture Scene";

            // If a scene with that name is already present and loaded, do nothing.
            var existing = SceneManager.GetSceneByName(captureSceneName);
            if (existing.IsValid() && existing.isLoaded)
                return true;

            // Try to locate the scene asset by name.
            // This is resilient to folder moves/renames.
            string[] guids = AssetDatabase.FindAssets($"t:Scene {captureSceneName}");
            foreach (var guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (System.IO.Path.GetFileNameWithoutExtension(path) == captureSceneName)
                {
                    EditorSceneManager.OpenScene(path, OpenSceneMode.Additive);
                    // Optionally make it the active scene (if your capture objects live there)
                    var loaded = SceneManager.GetSceneByPath(path);
                    if (loaded.IsValid())
                        SceneManager.SetActiveScene(loaded);
                    return true;
                }
            }

            // Not found — tell the user what to do.
            EditorUtility.DisplayDialog(
                "Capture Scene Required",
                $"Could not find a scene named \"{captureSceneName}\".\n\n" +
                "Please create it (and include your capture rig: camera + 'contentIconCaptureLocation') " +
                "or rename the existing capture scene to match.",
                "OK"
            );
            return false;
        }


        private void SavePreferences()
        {
            // Save resolutions and save directory
            EditorPrefs.SetString(PrefSaveDirectory, _saveDirectory);
            EditorPrefs.SetInt(PrefRenderResolution, (int)_renderResolution);
            EditorPrefs.SetInt(PrefOutputResolution, (int)_outputResolution);
        }

        private void LoadPreferences()
        {
            // Load settings
            _saveDirectory = EditorPrefs.GetString(PrefSaveDirectory, "C:/Thumbnails/");
            _renderResolution = (SquareResolution)EditorPrefs.GetInt(PrefRenderResolution, (int)SquareResolution._1024);
            _outputResolution = (SquareResolution)EditorPrefs.GetInt(PrefOutputResolution, (int)SquareResolution._128);
        }
    }
}