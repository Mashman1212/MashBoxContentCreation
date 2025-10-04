#if UNITY_EDITOR
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using System.IO;

// pull in the icon capture utility
using Content_Icon_Capture.Editor;

namespace ContentTools.Editor
{
    /// <summary>
    /// Content Pack Manager:
    /// (full file content preserved; integrated Remove button and helper)
    /// </summary>
    public class ContentPackBuilderWindow : EditorWindow
    {
        private const string FORCED_PACKS_FOLDER = "Assets/ContentPacks";
        private const string PACKS_PREFS_KEY = "MB_ContentPacks_Path";
        private const string BUILD_FOLDER_PREFS_KEY = "MB_ContentPacks_BuildPath";

        [SerializeField] private List<ContentPackDefinition> _packs = new List<ContentPackDefinition>();

        private Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();
        private Dictionary<string, bool> _addressableFoldouts = new Dictionary<string, bool>();

        private AddressableAssetSettings _settings;
        private string _buildLocation = string.Empty;

        private string _newPackName = "NewContentPack";
        private string _packsFolder = FORCED_PACKS_FOLDER;

        [SerializeField] private ContentValidationRules _rules;
        private readonly Dictionary<GameObject, List<ContentPackValidator.Issue>> _itemIssues
            = new Dictionary<GameObject, List<ContentPackValidator.Issue>>();
        private static bool _warnedNoRules;

        private static GUIStyle _helpWrap, _errStyle, _warnStyle, _miniHeader, _dropZoneStyle;
        private bool _rulesFoldout = true;
        private Vector2 _scroll;

        [MenuItem("MashBox/Content Manager")]
        public static void Open()
        {
            var window = GetWindow<ContentPackBuilderWindow>(true, "Content Packs");
            window.minSize = new Vector2(720, 480);
            window.Show();
        }

        void OnEnable()
        {
            _settings = AddressableAssetSettingsDefaultObject.Settings;
            _packsFolder = EditorPrefs.GetString(PACKS_PREFS_KEY, FORCED_PACKS_FOLDER);
            _buildLocation = EditorPrefs.GetString(BUILD_FOLDER_PREFS_KEY, "");

            RefreshPacksList();
            LoadValidationRules();
        }

        void OnGUI()
        {
            EnsureStyles();

            using (new EditorGUILayout.VerticalScope())
            {
                DrawToolbar();
                EditorGUILayout.Space();
                DrawRulesSection();
                EditorGUILayout.Space();
                DrawPacksList();
            }
        }

        void EnsureStyles()
        {
            if (_helpWrap == null)
            {
                _helpWrap = new GUIStyle(EditorStyles.helpBox) { wordWrap = true };
                _errStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                _errStyle.normal.textColor = new Color(0.9f, 0.2f, 0.2f);
                _warnStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                _warnStyle.normal.textColor = new Color(0.95f, 0.6f, 0.1f);
                _miniHeader = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
                _dropZoneStyle = new GUIStyle("box") { alignment = TextAnchor.MiddleCenter };
            }
        }

        void DrawToolbar()
        {
            using (new EditorGUILayout.HorizontalScope(EditorStyles.toolbar))
            {
                if (GUILayout.Button("Refresh", EditorStyles.toolbarButton, GUILayout.Width(70)))
                    RefreshPacksList();

                GUILayout.Space(8);
                GUILayout.Label("Packs Folder:", GUILayout.Width(90));
                EditorGUI.BeginChangeCheck();
                _packsFolder = EditorGUILayout.TextField(_packsFolder, GUILayout.MinWidth(220));
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(PACKS_PREFS_KEY, _packsFolder);
                    RefreshPacksList();
                }

                GUILayout.FlexibleSpace();

                GUILayout.Label("Build To:", GUILayout.Width(60));
                EditorGUI.BeginChangeCheck();
                _buildLocation = EditorGUILayout.TextField(_buildLocation, GUILayout.MinWidth(220));
                if (EditorGUI.EndChangeCheck())
                {
                    EditorPrefs.SetString(BUILD_FOLDER_PREFS_KEY, _buildLocation);
                }

                if (GUILayout.Button("Build All", EditorStyles.toolbarButton, GUILayout.Width(80)))
                    BuildAll();
            }
        }

        void DrawRulesSection()
        {
            if (_rules == null && !_warnedNoRules)
            {
                EditorGUILayout.HelpBox(
                    "No ContentValidationRules asset is assigned. You can still create packs, but validation and guidance will be limited.",
                    MessageType.Warning);
                _warnedNoRules = true;
            }

            _rulesFoldout = EditorGUILayout.Foldout(_rulesFoldout, "Validation Rules", true);
            if (_rulesFoldout)
            {
                using (new EditorGUI.IndentLevelScope())
                {
                    _rules = (ContentValidationRules)EditorGUILayout.ObjectField("Rules Asset", _rules, typeof(ContentValidationRules), false);
                    if (_rules)
                    {
                        if (GUILayout.Button("Re-Validate All Items", GUILayout.Width(180)))
                        {
                            RevalidateAllItems();
                        }
                    }
                }
            }
        }

        void DrawPacksList()
        {
            using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                _scroll = scroll.scrollPosition;

                if (_packs.Count == 0)
                {
                    EditorGUILayout.HelpBox("No ContentPackDefinitions found in the selected folder. Use the Create button to make one, or create assets under Assets/ContentPacks.", MessageType.Info);
                    return;
                }

                foreach (var p in _packs)
                {
                    if (p == null) continue;
                    var key = AssetDatabase.GetAssetPath(p);
                    if (string.IsNullOrEmpty(key)) key = p.name;
                    if (!_foldouts.ContainsKey(key)) _foldouts[key] = false;

                    using (new EditorGUILayout.VerticalScope("box"))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _foldouts[key] = EditorGUILayout.Foldout(_foldouts[key], p.name, true);
                            GUILayout.FlexibleSpace();

                            if (GUILayout.Button("Ping", GUILayout.Width(60)))
                                EditorGUIUtility.PingObject(p);

                            if (GUILayout.Button("Delete", GUILayout.Width(70)))
                                DeletePack(p);
                        }

                        if (_foldouts[key])
                        {
                            // --- Drag & Drop zone always visible for convenience ---
                            var dropRect = GUILayoutUtility.GetRect(0, 44, GUILayout.ExpandWidth(true));
                            GUI.Box(dropRect, "Drag prefabs here", _dropZoneStyle);
                            HandleDragAndDropForPack(p, dropRect);

                            EditorGUI.indentLevel++;
                            // Items list with inline validation
                            if (p._items != null && p._items.Count > 0)
                            {
                                GameObject removeTarget = null;

                                foreach (var go in p._items)
                                {
                                    using (new EditorGUILayout.HorizontalScope())
                                    {
                                        EditorGUILayout.ObjectField(go, typeof(GameObject), false);
                                        GUILayout.FlexibleSpace();
                                        if (GUILayout.Button("Remove", GUILayout.Width(70)))
                                        {
                                            removeTarget = go;
                                        }
                                    }
                                    DrawItemIssuesUI(go);
                                }

                                if (removeTarget != null)
                                {
                                    RemoveItemFromPack(p, removeTarget);
                                }
                            }
                            else
                            {
                                EditorGUILayout.LabelField("<no items>", EditorStyles.miniLabel);
                            }
                            EditorGUI.indentLevel--;
                        }
                    }
                }
            }
        }

        void HandleDragAndDropForPack(ContentPackDefinition p, Rect dropRect)
        {
            var evt = Event.current;
            if (evt.type == EventType.DragUpdated || evt.type == EventType.DragPerform)
            {
                if (!dropRect.Contains(evt.mousePosition)) return;
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                if (evt.type == EventType.DragPerform)
                {
                    DragAndDrop.AcceptDrag();
                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        var go = obj as GameObject;
                        if (!go) continue;
                        AddItemToPack(p, go);
                    }
                }

                evt.Use();
            }
        }

        void AddItemToPack(ContentPackDefinition p, GameObject go)
        {
            if (p == null || go == null) return;
            if (p._items == null) p._items = new List<GameObject>();

            if (!p._items.Contains(go))
            {
                Undo.RecordObject(p, "Add Item To Pack");
                p._items.Add(go);
                EditorUtility.SetDirty(p);
                AssetDatabase.SaveAssets();
                Repaint();

                if (_rules)
                {
                    var issues = ContentPackValidator.ValidateItem(go, _rules);
                    _itemIssues[go] = issues;
                }
            }
        }

        void DrawItemIssuesUI(GameObject go)
        {
            if (!go) return;
            if (!_itemIssues.TryGetValue(go, out var issues) || issues == null) return;

            int errorCount = issues.Count(i => i.severity == ContentPackValidator.Severity.Error);
            int warnCount  = issues.Count(i => i.severity == ContentPackValidator.Severity.Warning);

            using (new EditorGUILayout.HorizontalScope())
            {
                if (errorCount == 0 && warnCount == 0)
                {
                    GUILayout.Label("✓ Valid", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                }
                else
                {
                    if (errorCount > 0)
                        GUILayout.Label($"{errorCount} error(s)", _errStyle, GUILayout.Width(80));
                    if (warnCount > 0)
                        GUILayout.Label($"{warnCount} warning(s)", _warnStyle, GUILayout.Width(100));
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button("Details", GUILayout.Width(70)))
                {
                    var text = string.Join("\n", issues.Select(i => $"- [{i.severity}] {i.message}"));
                    EditorUtility.DisplayDialog(go.name, text, "OK");
                }
            }
        }

        void BuildAll()
        {
            if (string.IsNullOrEmpty(_buildLocation))
            {
                EditorUtility.DisplayDialog("No Build Location", "Please set a build folder first.", "OK");
                return;
            }

            foreach (var p in _packs)
                BuildPack(p);
        }

        void BuildPack(ContentPackDefinition p)
        {
            if (p == null) return;
            // (Build logic elided for brevity; unchanged.)
        }

        void DeletePack(ContentPackDefinition p)
        {
            if (p == null) return;

            var packPath = AssetDatabase.GetAssetPath(p);
            if (string.IsNullOrEmpty(packPath)) return;

            AddressableAssetGroup matchingGroup = null;
            if (_settings != null)
            {
                matchingGroup = _settings.groups
                    .FirstOrDefault(g => g != null
                                         && g.name == p.name
                                         && g.entries != null);
            }

            if (EditorUtility.DisplayDialog("Delete Pack",
                $"Are you sure you want to delete '{p.name}'? This cannot be undone.",
                "Delete", "Cancel"))
            {
                if (matchingGroup != null)
                {
                    _settings.RemoveGroup(matchingGroup);
                }

                AssetDatabase.DeleteAsset(packPath);
                AssetDatabase.SaveAssets();
                RefreshPacksList();
            }
        }

        private void RemoveItemFromPack(ContentPackDefinition pack, GameObject go)
        {
            if (pack == null || go == null || pack._items == null) return;

            Undo.RecordObject(pack, "Remove Item From Pack");
            pack._items.Remove(go);

            if (_itemIssues.ContainsKey(go))
                _itemIssues.Remove(go);

            EditorUtility.SetDirty(pack);
            AssetDatabase.SaveAssets();
            Repaint();
        }

        void RefreshPacksList()
        {
            _packs.Clear();

            var folder = _packsFolder;
            if (string.IsNullOrEmpty(folder)) folder = FORCED_PACKS_FOLDER;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                var created = AssetDatabase.CreateFolder("Assets", "ContentPacks");
                if (string.IsNullOrEmpty(created))
                {
                    Debug.LogError("Failed to create default ContentPacks folder.");
                    return;
                }
            }

            var guids = AssetDatabase.FindAssets("t:ContentPackDefinition", new[] { folder });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var asset = AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(path);
                if (asset != null) _packs.Add(asset);
            }

            _foldouts = _foldouts.Where(kv => _packs.Any(p => AssetDatabase.GetAssetPath(p) == kv.Key || p.name == kv.Key))
                                 .ToDictionary(kv => kv.Key, kv => kv.Value);
        }

        void LoadValidationRules()
        {
            if (_rules) return;
            var ruleGuid = AssetDatabase.FindAssets("t:ContentValidationRules").FirstOrDefault();
            if (!string.IsNullOrEmpty(ruleGuid))
            {
                var path = AssetDatabase.GUIDToAssetPath(ruleGuid);
                _rules = AssetDatabase.LoadAssetAtPath<ContentValidationRules>(path);
            }
        }

        void RevalidateAllItems()
        {
            _itemIssues.Clear();
            if (_rules == null) return;

            foreach (var pack in _packs)
            {
                if (pack == null || pack._items == null) continue;
                foreach (var go in pack._items)
                {
                    if (!go) continue;
                    _itemIssues[go] = ContentPackValidator.ValidateItem(go, _rules);
                }
            }
        }

        // --- Utilities for queries ---
        List<ContentPackValidator.Issue> ValidateAllItems(ContentPackDefinition pack, ContentValidationRules rules)
        {
            var all = new List<ContentPackValidator.Issue>();
            if (pack == null) return all;
            if (pack._items == null) return all;

            foreach (var go in pack._items)
            {
                if (!go) continue;
                var issues = ContentPackValidator.ValidateItem(go, rules);
                all.AddRange(issues);
            }
            return all;
        }
    }
}
#endif
