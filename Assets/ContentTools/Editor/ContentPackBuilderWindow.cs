#if UNITY_EDITOR
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using System.IO;

namespace ContentTools.Editor
{
    public class ContentPackBuilderWindow : EditorWindow
    {
        // ---- EditorPrefs keys for persistence ----
        private const string PREF_KEY_BUILD_LOCATION = "ContentPackBuilder.BuildLocation";
        private const string PREF_KEY_PACKS_FOLDER   = "ContentPackBuilder.PacksFolder";

        private ContentPackDefinition[] _packs;
        private bool[] _selected;
        private bool[] _foldouts; // show pack contents

        private AddressableAssetSettings _settings;

        // Single, simple destination for bundles/manifests
        private string _buildLocation = string.Empty; // filesystem folder

        // Creation helpers
        private string _newPackName = "NewContentPack";
        private string _packsFolder; // where to create packs

        [MenuItem("Window/MashBox/Content Pack Builder")]
        public static void Open()
        {
            GetWindow<ContentPackBuilderWindow>(true, "Content Pack Builder");
        }

        private void OnEnable()
        {
            _settings = AddressableAssetSettingsDefaultObject.Settings;
            _buildLocation = EditorPrefs.GetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
            _packsFolder   = EditorPrefs.GetString(PREF_KEY_PACKS_FOLDER, "Assets/ContentPacks");
            RefreshPacks();
        }

        private void OnDisable()
        {
            if (!string.IsNullOrEmpty(_buildLocation))
                EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
            else
                EditorPrefs.DeleteKey(PREF_KEY_BUILD_LOCATION);

            if (!string.IsNullOrEmpty(_packsFolder))
                EditorPrefs.SetString(PREF_KEY_PACKS_FOLDER, _packsFolder);
            else
                EditorPrefs.DeleteKey(PREF_KEY_PACKS_FOLDER);
        }

        private void RefreshPacks()
        {
            var guids = AssetDatabase.FindAssets("t:ContentPackDefinition");
            _packs = guids
                .Select(g => AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(AssetDatabase.GUIDToAssetPath(g)))
                .Where(p => p != null)
                .OrderBy(p => p.PackName)
                .ToArray();

            _selected = new bool[_packs.Length];
            _foldouts = new bool[_packs.Length];

            foreach (ContentPackDefinition pack in _packs)
            {
                pack.OnValidate();
            }
            
            for (int i = 0; i < _selected.Length; i++)
            {
                _selected[i] = true;
                _foldouts[i] = false;
            }
        }

        private Vector2 _scroll;

        private void OnGUI()
        {
            if (_settings == null)
            {
                EditorGUILayout.HelpBox("Addressables settings not found.", MessageType.Error);
                if (GUILayout.Button("Refresh")) OnEnable();
                return;
            }
            

            // ===== Build Location =====
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Build Location", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginChangeCheck();
            var newBuildLocation = EditorGUILayout.TextField(
                new GUIContent("Build Location", "Filesystem folder where this tool will emit bundles for the selected packs."),
                _buildLocation);
            if (EditorGUI.EndChangeCheck())
            {
                _buildLocation = (newBuildLocation ?? string.Empty).Replace("\\", "/");
                if (!string.IsNullOrEmpty(_buildLocation))
                    EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
                else
                    EditorPrefs.DeleteKey(PREF_KEY_BUILD_LOCATION);
            }

            if (GUILayout.Button("Browse", GUILayout.MaxWidth(70)))
            {
                string picked = EditorUtility.OpenFolderPanel(
                    "Select Build Location",
                    string.IsNullOrEmpty(_buildLocation) ? Application.dataPath : _buildLocation,
                    "");
                if (!string.IsNullOrEmpty(picked))
                {
                    _buildLocation = picked.Replace("\\", "/");
                    EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
                }
            }
            EditorGUILayout.EndHorizontal();

            // ===== Create / Delete Packs =====
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Manage Packs", EditorStyles.boldLabel);

            using (new EditorGUILayout.VerticalScope("box"))
            {
                // Folder selector
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Packs Folder", GUILayout.Width(95));
                EditorGUI.BeginChangeCheck();
                _packsFolder = EditorGUILayout.TextField(_packsFolder);
                if (EditorGUI.EndChangeCheck())
                {
                    if (!string.IsNullOrEmpty(_packsFolder))
                        EditorPrefs.SetString(PREF_KEY_PACKS_FOLDER, _packsFolder);
                }
                if (GUILayout.Button("Choose…", GUILayout.MaxWidth(80)))
                {
                    string start = string.IsNullOrEmpty(_packsFolder) ? "Assets" : _packsFolder;
                    var abs = EditorUtility.OpenFolderPanel("Choose Packs Folder", Application.dataPath, "");
                    if (!string.IsNullOrEmpty(abs))
                    {
                        if (!abs.StartsWith(Application.dataPath))
                        {
                            EditorUtility.DisplayDialog("Invalid Folder",
                                "Please pick a folder under your project's Assets/ directory.", "OK");
                        }
                        else
                        {
                            _packsFolder = "Assets" + abs.Substring(Application.dataPath.Length);
                            EditorPrefs.SetString(PREF_KEY_PACKS_FOLDER, _packsFolder);
                        }
                    }
                }
                EditorGUILayout.EndHorizontal();

                // Create row
                EditorGUILayout.BeginHorizontal();
                _newPackName = EditorGUILayout.TextField(new GUIContent("New Pack Name"), _newPackName,GUILayout.MinWidth(300));
                GUI.enabled = !string.IsNullOrWhiteSpace(_newPackName);
                if (GUILayout.Button("Create Pack", GUILayout.MaxWidth(110)))
                {
                    CreatePackAsset(_newPackName.Trim(), _packsFolder);
                    _newPackName = "NewContentPack";
                }
                GUI.enabled = true;

                GUILayout.FlexibleSpace();

                // Delete selected
                GUI.enabled = _packs != null && _packs.Length > 0 && _selected.Any(x => x);
                if (GUILayout.Button("Delete Selected", GUILayout.MaxWidth(130)))
                {
                    DeleteSelectedPacks();
                }
                GUI.enabled = true;
                EditorGUILayout.EndHorizontal();
            }

            // ===== Packs list =====
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Packs", EditorStyles.boldLabel);
            if (_packs == null || _packs.Length == 0)
            {
                EditorGUILayout.HelpBox("No ContentPackDefinition assets found. Create one above.", MessageType.Info);
                if (GUILayout.Button("Refresh")) RefreshPacks();
            }
            else
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(240));
                for (int i = 0; i < _packs.Length; i++)
                {
                    var p = _packs[i];
                    EditorGUILayout.BeginVertical("box");

                    // Header row
                    EditorGUILayout.BeginHorizontal();
                    _selected[i] = EditorGUILayout.Toggle(_selected[i], GUILayout.Width(18));
                    _foldouts[i] = EditorGUILayout.Foldout(_foldouts[i], p.PackName, true);
                    GUILayout.FlexibleSpace();

                    // Quick actions
                    if (GUILayout.Button("Open", GUILayout.MaxWidth(58)))
                        Selection.activeObject = p;

                    if (GUILayout.Button("Ping", GUILayout.MaxWidth(58)))
                        EditorGUIUtility.PingObject(p);

                    if (GUILayout.Button("Sync Now", GUILayout.MaxWidth(80)))
                        p.SyncToAddressables();

                    if (GUILayout.Button("Delete", GUILayout.MaxWidth(70)))
                        DeletePack(p);

                    EditorGUILayout.EndHorizontal();

                    // Info + contents
                    using (new EditorGUI.IndentLevelScope())
                    {
                        var groupName = p.PackName;
                        EditorGUILayout.LabelField("Addressables Group", groupName);

                        int count = p._items != null ? p._items.Count(go => go != null) : 0;
                        EditorGUILayout.LabelField("Prefabs", count.ToString());

                        if (_foldouts[i])
                        {
                            EditorGUILayout.Space(2);
                            if (count == 0)
                            {
                                EditorGUILayout.HelpBox("Drop prefabs onto this asset to include them in the pack.", MessageType.None);
                            }
                            else
                            {
                                foreach (var go in p._items)
                                {
                                    EditorGUILayout.BeginHorizontal();
                                    GUIContent icon = EditorGUIUtility.ObjectContent(go, typeof(GameObject));
                                    GUILayout.Label(icon.image, GUILayout.Width(20), GUILayout.Height(18));
                                    EditorGUILayout.ObjectField(go, typeof(GameObject), false);
                                    EditorGUILayout.EndHorizontal();
                                }
                            }
                        }
                    }

                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndScrollView();
            }

            // ===== Actions =====
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Build Selected")) BuildSelected(false);
            if (GUILayout.Button("Build All")) BuildSelected(true);
            if (GUILayout.Button("Refresh")) RefreshPacks();
            EditorGUILayout.EndHorizontal();
            
        }

        private void BuildSelected(bool all)
        {
            if (_settings == null)
            {
                Debug.LogError("Addressables settings not found.");
                return;
            }

            var opts = new AddressablesPackBuilder.BuildOptions
            {
                // Hidden from UI: just use active profile
                profileId = _settings.activeProfileId,
                rebuildPlayerContent = true,
                enableRemoteCatalog = true,
                disableOtherGroups = true,
                writeManifestJson = true,
                manifestFileName = null,
                setPlayerVersionOverride = true,

                sessionRemoteBuildRootOverride = string.IsNullOrEmpty(_buildLocation) ? null : _buildLocation,
                sessionRemoteLoadRootOverride = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}",
            };

            int built = 0;
            for (int i = 0; i < _packs.Length; i++)
            {
                if (!all && !_selected[i]) continue;
                AddressablesPackBuilder.BuildPack(_packs[i], opts);
                built++;
            }
            Debug.Log(built > 0 ? $"Built {built} content pack(s)." : "Nothing to build.");
        }

        // ===== Create / Delete helpers =====

        private void CreatePackAsset(string name, string folder)
        {
            if (string.IsNullOrWhiteSpace(name)) return;

            // Ensure folder
            if (string.IsNullOrEmpty(folder)) folder = "Assets/ContentPacks";
            if (!AssetDatabase.IsValidFolder(folder))
            {
                string parent = "Assets";
                foreach (var seg in folder.Replace("\\", "/").Split('/').Skip(1))
                {
                    if (string.IsNullOrEmpty(seg)) continue;
                    string next = parent + "/" + seg;
                    if (!AssetDatabase.IsValidFolder(next))
                        AssetDatabase.CreateFolder(parent, seg);
                    parent = next;
                }
            }

            // Unique asset path
            string path = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(folder, name + ".asset"));

            var asset = ScriptableObject.CreateInstance<ContentPackDefinition>();
            asset.OnValidate();
            
            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);

            RefreshPacks();
        }

        private void DeleteSelectedPacks()
        {
            var toDelete = new List<ContentPackDefinition>();
            for (int i = 0; i < _packs.Length; i++)
                if (_selected[i]) toDelete.Add(_packs[i]);

            if (toDelete.Count == 0) return;

            if (!EditorUtility.DisplayDialog("Delete Packs",
                $"Delete {toDelete.Count} pack asset(s)?\n(This removes the .asset files; Addressables groups remain.)",
                "Delete", "Cancel"))
            {
                return;
            }

            foreach (var p in toDelete)
                DeletePack(p, silent: true);

            RefreshPacks();
        }

        private void DeletePack(ContentPackDefinition p, bool silent = false)
        {
            if (p == null) return;
            string path = AssetDatabase.GetAssetPath(p);
            if (string.IsNullOrEmpty(path)) return;

            if (!silent)
            {
                if (!EditorUtility.DisplayDialog("Delete Pack",
                    $"Delete pack asset '{p.PackName}'?\n(This removes only the .asset, not the Addressables group.)",
                    "Delete", "Cancel"))
                {
                    return;
                }
            }

            AssetDatabase.DeleteAsset(path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            
            RefreshPacks();
        }
    }
}
#endif
