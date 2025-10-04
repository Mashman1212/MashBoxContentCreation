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
    /// <summary>
    /// Content Pack Manager:
    ///  • Forced pack folder: Assets/ContentPacks
    ///  • Shows Validation Rules panel (read-only)
    ///  • Inline validation with colored severities
    ///  • Blocks build when any selected pack has errors
    ///  • Prevent duplicate pack names
    /// </summary>
    public class ContentPackBuilderWindow : EditorWindow
    {
        // ---- Forced folder for ContentPackDefinition assets ----
        private const string FORCED_PACKS_FOLDER = "Assets/ContentPacks";

        // ---- EditorPrefs keys for persistence ----
        private const string PREF_KEY_BUILD_LOCATION = "ContentPackBuilder.BuildLocation";

        // cached packs + UI state
        private List<ContentPackDefinition> _packs = new List<ContentPackDefinition>();
        private Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();
        private Dictionary<string, bool> _selected = new Dictionary<string, bool>();

        // Addressables settings
        private AddressableAssetSettings _settings;

        // build output
        private string _buildLocation = string.Empty;

        // Creation helpers
        private string _newPackName = "NewContentPack";
        private string _packsFolder = FORCED_PACKS_FOLDER;

        // -------- Validation integration (hidden) --------
        [SerializeField] private ContentValidationRules _rules;  // auto-loaded, not shown in pickers
        private readonly Dictionary<GameObject, List<ContentPackValidator.Issue>> _itemIssues
            = new Dictionary<GameObject, List<ContentPackValidator.Issue>>();
        private static bool _warnedNoRules;

        // ---- UI styles & flags ----
        private static GUIStyle _helpWrap, _errStyle, _warnStyle, _miniHeader;
        private bool _rulesFoldout = true;
        private Vector2 _scroll;

        [MenuItem("MashBox/Content Manager")]
        public static void Open()
        {
            GetWindow<ContentPackBuilderWindow>(true, "MG Content Manager");
        }

        private void OnEnable()
        {
            _settings = AddressableAssetSettingsDefaultObject.Settings;
            _buildLocation = EditorPrefs.GetString(PREF_KEY_BUILD_LOCATION, _buildLocation);

            _packsFolder = FORCED_PACKS_FOLDER;
            EnsureFolderExists(_packsFolder);

            AutoLoadRulesIfNeeded();
            RefreshPacks();
            EditorApplication.projectChanged += OnProjectChanged;
            
            RevalidateAllItems();
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation ?? string.Empty);
            EditorApplication.projectChanged -= OnProjectChanged; 
        }
        
        private void OnProjectChanged()
        {
            RevalidateAllItems();
            Repaint();
        }

        private void RevalidateAllItems()
        {
            if (_packs == null) return;
            foreach (var p in _packs)
            {
                if (p == null || p._items == null) continue;
                foreach (var go in p._items)
                {
                    if (!go) continue;
                    _itemIssues[go] = ContentPackValidator.ValidateItem(go, _rules);
                }
            }
        }

        private void AutoLoadRulesIfNeeded()
        {
            if (_rules != null) return;

            var guids = AssetDatabase.FindAssets("t:ContentValidationRules");
            if (guids != null && guids.Length > 0)
            {
                var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                _rules = AssetDatabase.LoadAssetAtPath<ContentValidationRules>(path);
                if (_rules)
                    Debug.Log($"[ContentPackBuilder] Using validation rules: {path}", _rules);
            }
            else if (!_warnedNoRules)
            {
                _warnedNoRules = true;
                Debug.LogWarning(
                    "[ContentPackBuilder] No ContentValidationRules asset found. " +
                    "Validation will be limited (Brand ignored; SuperType/Type pairs & Colors/Anchors require rules).");
            }
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
                p.SyncToAddressables();
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
                DrawRulesOverview();     // <-- rules panel
                GUILayout.Space(6);
                DrawPacksList();
                GUILayout.Space(8);
                DrawBuildRow();
            }
        }

        private void Header()
        {
            // styles
            if (_helpWrap == null) _helpWrap = new GUIStyle(EditorStyles.helpBox) { wordWrap = true, richText = true };
            if (_errStyle == null)
            {
                _errStyle = new GUIStyle(EditorStyles.boldLabel);
                _errStyle.normal.textColor = Color.red;
            }
            if (_warnStyle == null)
            {
                _warnStyle = new GUIStyle(EditorStyles.label);
                _warnStyle.normal.textColor = new Color(1f, 0.5f, 0f); // orange
            }
            if (_miniHeader == null)
            {
                _miniHeader = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleLeft };
            }

            EditorGUILayout.LabelField("Content Manager", EditorStyles.boldLabel);

            GUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Build Output Folder", GUILayout.Width(140));
                EditorGUI.BeginChangeCheck();
                _buildLocation = EditorGUILayout.TextField(_buildLocation);
                if (EditorGUI.EndChangeCheck())
                    EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);

                if (GUILayout.Button("Browse", GUILayout.Width(80)))
                {
                    var chosen = EditorUtility.OpenFolderPanel("Choose build folder",
                        string.IsNullOrEmpty(_buildLocation) ? Application.dataPath : _buildLocation, "");
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
            EditorGUILayout.LabelField("Create Packs", EditorStyles.boldLabel);

            // read-only forced folder
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Pack Definitions Folder", GUILayout.Width(180));
                EditorGUILayout.LabelField(FORCED_PACKS_FOLDER, EditorStyles.miniLabel);
                if (!AssetDatabase.IsValidFolder(FORCED_PACKS_FOLDER))
                {
                    if (GUILayout.Button("Create Folder", GUILayout.Width(110)))
                    {
                        EnsureFolderExists(FORCED_PACKS_FOLDER);
                        AssetDatabase.Refresh();
                    }
                }
            }

            // name + duplicate checks
            string sanitized = SanitizePackName(_newPackName);
            if (sanitized != _newPackName) _newPackName = sanitized;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("New Pack Name", GUILayout.Width(110));
                _newPackName = EditorGUILayout.TextField(_newPackName, GUILayout.MinWidth(250), GUILayout.ExpandWidth(true));

                bool dupeAsset = !string.IsNullOrWhiteSpace(_newPackName) && PackNameExists(_newPackName, out _);
                bool dupeGroup = !string.IsNullOrWhiteSpace(_newPackName) && AddressablesGroupExists(_newPackName);

                GUI.enabled = !string.IsNullOrWhiteSpace(_newPackName) && !dupeAsset && !dupeGroup;
                if (GUILayout.Button("Create Pack", GUILayout.Width(110)))
                {
                    CreatePack();
                }
                GUI.enabled = true;
            }

            if (string.IsNullOrWhiteSpace(_newPackName))
            {
                EditorGUILayout.HelpBox("Enter a pack name.", MessageType.Info);
            }
            else
            {
                if (PackNameExists(_newPackName, out var existingPath))
                    EditorGUILayout.HelpBox($"A ContentPackDefinition named '{_newPackName}' already exists:\n{existingPath}", MessageType.Error);
                if (AddressablesGroupExists(_newPackName))
                    EditorGUILayout.HelpBox($"An Addressables Group named '{_newPackName}' already exists.", MessageType.Error);
            }
        }

        private void DrawRulesOverview()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _rulesFoldout = EditorGUILayout.Foldout(_rulesFoldout, "Validation Rules (read-only)", true);
                if (!_rulesFoldout) return;

                if (_rules == null)
                {
                    EditorGUILayout.HelpBox(
                        "No ContentValidationRules asset found. Create one via Tools → MashBox → Create Prefilled Validation Rules, or Assets → Create → Content → Validation Rules.",
                        MessageType.Warning);
                    return;
                }

                EditorGUILayout.LabelField("SuperTypes", _miniHeader);
                if (_rules.SuperTypes != null && _rules.SuperTypes.Length > 0)
                    EditorGUILayout.LabelField(string.Join(", ", _rules.SuperTypes));
                else
                    EditorGUILayout.LabelField("<none>");

                EditorGUILayout.Space(3);

                EditorGUILayout.LabelField("Allowed Pairs (SuperType → Types)", _miniHeader);
                if (_rules.AllowedPairs != null && _rules.AllowedPairs.Count > 0)
                {
                    foreach (var pair in _rules.AllowedPairs)
                    {
                        if (pair == null) continue;
                        var types = (pair.Types != null && pair.Types.Length > 0) ? string.Join(", ", pair.Types) : "<none>";
                        EditorGUILayout.LabelField($"• {pair.SuperType} → {types}");
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("<none>");
                }

                EditorGUILayout.Space(3);

                EditorGUILayout.LabelField("Colors", _miniHeader);
                if (_rules.Colors != null && _rules.Colors.Length > 0)
                    EditorGUILayout.LabelField(string.Join(", ", _rules.Colors));
                else
                    EditorGUILayout.LabelField("<none>");

                EditorGUILayout.Space(3);

                EditorGUILayout.LabelField("Anchor Rules", _miniHeader);
                if (_rules.AnchorRules != null && _rules.AnchorRules.Count > 0)
                {
                    foreach (var r in _rules.AnchorRules)
                    {
                        if (r == null) continue;
                        string scope =
                            $"{(string.IsNullOrEmpty(r.AppliesToSuperType) ? "*" : r.AppliesToSuperType)}/" +
                            $"{(string.IsNullOrEmpty(r.AppliesToType) ? "*" : r.AppliesToType)}/" +
                            $"{(string.IsNullOrEmpty(r.AppliesToBrand) ? "*" : r.AppliesToBrand)}";
                        var req = (r.RequiredChildren != null && r.RequiredChildren.Length > 0)
                            ? string.Join(", ", r.RequiredChildren)
                            : "<none>";
                        EditorGUILayout.LabelField($"• [{scope}] → {req}");
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("<none>");
                }
            }
        }

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
                            if (GUILayout.Button("Ping", GUILayout.Width(70)))
                                EditorGUIUtility.PingObject(p);
                            if (GUILayout.Button("Delete", GUILayout.Width(70)))
                            {
                                DeletePack(p);
                                break;
                            }
                        }

                        if (_foldouts.ContainsKey(key) && _foldouts[key])
                        {
                            DrawPackItemsList(p);

                            // item drag-and-drop (prefabs only). Remove this call if you don't want item DnD.
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
                var before = p._items[i];
                using (new EditorGUILayout.HorizontalScope())
                {
                    p._items[i] = (GameObject)EditorGUILayout.ObjectField(before, typeof(GameObject), false);

                    if (p._items[i] != before && p._items[i] != null)
                        ValidateItemLive(p._items[i]);

                    if (GUILayout.Button("-", GUILayout.Width(22)))
                    {
                        Undo.RecordObject(p, "Remove Item from Pack");
                        _itemIssues.Remove(p._items[i]);
                        p._items.RemoveAt(i);
                        EditorUtility.SetDirty(p);
                        AssetDatabase.SaveAssets();
                        GUIUtility.ExitGUI();
                    }
                }

                DrawItemIssuesUI(p._items.ElementAtOrDefault(i)); // colored errors/warnings
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
                        {
                            p._items.Add(go);
                            ValidateItemLive(go);
                        }
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

                    // Block build if any error exists
                    foreach (var pack in list)
                    {
                        var issues = ContentPackValidator.ValidatePack(pack, _rules);
                        if (issues.Any(i => i.severity == ContentPackValidator.Severity.Error))
                        {
                            ContentPackValidator.LogReport(pack, issues, "Build blocked");
                            EditorUtility.DisplayDialog("Build blocked",
                                $"'{pack.name}' has validation errors. See Console.", "OK");
                            return;
                        }
                    }

                    BuildPacks(list, cleanMissing: true);
                }
                GUI.enabled = true;

                if (GUILayout.Button("Build All", GUILayout.Width(110)))
                {
                    foreach (var pack in _packs)
                    {
                        var issues = ContentPackValidator.ValidatePack(pack, _rules);
                        if (issues.Any(i => i.severity == ContentPackValidator.Severity.Error))
                        {
                            ContentPackValidator.LogReport(pack, issues, "Build blocked");
                            EditorUtility.DisplayDialog("Build blocked",
                                $"'{pack.name}' has validation errors. See Console.", "OK");
                            return;
                        }
                    }

                    BuildPacks(_packs.ToList(), cleanMissing: true);
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

            RefreshPacks();

            // Clean missing items quietly
            if (cleanMissing)
            {
                foreach (var p in list)
                {
                    if (p == null || p._items == null) continue;
                    int before = p._items.Count;
                    if (before == 0) continue;

                    Undo.RecordObject(p, "Clean Missing Items");
                    p._items.RemoveAll(x => x == null);
                    if (p._items.Count != before)
                        EditorUtility.SetDirty(p);
                }
                AssetDatabase.SaveAssets();
            }

            var opts = new AddressablesPackBuilder.BuildOptions
            {
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

            var packPath = AssetDatabase.GetAssetPath(p);
            if (string.IsNullOrEmpty(packPath)) return;

            AddressableAssetGroup matchingGroup = null;
            if (_settings != null)
            {
                matchingGroup = _settings.groups
                    .FirstOrDefault(g => g != null
                                         && g.name == p.name
                                         && !g.ReadOnly
                                         && g != _settings.DefaultGroup);
            }

            string msg = matchingGroup != null
                ? $"Delete content pack '{p.name}' and its Addressables group with the same name?\nThis cannot be undone."
                : $"Delete content pack '{p.name}' from the project?\n(This pack has no matching writable Addressables group.)\nThis cannot be undone.";

            bool confirm = EditorUtility.DisplayDialog("Delete Content Pack?", msg, "Delete", "Cancel");
            if (!confirm) return;

            if (Selection.activeObject == p) Selection.activeObject = null;

            AssetDatabase.DeleteAsset(packPath);

            if (matchingGroup != null)
                _settings.RemoveGroup(matchingGroup);

            CleanAddressablesGroups(_settings);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            RefreshPacks();
        }

        // ---------- Validation helpers (live inline UI) ----------
        private void ValidateItemLive(GameObject go)
        {
            if (!go) return;
            var issues = ContentPackValidator.ValidateItem(go, _rules);
            _itemIssues[go] = issues;
            Repaint();
        }

        private void DrawItemIssuesUI(GameObject go)
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
                        GUILayout.Label($"Errors: {errorCount}", _errStyle, GUILayout.Width(120));   // RED
                    if (warnCount > 0)
                        GUILayout.Label($"Warnings: {warnCount}", _warnStyle, GUILayout.Width(140)); // ORANGE
                }
                GUILayout.FlexibleSpace();
            }

            if (errorCount > 0 || warnCount > 0)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    foreach (var i in issues)
                    {
                        var style = i.severity == ContentPackValidator.Severity.Error ? _errStyle : _warnStyle;
                        EditorGUILayout.LabelField("• " + i.message, style);
                    }
                }
            }
        }

        // ---------- Utility ----------

        private static void EnsureFolderExists(string assetPath)
        {
            if (AssetDatabase.IsValidFolder(assetPath)) return;

            var parts = assetPath.Trim('/').Split('/');
            string current = parts[0]; // "Assets"
            for (int i = 1; i < parts.Length; i++)
            {
                string next = $"{current}/{parts[i]}";
                if (!AssetDatabase.IsValidFolder(next))
                    AssetDatabase.CreateFolder(current, parts[i]);
                current = next;
            }
        }

        private static string SanitizePackName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;
            var trimmed = raw.Trim();
            var chars = trimmed.Select(ch =>
                (char.IsLetterOrDigit(ch) || ch == '_' || ch == '-' || ch == ' ') ? ch : '_').ToArray();
            var cleaned = new string(chars);
            while (cleaned.Contains("  ")) cleaned = cleaned.Replace("  ", " ");
            while (cleaned.Contains("__")) cleaned = cleaned.Replace("__", "_");
            return cleaned.Trim();
        }

        private static bool PackNameExists(string name, out string existingPath)
        {
            existingPath = null;
            var guids = AssetDatabase.FindAssets("t:ContentPackDefinition");
            foreach (var g in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(g);
                var assetName = Path.GetFileNameWithoutExtension(path);
                if (string.Equals(assetName, name, System.StringComparison.OrdinalIgnoreCase))
                {
                    existingPath = path;
                    return true;
                }
            }
            return false;
        }

        private bool AddressablesGroupExists(string name)
        {
            if (_settings == null) return false;
            return _settings.groups.Any(g => g != null && g.name.Equals(name, System.StringComparison.OrdinalIgnoreCase));
        }

        private void CreatePack()
        {
            _packsFolder = FORCED_PACKS_FOLDER;
            EnsureFolderExists(_packsFolder);

            _newPackName = SanitizePackName(_newPackName);
            if (string.IsNullOrWhiteSpace(_newPackName))
            {
                EditorUtility.DisplayDialog("Invalid Name", "Please enter a valid pack name.", "OK");
                return;
            }

            if (PackNameExists(_newPackName, out var existingPath))
            {
                EditorUtility.DisplayDialog("Duplicate Name",
                    $"A ContentPackDefinition named '{_newPackName}' already exists:\n{existingPath}\n\nChoose a different name.",
                    "OK");
                return;
            }

            if (AddressablesGroupExists(_newPackName))
            {
                EditorUtility.DisplayDialog("Duplicate Name",
                    $"An Addressables Group named '{_newPackName}' already exists.\n\nChoose a different name.",
                    "OK");
                return;
            }

            string assetPath = $"{_packsFolder}/{_newPackName}.asset";
            var instance = CreateInstance<ContentPackDefinition>();
            AssetDatabase.CreateAsset(instance, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            // create group + schema immediately
            instance.SyncToAddressables();

            EditorGUIUtility.PingObject(instance);
            Selection.activeObject = instance;

            RefreshPacks();
        }

        private static void CleanAddressablesGroups(AddressableAssetSettings settings)
        {
            if (settings == null) return;

            bool changed = false;

            for (int i = settings.groups.Count - 1; i >= 0; i--)
            {
                var g = settings.groups[i];
                if (g == null)
                {
                    settings.groups.RemoveAt(i);
                    changed = true;
                    continue;
                }

                var path = AssetDatabase.GetAssetPath(g);
                if (string.IsNullOrEmpty(path) || !File.Exists(path))
                {
                    settings.groups.RemoveAt(i);
                    changed = true;
                }
            }

            if (changed)
            {
                EditorUtility.SetDirty(settings);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
        }
    }
}
#endif
