#if UNITY_EDITOR
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
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
            string[] guids = AssetDatabase.FindAssets("t:ContentPackDefinition");
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var pack = AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(path);
                if (pack != null) _packs.Add(pack);
            }
            _packs = _packs.OrderBy(p => p.name).ToList();
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
            }
        }

        private static GUIStyle _helpWrap;
        private void Header()
        {
            if (_helpWrap == null)
            {
                _helpWrap = new GUIStyle(EditorStyles.helpBox) { wordWrap = true, richText = true };
            }

            EditorGUILayout.LabelField("Content Packs", EditorStyles.boldLabel);
            EditorGUILayout.LabelField(
                "This window displays your ContentPackDefinition assets and lets you manage their items.\n"+
                "Tip: You can now <b>drag & drop prefabs directly into each pack</b> below.", _helpWrap);

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
                    var chosen = EditorUtility.OpenFolderPanel("Choose build folder", _buildLocation, "");
                    if (!string.IsNullOrEmpty(chosen))
                    {
                        _buildLocation = chosen;
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
                EditorGUILayout.LabelField("Packs Folder", GUILayout.Width(140));
                EditorGUI.BeginChangeCheck();
                _packsFolder = EditorGUILayout.TextField(_packsFolder);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetString(PREF_KEY_PACKS_FOLDER, _packsFolder);

                if (GUILayout.Button("Select", GUILayout.Width(80)))
                {
                    var start = string.IsNullOrEmpty(_packsFolder) ? "Assets" : _packsFolder;
                    var folder = EditorUtility.OpenFolderPanel("Choose folder for packs", start, "");
                    if (!string.IsNullOrEmpty(folder))
                    {
                        if (folder.StartsWith(Application.dataPath))
                        {
                            _packsFolder = "Assets" + folder.Substring(Application.dataPath.Length);
                            EditorPrefs.SetString(PREF_KEY_PACKS_FOLDER, _packsFolder);
                        }
                        else
                        {
                            EditorUtility.DisplayDialog("Invalid Folder",
                                "Please choose a folder <b>inside</b> the project Assets directory.", "OK");
                        }
                    }
                }
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                _newPackName = EditorGUILayout.TextField("New Pack Name", _newPackName);
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

            var assetPath = Path.Combine(_packsFolder, _newPackName + ".asset").Replace("\\", "/");
            if (File.Exists(assetPath))
            {
                EditorUtility.DisplayDialog("Already exists",
                    $"A pack named '{_newPackName}' already exists in that folder.", "OK");
                return;
            }

            var instance = CreateInstance<ContentPackDefinition>();
#if UNITY_2021_2_OR_NEWER
            AssetDatabase.CreateAsset(instance, assetPath);
#else
            AssetDatabase.CreateAsset(instance, assetPath);
#endif
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

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _foldouts[key] = EditorGUILayout.Foldout(_foldouts[key], p.name, true);
                            GUILayout.FlexibleSpace();
                            if (GUILayout.Button("Open", GUILayout.Width(70)))
                            {
                                Selection.activeObject = p;
                                EditorGUIUtility.PingObject(p);
                            }
                            if (GUILayout.Button("Remove Missing", GUILayout.Width(120)))
                            {
                                RemoveMissing(p);
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

            //if (GUILayout.Button("Add Slot"))
            //{
            //    Undo.RecordObject(p, "Add Item Slot");
            //    p._items.Add(null);
            //    EditorUtility.SetDirty(p);
            //    AssetDatabase.SaveAssets();
            //}

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
                // Accept only asset-backed GameObjects (prefabs)
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

                    // If your ContentPackDefinition automatically syncs Addressables in OnValidate, that's enough.
                    // Otherwise, you may call a public method on the asset here (if it exists):
                    // p.SyncToAddressables();

                    evt.Use();
                }
                else
                {
                    evt.Use();
                }
            }
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

        private static void RemoveMissing(ContentPackDefinition p)
        {
            if (p._items == null) return;
            Undo.RecordObject(p, "Remove Missing");
            p._items.RemoveAll(x => x == null);
            EditorUtility.SetDirty(p);
            AssetDatabase.SaveAssets();
        }
    }
}
#endif
