#if UNITY_EDITOR
using System;
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using System.IO;

// pull in the icon capture utility
using Content_Icon_Capture.Editor;
using Object = UnityEngine.Object;

namespace ContentTools.Editor
{
    /// <summary>
    /// Content Pack Manager:
    ///  • Forced pack folder: Assets/ContentPacks
    ///  • Read-only Validation Rules panel + inline item issues
    ///  • Build hooks capture 2K icons per item before Addressables build
    ///  • Duplicate name checks only on "Create Pack" click
    ///  • Drag & Drop prefabs into packs (Project or Hierarchy)
    /// </summary>
    public class ContentPackBuilderWindow : EditorWindow
    {
        private const string FORCED_PACKS_FOLDER = "Assets/ContentPacks";
        private const string PREF_KEY_BUILD_LOCATION = "ContentPackBuilder.BuildLocation";

        private readonly List<ContentPackDefinition> _packs = new List<ContentPackDefinition>();
        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();
        private readonly Dictionary<string, bool> _selected = new Dictionary<string, bool>();

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
            GetWindow<ContentPackBuilderWindow>(true, "MG Content Manager");
        }

        private void OnEnable()
        {
            _settings = AddressableAssetSettingsDefaultObject.Settings;
            _buildLocation = EditorPrefs.GetString(PREF_KEY_BUILD_LOCATION, DefaultBuildFolderRel);

// If there isn't one saved yet, default to Assets/StreamingAssets/Addressables/Customization
            if (string.IsNullOrWhiteSpace(_buildLocation))
            {
                _buildLocation = DefaultBuildFolderRel; // keep as project-relative for UX
                // Don't save yet—only persist when the user builds or explicitly saves settings
            }


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
            foreach (ContentPackDefinition pack in _packs)
            {
                pack.RemoveMissingReferences();
            }

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
            }
            else if (!_warnedNoRules)
            {
                _warnedNoRules = true;
                Debug.LogWarning(
                    "No ContentValidationRules asset found. Create one via Tools → MashBox → Create Prefilled Validation Rules, or Assets → Create → Content → Validation Rules.");
            }
        }

        private const string DefaultBuildFolderRel = "Assets/StreamingAssets/Addressables/Customization";

        private static string ToProjectAbsolutePath(string path)
        {
            // Accept absolute paths as-is
            if (Path.IsPathRooted(path)) return path.Replace("\\", "/");

            // Accept project-relative "Assets/..." paths
            if (path.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                path.Equals("Assets", StringComparison.OrdinalIgnoreCase))
            {
                var projectRoot = Application.dataPath; // "<project>/Assets"
                var abs = Path.Combine(Path.GetDirectoryName(projectRoot) ?? "",
                    path.Replace("/", Path.DirectorySeparatorChar.ToString()));
                return Path.GetFullPath(abs).Replace("\\", "/");
            }

            // Anything else must be absolute; return as-is (will fail validation)
            return path.Replace("\\", "/");
        }

        private void OnGUI()
        {
            using (new EditorGUILayout.VerticalScope())
            {
                Header();
                GUILayout.Space(6);
                DrawCreateSection();
                GUILayout.Space(6);
                DrawRulesOverview();
                GUILayout.Space(6);
                DrawPacksList();
                GUILayout.Space(8);
                DrawBuildRow();
            }
        }

        private void Header()
        {
            if (_helpWrap == null) _helpWrap = new GUIStyle(EditorStyles.helpBox) { wordWrap = true, richText = true };
            if (_errStyle == null)
            {
                _errStyle = new GUIStyle(EditorStyles.boldLabel);
                _errStyle.normal.textColor = Color.red;
            }

            if (_warnStyle == null)
            {
                _warnStyle = new GUIStyle(EditorStyles.label);
                _warnStyle.normal.textColor = new Color(1f, 0.5f, 0f);
            }

            if (_miniHeader == null)
                _miniHeader = new GUIStyle(EditorStyles.miniBoldLabel) { alignment = TextAnchor.MiddleLeft };
            if (_dropZoneStyle == null)
            {
                _dropZoneStyle = new GUIStyle(EditorStyles.helpBox)
                {
                    alignment = TextAnchor.MiddleCenter,
                    fontStyle = FontStyle.Italic
                };
                _dropZoneStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
            }

            EditorGUILayout.LabelField("Content Manager", EditorStyles.boldLabel);
            GUILayout.Space(4);

            using (new EditorGUILayout.HorizontalScope())
            {
                using (new EditorGUILayout.VerticalScope())
                {
                    EditorGUILayout.LabelField("Build Output Folder", GUILayout.Width(140));

                    if (string.IsNullOrWhiteSpace(_buildLocation))
                    {
                        EditorGUILayout.HelpBox("Set a build output folder before building.", MessageType.Warning);
                    }
                }

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

            using (new EditorGUILayout.HorizontalScope())
            {
                if (!AssetDatabase.IsValidFolder(FORCED_PACKS_FOLDER))
                {
                    if (GUILayout.Button("Create Folder", GUILayout.Width(110)))
                    {
                        EnsureFolderExists(FORCED_PACKS_FOLDER);
                        AssetDatabase.Refresh();
                    }
                }
            }

            string sanitized = SanitizePackName(_newPackName);
            if (sanitized != _newPackName) _newPackName = sanitized;

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("New Pack Name", GUILayout.Width(110));
                _newPackName =
                    EditorGUILayout.TextField(_newPackName, GUILayout.MinWidth(250), GUILayout.ExpandWidth(true));

                GUI.enabled = !string.IsNullOrWhiteSpace(_newPackName);
                if (GUILayout.Button("Create Pack", GUILayout.Width(110)))
                {
                    string safe = SanitizePackName(_newPackName);
                    if (string.IsNullOrWhiteSpace(safe))
                    {
                        EditorUtility.DisplayDialog("Invalid Name", "Enter a valid pack name.", "OK");
                    }
                    else if (PackNameExists(safe, out var existingPath))
                    {
                        EditorUtility.DisplayDialog("Duplicate Name",
                            $"A ContentPackDefinition named '{safe}' already exists:\n{existingPath}",
                            "OK");
                    }
                    else if (AddressablesGroupExists(safe))
                    {
                        EditorUtility.DisplayDialog("Duplicate Group",
                            $"An Addressables Group named '{safe}' already exists.",
                            "OK");
                    }
                    else
                    {
                        CreatePackWithName(safe);
                    }
                }

                GUI.enabled = true;
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
                        var types = (pair.Types != null && pair.Types.Length > 0)
                            ? string.Join(", ", pair.Types)
                            : "<none>";
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

                    return;
                }

                for (int j = 0; j < _packs.Count; j++)
                {
                    var p = _packs[j];
                    if (p == null) continue;
                    var key = AssetDatabase.GetAssetPath(p);
                    if (string.IsNullOrEmpty(key)) continue;

                    if (!_foldouts.ContainsKey(key)) _foldouts[key] = true;
                    if (!_selected.ContainsKey(key)) _selected[key] = true;

                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _selected[key] = EditorGUILayout.Toggle(_selected[key], GUILayout.Width(18));
                            _foldouts[key] = EditorGUILayout.Foldout(_foldouts[key], p.name, true);
                            GUILayout.FlexibleSpace();

                            if (GUILayout.Button("Generate Icons", GUILayout.Width(120)))
                                GenerateIconsForPack(p);

                            if (GUILayout.Button("Delete", GUILayout.Width(70)))
                                DeletePack(p);
                        }


                        if (_foldouts.ContainsKey(key))
                            if (_foldouts[key])
                            {

                                EditorGUI.indentLevel++;
                                // Items list with inline validation
                                if (p._items != null && p._items.Count > 0)
                                {
                                    // Use for-loop so we can safely remove by index
                                    for (int i = 0; i < p._items.Count; i++)
                                    {
                                        var go = p._items[i];

                                        using (new EditorGUILayout.HorizontalScope())
                                        {

                                            // Draw issues under the row (skip if item was deleted above)
                                            if (i >= 0 && i < p._items.Count)
                                                DrawItemIssuesUI(p._items[i], true);

                                            // Object field (read-only presentation but still shows ping/select, keep editable if you prefer)
                                            EditorGUI.BeginChangeCheck();
                                            var next = (GameObject)EditorGUILayout.ObjectField(go, typeof(GameObject),
                                                false);

                                            // Spacer
                                            //GUILayout.FlexibleSpace();

                                            // Remove button
                                            if (GUILayout.Button("X", GUILayout.Width(26)))
                                            {
                                                Undo.RecordObject(p, "Remove Item From Pack");

                                                // Remove from pack
                                                p._items.RemoveAt(i);

                                                // Keep the validation map tidy
                                                if (go != null && _itemIssues.ContainsKey(go))
                                                    _itemIssues.Remove(go);

                                                EditorUtility.SetDirty(p);
                                                AssetDatabase.SaveAssets();

                                                // Adjust index since we removed current item
                                                i--;


                                                p.SyncToAddressables();
                                                p.SyncToAddressables();
                                                // Exit GUI so Repaint doesn't clash with changed layout
                                                Repaint();
                                                GUIUtility.ExitGUI();
                                            }
                                            else if (EditorGUI.EndChangeCheck())
                                            {
                                                // If user changed the reference in-place, update and revalidate
                                                Undo.RecordObject(p, "Change Pack Item");
                                                p._items[i] = next;

                                                if (next != null)
                                                    _itemIssues[next] = ContentPackValidator.ValidateItem(next, _rules);

                                                if (go != null && go != next && _itemIssues.ContainsKey(go))
                                                    _itemIssues.Remove(go);

                                                EditorUtility.SetDirty(p);
                                                AssetDatabase.SaveAssets();
                                                Repaint();
                                            }

                                        }

                                        // Draw issues under the row (skip if item was deleted above)
                                        if (i >= 0 && i < p._items.Count)
                                            DrawItemIssuesUI(p._items[i], false);
                                    }
                                }
                                else
                                {
                                    EditorGUILayout.LabelField("<no items>", EditorStyles.miniLabel);
                                }


                                // --- Drag & Drop zone always visible for convenience ---
                                var dropRect = GUILayoutUtility.GetRect(0, 20, GUILayout.ExpandWidth(true));
                                GUI.Box(dropRect, "Drag prefabs here", _dropZoneStyle);
                                HandleDragAndDropForPack(p, dropRect);

                                EditorGUI.indentLevel--;
                            }
                    }
                }
            }
        }

        private void DrawBuildRow()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("Build Selected", GUILayout.Width(120)))
                {
                    var toBuild = _packs.Where(p =>
                        p != null && _selected.TryGetValue(AssetDatabase.GetAssetPath(p), out var sel) && sel).ToList();
                    if (toBuild.Count == 0)
                    {
                        EditorUtility.DisplayDialog("Nothing Selected", "Select one or more packs to build.", "OK");
                        return;
                    }

                    foreach (var pack in toBuild)
                    {
                        var issues = ValidatePack(pack, _rules);
                        if (issues.Any(i => i.severity == ContentPackValidator.Severity.Error))
                        {
                            ContentPackValidator.LogReport(pack, issues, "Build blocked");
                            EditorUtility.DisplayDialog("Build blocked",
                                $"'{pack.name}' has validation errors. See Console.", "OK");
                            return;
                        }
                    }

                    BuildPacks(toBuild, cleanMissing: true);
                }

                if (GUILayout.Button("Build All", GUILayout.Width(90)))
                {
                    foreach (var pack in _packs)
                    {
                        var issues = ValidatePack(pack, _rules);
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

        private bool EnsureValidBuildFolder(out string error)
        {
            error = null;

            if (string.IsNullOrWhiteSpace(_buildLocation))
            {
                error = "No build output folder set.";
                return false;
            }

            // Normalize slashes
            _buildLocation = _buildLocation.Replace("\\", "/");

            // Must be an absolute path
            if (!Path.IsPathRooted(_buildLocation))
            {
                error = $"Build output folder must be an absolute path:\n{_buildLocation}";
                return false;
            }

            try
            {
                if (!Directory.Exists(_buildLocation))
                    Directory.CreateDirectory(_buildLocation);

                // Verify write access with a quick temp file probe
                var probe = Path.Combine(_buildLocation, ".write_probe.tmp");
                File.WriteAllText(probe, "ok");
                File.Delete(probe);
                return true;
            }
            catch (System.Exception ex)
            {
                error = $"Build output folder is not writable:\n{_buildLocation}\n\n{ex.Message}";
                return false;
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


            string buildErr;
            if (!EnsureValidBuildFolder(out buildErr))
            {
                EditorUtility.DisplayDialog("Invalid Build Output Folder", buildErr, "OK");
                return;
            }

            RefreshPacks();

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
                    {
                        EditorUtility.SetDirty(p);
                        p.SyncToAddressables();
                    }

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

                sessionRemoteBuildRootOverride =
                    string.IsNullOrEmpty(_buildLocation) ? null : _buildLocation.Replace("\\", "/"),
                sessionRemoteLoadRootOverride = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}",
            };

            int built = 0;
            foreach (var p in list)
            {
                if (p == null) continue;

                // Capture 2K icons before building
                try
                {
                    var items = (p._items ?? new List<GameObject>()).Where(x => x != null);
                    ContentIconCaptureUtility.CaptureIconsForPrefabs(
                        items,
                        renderSize: 2048,
                        outputSize: 2048,
                        imageType: ContentIconCaptureUtility.ImageType.PNG
                    );
                    Debug.Log($"[ContentPackBuilder] Captured 2K icons for pack '{p.name}'.");
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[ContentPackBuilder] Icon capture failed for '{p?.name}': {ex.Message}");
                }
                
                try
                {
                    if (p._icons == null) p._icons = new List<Texture2D>();
                    bool changedIcons = false;

                    foreach (var go in (p._items ?? new List<GameObject>()).Where(x => x != null))
                    {
                        string folder;
                        var iconPath = ComputeIconPathForPrefab(go, out folder);
                        if (string.IsNullOrEmpty(iconPath)) continue;

                        var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
                        if (tex != null && !p._icons.Contains(tex))
                        {
                            p._icons.Add(tex);
                            changedIcons = true;
                        }
                    }

                    if (changedIcons)
                    {
                        EditorUtility.SetDirty(p);
                        AssetDatabase.SaveAssets();
                    }

                    // ensure the _Icons group is updated before the addressables build for this pack
                    p.SyncToAddressables();
                }
                catch (System.Exception exCollect)
                {
                    Debug.LogError($"[ContentPackBuilder] Icon registration failed for '{p?.name}': {exCollect.Message}");
                }

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

        // ---------- Drag & Drop support ----------
        private void HandleDragAndDropForPack(ContentPackDefinition pack, Rect dropRect)
        {
            var e = Event.current;
            if (!dropRect.Contains(e.mousePosition)) return;

            // Accept GameObjects / prefab assets
            bool HasValidObject(Object o)
            {
                if (o is GameObject go)
                {
                    // If it's a scene object, try to resolve to prefab asset
                    var path = AssetDatabase.GetAssetPath(go);
                    if (!string.IsNullOrEmpty(path) && path.EndsWith(".prefab"))
                        return true;

                    // Try linked prefab
                    var prefab = PrefabUtility.GetCorrespondingObjectFromSource(go);
                    if (prefab != null)
                    {
                        var p = AssetDatabase.GetAssetPath(prefab);
                        return !string.IsNullOrEmpty(p) && p.EndsWith(".prefab");
                    }
                }

                return false;
            }

            if (e.type == EventType.DragUpdated || e.type == EventType.DragPerform)
            {
                var anyValid = DragAndDrop.objectReferences.Any(HasValidObject);
                DragAndDrop.visualMode = anyValid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;

                if (e.type == EventType.DragPerform && anyValid)
                {
                    DragAndDrop.AcceptDrag();

                    Undo.RecordObject(pack, "Add Items To Pack");
                    if (pack._items == null) pack._items = new List<GameObject>();

                    foreach (var obj in DragAndDrop.objectReferences)
                    {
                        if (!(obj is GameObject go)) continue;

                        // Prefer prefab asset version
                        GameObject assetGo = null;
                        var assetPath = AssetDatabase.GetAssetPath(go);
                        if (!string.IsNullOrEmpty(assetPath) && assetPath.EndsWith(".prefab"))
                        {
                            assetGo = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                        }
                        else
                        {
                            var fromSource = PrefabUtility.GetCorrespondingObjectFromSource(go);
                            if (fromSource != null)
                            {
                                var srcPath = AssetDatabase.GetAssetPath(fromSource);
                                if (!string.IsNullOrEmpty(srcPath) && srcPath.EndsWith(".prefab"))
                                    assetGo = AssetDatabase.LoadAssetAtPath<GameObject>(srcPath);
                            }
                        }

                        if (assetGo == null) continue;
                        if (!pack._items.Contains(assetGo))
                            pack._items.Add(assetGo);

                        // validate on add
                        _itemIssues[assetGo] = ContentPackValidator.ValidateItem(assetGo, _rules);

                        pack.SyncToAddressables();
                    }

                    EditorUtility.SetDirty(pack);
                    AssetDatabase.SaveAssets();
                    Repaint();
                }

                e.Use();
            }
        }

        // ---------- Validation helpers (live inline UI) ----------
        private void ValidateItemLive(GameObject go)
        {
            if (!go) return;
            var issues = ContentPackValidator.ValidateItem(go, _rules);
            _itemIssues[go] = issues;
            Repaint();
        }

        private void DrawItemIssuesUI(GameObject go, bool onlyValid)
        {
            if (!go) return;
            if (!_itemIssues.TryGetValue(go, out var issues) || issues == null) return;

            int errorCount = issues.Count(i => i.severity == ContentPackValidator.Severity.Error);
            int warnCount = issues.Count(i => i.severity == ContentPackValidator.Severity.Warning);

            if (errorCount == 0 && warnCount == 0 && onlyValid)
            {
                GUILayout.Label("✓ Valid", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                return;
            }
            else if (onlyValid)
            {
                return;
            }

            using (new EditorGUILayout.HorizontalScope())
            {
                if (errorCount == 0 && warnCount == 0)
                {
                    // GUILayout.Label("✓ Valid", EditorStyles.miniBoldLabel, GUILayout.Width(60));
                }
                else if (!onlyValid)
                {
                    if (errorCount > 0)
                        GUILayout.Label($"Errors: {errorCount}", _errStyle, GUILayout.Width(120));
                    if (warnCount > 0)
                        GUILayout.Label($"Warnings: {warnCount}", _warnStyle, GUILayout.Width(140));
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

        // ---------- Pack list + Addressables helpers ----------
        private void CreatePackWithName(string safeName)
        {
            EnsureFolderExists(FORCED_PACKS_FOLDER);

            string assetPath = $"{FORCED_PACKS_FOLDER}/{safeName}.asset";
            var asset = ScriptableObject.CreateInstance<ContentPackDefinition>();
            AssetDatabase.CreateAsset(asset, assetPath);
            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();

            asset.SyncToAddressables();
            //EditorGUIUtility.PingObject(asset);
            RefreshPacks();
        }

        private void RefreshPacks()
        {
            _packs.Clear();
            _foldouts.Clear();
            _selected.Clear();

            var guids = AssetDatabase.FindAssets("t:ContentPackDefinition", new[] { FORCED_PACKS_FOLDER });
            foreach (var guid in guids)
            {
                var path = AssetDatabase.GUIDToAssetPath(guid);
                var pack = AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(path);
                if (pack != null) _packs.Add(pack);
            }


            foreach (ContentPackDefinition pack in _packs)
            {
                pack.RemoveMissingReferences();
            }

            Repaint();
        }

        private static void EnsureFolderExists(string folder)
        {
            var parts = folder.Split('/');
            string cur = "";
            for (int i = 0; i < parts.Length; i++)
            {
                if (i == 0 && parts[i] == "Assets")
                {
                    cur = "Assets";
                    continue;
                }

                var parent = string.IsNullOrEmpty(cur) ? "Assets" : cur;
                var name = parts[i];
                if (!AssetDatabase.IsValidFolder(Path.Combine(parent, name)))
                    AssetDatabase.CreateFolder(parent, name);
                cur = Path.Combine(parent, name).Replace("\\", "/");
            }
        }

        private static string SanitizePackName(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return string.Empty;
            var invalid = Path.GetInvalidFileNameChars();
            var chars = raw.Trim().Select(c => invalid.Contains(c) ? '_' : c).ToArray();
            return new string(chars);
        }

        private static bool PackNameExists(string packName, out string path)
        {
            path = null;
            var guids = AssetDatabase.FindAssets($"t:ContentPackDefinition {packName}");
            foreach (var guid in guids)
            {
                var p = AssetDatabase.GUIDToAssetPath(guid);
                var name = Path.GetFileNameWithoutExtension(p);
                if (string.Equals(name, packName, System.StringComparison.OrdinalIgnoreCase))
                {
                    path = p;
                    return true;
                }
            }

            return false;
        }

        private bool AddressablesGroupExists(string groupName)
        {
            var s = AddressableAssetSettingsDefaultObject.Settings;
            if (s == null) return false;
            return s.groups.Any(g => g != null && g.name == groupName);
        }

        private static void CleanAddressablesGroups(AddressableAssetSettings s)
        {
            if (s == null) return;
            var empties = s.groups.Where(g =>
                    g != null && g != s.DefaultGroup && !g.ReadOnly && (g.entries == null || g.entries.Count == 0))
                .ToList();
            foreach (var g in empties)
                s.RemoveGroup(g);
        }

        private static List<ContentPackValidator.Issue> ValidatePack(ContentPackDefinition pack,
            ContentValidationRules rules)
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

        private static string ComputeIconPathForPrefab(GameObject prefab, out string folder)
        {
            folder = null;
            if (!prefab) return null;
            var prefabPath = AssetDatabase.GetAssetPath(prefab);
            if (string.IsNullOrEmpty(prefabPath)) return null;

            // Mirror "…/Prefabs/Foo.prefab" → "…/Icons/Foo_Icon.png"
            var dir = Path.GetDirectoryName(prefabPath)?.Replace("\\", "/") ?? "Assets";
            folder = dir.Replace("/Prefabs", "/Icons");
            var fileNameNoExt = Path.GetFileNameWithoutExtension(prefabPath) + "_Icon";
            return Path.Combine(folder, fileNameNoExt + ".png").Replace("\\", "/");
        }

        private void GenerateIconsForPack(ContentPackDefinition pack)
        {
            if (pack == null) return;

            var items = (pack._items ?? new List<GameObject>()).Where(x => x != null).ToList();
            if (items.Count == 0)
            {
                Debug.LogWarning($"[ContentPackBuilder] No items in '{pack.name}' to capture icons for.");
                return;
            }

            // 1) Capture icons (same utility used by build)
            try
            {
                Content_Icon_Capture.Editor.ContentIconCaptureUtility.CaptureIconsForPrefabs(
                    items,
                    renderSize: 2048,
                    outputSize: 2048,
                    imageType: Content_Icon_Capture.Editor.ContentIconCaptureUtility.ImageType.PNG
                );
                Debug.Log($"[ContentPackBuilder] Generated icons for pack '{pack.name}'.");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ContentPackBuilder] Icon capture failed for '{pack?.name}': {ex.Message}");
                return;
            }

            // 2) Load the generated textures and record them on the pack
            bool changed = false;
            if (pack._icons == null) pack._icons = new List<Texture2D>();

            foreach (var prefab in items)
            {
                string folder;
                var iconPath = ComputeIconPathForPrefab(prefab, out folder);
                if (string.IsNullOrEmpty(iconPath)) continue;

                var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
                if (tex != null && !pack._icons.Contains(tex))
                {
                    pack._icons.Add(tex);
                    changed = true;
                }
            }

            if (changed)
            {
                EditorUtility.SetDirty(pack);
                AssetDatabase.SaveAssets();
            }

            // 3) Push the icons into the "{PackName}_Icons" addressable group
            pack.SyncToAddressables();
        }

    }
}
#endif
