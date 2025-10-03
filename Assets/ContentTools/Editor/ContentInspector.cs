using UnityEditor;
using UnityEngine;
using System.Collections.Generic;

namespace ContentTools.Editor
{
    public class ContentInspector : UnityEditor.EditorWindow
    {
        private string supertypeToSearch = "BMX_"; // Variable for storing user-defined supertype
        private string subtypeToSearch = ""; // Variable for storing user-defined subtype
        private string folderToSearch = "Prefabs"; // Variable for storing user-defined folder filter
        private List<string> cachedPrefabs = new List<string>(); // Cache for storing found prefab names
        private Vector2 scrollPosition; // Tracks the scroll position

        private const string SupertypeKey = "ContentInspector.Supertype";
        private const string SubtypeKey = "ContentInspector.Subtype";
        private const string FolderKey = "ContentInspector.Folder";

        [MenuItem("Tools/MashBox/Content Inspector")]
        static void Init()
        {
            ContentInspector window = (ContentInspector)EditorWindow.GetWindow(typeof(ContentInspector));
            window.Show();
        }

        void OnEnable()
        {
            // Load previously saved values
            supertypeToSearch = EditorPrefs.GetString(SupertypeKey, "");
            subtypeToSearch = EditorPrefs.GetString(SubtypeKey, "");
            folderToSearch = EditorPrefs.GetString(FolderKey, "");
        }

        void OnDisable()
        {
            // Save values when the window is closed or disabled
            EditorPrefs.SetString(SupertypeKey, supertypeToSearch);
            EditorPrefs.SetString(SubtypeKey, subtypeToSearch);
            EditorPrefs.SetString(FolderKey, folderToSearch);
        }

        void OnGUI()
        {
            GUILayout.Label("Search for Prefabs Starting with Supertype:");
            supertypeToSearch = GUILayout.TextField(supertypeToSearch);

            GUILayout.Label("Search for Prefabs Containing Subtype:");
            subtypeToSearch = GUILayout.TextField(subtypeToSearch);

            GUILayout.Label("Search in Folders Containing Name:");
            folderToSearch = GUILayout.TextField(folderToSearch);

            if (GUILayout.Button("Search Prefabs"))
            {
                SearchAndCachePrefabs();
            }

            GUILayout.Label("Cached Prefabs:");
            if (cachedPrefabs.Count > 0)
            {
                scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(200));
                foreach (string prefabName in cachedPrefabs)
                {
                    GUILayout.Label(prefabName);
                }
                EditorGUILayout.EndScrollView();
            }
            else
            {
                GUILayout.Label("No prefabs cached yet.");
            }

            if (GUILayout.Button("Instantiate Prefabs"))
            {
                InstantiateContentPrefabs();
            }
        }

        private void InstantiateContentPrefabs()
        {
            if (cachedPrefabs.Count == 0)
            {
                Debug.LogWarning("No prefabs cached to instantiate. Please search for prefabs first.");
                return;
            }

            string groupName = supertypeToSearch + "_" + supertypeToSearch;
            
            // Create or find a parent GameObject to group instantiated prefabs
            GameObject parentObject = GameObject.Find(groupName);
            if (parentObject == null)
            {
                parentObject = new GameObject(groupName);
                GridLayoutBehaviour gridLayoutBehaviour = parentObject.AddComponent<GridLayoutBehaviour>();
                
                Debug.Log("Created new parent object: InstantiatedPrefabsGroup");
            }

            foreach (string prefabName in cachedPrefabs)
            {
                string[] guids = AssetDatabase.FindAssets(prefabName + " t:Prefab");
                if (guids.Length > 0)
                {
                    string path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    GameObject prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
                    if (prefab != null)
                    {
                        // Specify the parent during instantiation
                        GameObject instantiatedObject = (GameObject)PrefabUtility.InstantiatePrefab(prefab);
                        instantiatedObject.transform.SetParent(parentObject.transform);
                        Debug.Log($"Prefab '{prefabName}' instantiated under 'InstantiatedPrefabsGroup'.");
                    }
                    else
                    {
                        Debug.LogWarning($"Could not load prefab: {prefabName} from path: {path}");
                    }
                }
                else
                {
                    Debug.LogWarning($"No GUIDs found for prefab: {prefabName}. Skipping instantiation.");
                }
            }
        }

        private void SearchAndCachePrefabs()
        {
            if (string.IsNullOrEmpty(supertypeToSearch))
            {
                Debug.LogWarning("Please enter a supertype to search for.");
                return;
            }

            cachedPrefabs.Clear();
            string[] guids = AssetDatabase.FindAssets("t:Prefab");

            Debug.Log($"Searching prefabs starting with '{supertypeToSearch}', containing '{subtypeToSearch}', in folders containing '{folderToSearch}'...");

            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                string prefabName = System.IO.Path.GetFileNameWithoutExtension(path);
                if (prefabName.StartsWith(supertypeToSearch) && (string.IsNullOrEmpty(subtypeToSearch) || prefabName.Contains(subtypeToSearch)))
                {
                    if (string.IsNullOrEmpty(folderToSearch) || path.Contains(folderToSearch))
                    {
                        cachedPrefabs.Add(prefabName);
                        Debug.Log($"Prefab Found: {prefabName} (Path: {path})");
                    }
                }
            }

            Debug.Log("Search completed.");
        }
    }
}