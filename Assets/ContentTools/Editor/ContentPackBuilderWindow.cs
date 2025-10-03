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

        // cached packs + UI state
        private List<ContentPackDefinition> _packs = new List<ContentPackDefinition>();
        private Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();
        private Dictionary<string, bool> _selected = new Dictionary<string, bool>();

        // Addressables settings (for active profile id)
        private AddressableAssetSettings _settings;

        // Single, simple destination for bundles/manifests
        private string _buildLocation = string.Empty; // filesystem folder

        // Creation helpers
        private string _newPackName = "NewContentPack";
        private string _packsFolder; // where to create packs

        [MenuItem("Window/MashBox/Content Manager")]
        public static void Open()
        {
            GetWindow<ContentPackBuilderWindow>(true, "Content Manager");
        }

        private void OnEnable()
        {
            _settings = AddressableAssetSettingsDefaultObject.Settings;
            _buildLocation = EditorPrefs.GetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
            _packsFolder   = EditorPrefs.GetString(PREF_KEY_PACKS_FOLDER,   _packsFolder);
            RefreshPacks();
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation ?? string.Empty);
            EditorPrefs.SetString(PREF_KEY_PACKS_FOLDER,   _packsFolder   ?? string.Empty);
        }

        private void RefreshPacks()
        {
            _packs.Clear();
            _foldouts.Clear();
            var prevSelected = new Dictionary<string, bool>(_selected);
            _selected.Clear();

            string[] guids = AssetDatabase.FindAssets("t:ContentPackDefinition");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var pack = AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(path);
                if (pack != null) _packs.Add(pack);
            }
            _packs = _packs.OrderBy(p => p.name).ToList();

            foreach (var p in _packs)
            {
                var key = AssetDatabase.GetAssetPath(p);
                _foldouts[key] = true;
                _selected[key] = prevSelected.TryGetValue(key, out var was) ? was : true;
            }
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.VerticalScope())
            {
                Header();
                GUILayout.Space(6);
                DrawCreateSection();
                GUILayout.Space(6);
                DrawPacksList();
                GUILayout.Space(8);
                DrawBuildRow();
            }
        }

        private static GUIStyle _helpWrap;
        private void Header()
        {
            if (_helpWrap == null)
            {
                _helpWrap = new GUIStyle(EditorStyles.helpBox) { wordWrap = true, richText = true };
            }

            EditorGUILayout.LabelField("Content Manager", EditorStyles.boldLabel);
            //EditorGUILayout.LabelField(
            //    "This window displays your ContentPackDefinition assets and lets you manage their items.\n" +
            //    "Tip: You can <b>drag & drop prefabs directly into each pack</b> below.", _helpWrap);

            GUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Build Output Folder", GUILayout.Width(140));
                EditorGUI.BeginChangeCheck();
                _buildLocation = EditorGUILayout.TextField(_buildLocation);
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
                }
                if (GUILayout.Button("Browse", GUILayout.Width(80)))
                {
                    var chosen = EditorUtility.OpenFolderPanel("Choose build folder", string.IsNullOrEmpty(_buildLocation) ? Application.dataPath : _buildLocation, "");
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        _buildLocation = chosen.Replace("\\", "/");
                        EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
                    }
                }
            }
        }

        private void DrawCreateSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Create / Locate Packs", EditorStyles.boldLabel);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Pack Definitions Data Folder", GUILayout.Width(180));
                EditorGUI.BeginChangeCheck();
                _packsFolder = EditorGUILayout.TextField(_packsFolder, GUILayout.MinWidth(250), GUILayout.ExpandWidth(true));
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetString(PREF_KEY_PACKS_FOLDER, _packsFolder);

                if (GUILayout.Button("Browse", GUILayout.Width(80)))
                {
                    var start = string.IsNullOrEmpty(_packsFolder)
                        ? Application.dataPath
                        : Path.Combine(Application.dataPath, _packsFolder.TrimStart("Assets/".ToCharArray()));
                    var folder = EditorUtility.OpenFolderPanel("Choose folder for packs", start, "");
                    if (!string.IsNullOrEmpty(folder))
                    {
                        if (folder.StartsWith(Application.dataPath))
                        {
                            _packsFolder = "Assets" + folder.Substring(Application.dataPath.Length);
                            _packsFolder = _packsFolder.Replace("\\", "/");
                            EditorPrefs.SetString(PREF_KEY_PACKS_FOLDER, _packsFolder);
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Invalid Folder",
                                "Please choose a folder inside the project Assets directory.", "OK");
                        }
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("New Pack Name", GUILayout.Width(110));
                _newPackName = EditorGUILayout.TextField(_newPackName, GUILayout.MinWidth(250), GUILayout.ExpandWidth(true));
                GUI.enabled = !string.IsNullOrEmpty(_packsFolder) && !string.IsNullOrWhiteSpace(_newPackName);
                if (GUILayout.Button("Create Pack", GUILayout.Width(110)))
                {
                    CreatePack();
                }
                GUI.enabled = true;
            }
        }

        private void CreatePack()
        {
            if (string.IsNullOrEmpty(_packsFolder))
            {
                EditorUtility.DisplayDialog("Pick a folder", "Please choose a folder for your packs first.", "OK");
                return;
            }

            if (!AssetDatabase.IsValidFolder(_packsFolder))
            {
                EditorUtility.DisplayDialog("Folder missing",
                    $"'{_packsFolder}' isn't a valid folder inside Assets.", "OK");
                return;
            }

            var assetPath = AssetDatabase.GenerateUniqueAssetPath(Path.Combine(_packsFolder, _newPackName + ".asset").Replace("\\", "/"));
            var instance = CreateInstance<ContentPackDefinition>();
            AssetDatabase.CreateAsset(instance, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            EditorGUIUtility.PingObject(instance);
            Selection.activeObject = instance;

            RefreshPacks();
        }

        private Vector2 _scroll;
        private void DrawPacksList()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                if (_packs.Count == 0)
                {
                    EditorGUILayout.HelpBox("No ContentPackDefinition assets found. Create one above.", MessageType.Info);
                    return;
                }

                foreach (var p in _packs)
                {
                    if (p == null) continue;
                    var key = AssetDatabase.GetAssetPath(p);
                    if (!_foldouts.ContainsKey(key)) _foldouts[key] = true;
                    if (!_selected.ContainsKey(key)) _selected[key] = true;

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _selected[key] = EditorGUILayout.Toggle(_selected[key], GUILayout.Width(18));
                            _foldouts[key] = EditorGUILayout.Foldout(_foldouts[key], p.name, true);
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("Open", GUILayout.Width(70)))
                            {
                                Selection.activeObject = p;
                                EditorGUIUtility.PingObject(p);
                            }
                            if (GUILayout.Button("Delete", GUILayout.Width(70)))
                            {
                                DeletePack(p);
                            }
                        }

                        if (_foldouts[key])
                        {
                            DrawPackItemsList(p);
                            DrawPackDropZone(p);
                        }
                    }
                }
            }
        }

        private void DrawPackItemsList(ContentPackDefinition p)
        {
            EditorGUI.indentLevel++;
            if (p._items == null) p._items = new List<GameObject>();

            for (int i = 0; i < p._items.Count; i++)
            {
                var go = p._items[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    p._items[i] = (GameObject)EditorGUILayout.ObjectField(go, typeof(GameObject), false);
                    if (GUILayout.Button("-", GUILayout.Width(22)))
                    {
                        Undo.RecordObject(p, "Remove Item from Pack");
                        p._items.RemoveAt(i);
                        EditorUtility.SetDirty(p);
                        AssetDatabase.SaveAssets();
                        GUIUtility.ExitGUI();
                    }
                }
            }
            EditorGUI.indentLevel--;
        }

        private void DrawPackDropZone(ContentPackDefinition p)
        {
            GUILayout.Space(6);
            var rect = GUILayoutUtility.GetRect(0, 48, GUILayout.ExpandWidth(true));
            GUI.Box(rect, "Drag prefabs here to add to this pack", EditorStyles.helpBox);

            var evt = Event.current;
            if (!rect.Contains(evt.mousePosition))
                return;

            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                bool hasValid = DragAndDrop.objectReferences.Any(o =>
                {
                    var go = o as GameObject;
                    if (!go) return false;
                    var path = AssetDatabase.GetAssetPath(go);
                    return !string.IsNullOrEmpty(path);
                });

                DragAndDrop.visualMode = hasValid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;

                if (evt.type == EventType.DragPerform && hasValid)
                {
                    DragAndDrop.AcceptDrag();

                    Undo.RecordObject(p, "Add prefabs to Content Pack");

                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        var go = obj as GameObject;
                        if (!go) continue;
                        var path = AssetDatabase.GetAssetPath(go);
                        if (string.IsNullOrEmpty(path)) continue; // not an asset
                        if (!p._items.Contains(go))
                            p._items.Add(go);
                    }

                    EditorUtility.SetDirty(p);
                    AssetDatabase.SaveAssets();
                    evt.Use();
                }
                else
                {
                    evt.Use();
                }
            }
        }

        // ===== Build buttons row (bottom) =====
        private void DrawBuildRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                GUILayout.FlexibleSpace();

                bool anySelected = _selected.Values.Any(v => v);
                GUI.enabled = anySelected;
                if (GUILayout.Button("Build Selected", GUILayout.Width(140)))
                {
                    var list = _packs.Where(p =>
                    {
                        var key = AssetDatabase.GetAssetPath(p);
                        return _selected.TryGetValue(key, out var on) && on;
                    }).ToList();
                    BuildPacks(list, cleanMissing:true);
                }
                GUI.enabled = true;

                if (GUILayout.Button("Build All", GUILayout.Width(110)))
                {
                    BuildPacks(_packs.ToList(), cleanMissing:true);
                }

                if (GUILayout.Button("Refresh", GUILayout.Width(90)))
                {
                    RefreshPacks();
                }
            }
        }

        private void BuildPacks(List<ContentPackDefinition> list, bool cleanMissing)
        {
            if (_settings == null)
            {
                Debug.LogError("Addressables settings not found.");
                return;
            }
            if (list == null || list.Count == 0)
            {
                Debug.LogWarning("No packs selected to build.");
                return;
            }

            // Clean missing items before build (quietly)
            if (cleanMissing)
            {
                foreach (var p in list)
                {
                    if (p == null) continue;
                    if (p._items == null) continue;

                    int before = p._items.Count;
                    if (before == 0) continue;

                    Undo.RecordObject(p, "Clean Missing Items");
                    p._items.RemoveAll(x => x == null);
                    if (p._items.Count != before)
                    {
                        EditorUtility.SetDirty(p);
                    }
                }
                AssetDatabase.SaveAssets();
            }

            var opts = new AddressablesPackBuilder.BuildOptions
            {
                // Hidden from UI: use active profile
                profileId = _settings.activeProfileId,
                rebuildPlayerContent = true,
                enableRemoteCatalog = true,
                disableOtherGroups = true,
                writeManifestJson = true,
                manifestFileName = null,
                setPlayerVersionOverride = true,

                sessionRemoteBuildRootOverride = string.IsNullOrEmpty(_buildLocation) ? null : _buildLocation.Replace("\\", "/"),
                sessionRemoteLoadRootOverride = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}",
            };

            int built = 0;
            foreach (var p in list)
            {
                if (p == null) continue;
                AddressablesPackBuilder.BuildPack(p, opts);
                built++;
            }
            Debug.Log(built > 0 ? $"Built {built} content pack(s)." : "Nothing to build.");
        }

        private void DeletePack(ContentPackDefinition p)
        {
            if (p == null) return;
            var path = AssetDatabase.GetAssetPath(p);
            if (string.IsNullOrEmpty(path)) return;

            bool confirm = EditorUtility.DisplayDialog(
                "Delete Content Pack?",
                $"Delete '{p.name}' from the project?\nThis cannot be undone.",
                "Delete", "Cancel");
            if (!confirm) return;

            if (Selection.activeObject == p) Selection.activeObject = null;

            AssetDatabase.DeleteAsset(path);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            RefreshPacks();
        }
    }
}
#endif
