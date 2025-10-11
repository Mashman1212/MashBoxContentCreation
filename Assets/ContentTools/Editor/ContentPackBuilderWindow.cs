
#if UNITY_EDITOR
using System;
using System.Linq;
using System.Collections.Generic;
using System.Diagnostics;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;

using Content_Icon_Capture.Editor;
using Debug = UnityEngine.Debug;
using Object = UnityEngine.Object;

using ContentTools.Editor.SteamDetect;
using ContentTools.ModIo;

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
        [System.Serializable]
        public struct AllowedGame
        {
            public string DisplayName;
            public long SteamAppId;
            public string ModIoApiBase;
        }

        private static readonly AllowedGame[] ALLOWED_GAMES = new[]
        {
            new AllowedGame
            {
                DisplayName = "BMXS",
                SteamAppId = 871540,
                ModIoApiBase = "https://g-10073.modapi.io/v1"
            },
            new AllowedGame
            {
                DisplayName = "ScootX",
                SteamAppId = 3800340,
                ModIoApiBase = "https://g-2835.modapi.io/v1"
            },
        };

        private const string STREAMING_SUBPATH = "Addressables/Customization"; // under StreamingAssets

// Cache of detections (AppID -> install path)
        private System.Collections.Generic.Dictionary<long, string> _steamInstalls =
            new System.Collections.Generic.Dictionary<long, string>();

        private long _lastChosenAppId = 0; // persisted for UX
        private const string PREF_KEY_LAST_APP = "ContentPackBuilder.LastChosenAppId";


        private const string FORCED_PACKS_FOLDER = "Assets/Content/Content Pack Data";
        private const string PREF_KEY_BUILD_LOCATION = "ContentPackBuilder.BuildLocation";

        private const string PREF_KEY_PROXY_BASE = "ModIo.ProxyBase";

        private const string DEFAULT_PROXY_BASE = "https://modio-proxy-cgf2e7hvc6fggsh6.centralus-01.azurewebsites.net/modio";

        private const string UGC_REQUEST_PATH = "https://modio-proxy-cgf2e7hvc6fggsh6.centralus-01.azurewebsites.net/ugc/request-upload";

        private static string ProxyHostBase => EditorPrefs.GetString(PREF_KEY_PROXY_BASE, DEFAULT_PROXY_BASE)
            .TrimEnd('/').Replace("/modio", "");

        private static string UploaderEndpoint => UGC_REQUEST_PATH;



        private readonly List<ContentPackDefinition> _packs = new List<ContentPackDefinition>();
        private readonly Dictionary<string, bool> _foldouts = new Dictionary<string, bool>();

        private Dictionary<string, bool> _metaFoldouts = new Dictionary<string, bool>();

        // NEW: foldout memory for the rules section grouped by SuperType
        private readonly Dictionary<string, bool> _rulesSuperFoldouts = new Dictionary<string, bool>();

        private AddressableAssetSettings _settings;
        private string _buildLocation = string.Empty;

        private string _newPackName = "NewContentPack";
        private string _packsFolder = FORCED_PACKS_FOLDER;

        [SerializeField] private ContentValidationRules _rules;

        private readonly Dictionary<GameObject, List<ContentPackValidator.Issue>> _itemIssues
            = new Dictionary<GameObject, List<ContentPackValidator.Issue>>();

        private static bool _warnedNoRules;

        private static GUIStyle _helpWrap, _errStyle, _warnStyle, _miniHeader, _dropZoneStyle;

// UI state: Build Output Target panel foldout (default open)
        private bool _targetFoldout = true;

// Cache Steam library images (capsules/headers) by AppID
        private readonly Dictionary<long, Texture2D> _steamCapsuleCache = new Dictionary<long, Texture2D>();
        private bool _rulesFoldout = false;
        private Vector2 _scroll;

        private const string HEADER_RESOURCE_NAME = "ContentManager_Header";
        private Texture2D _headerTex;

// Footer (button bar) height
        private const float FOOTER_H = 40f;

        [MenuItem("MashBox/Content Manager")]
        public static void Open()
        {
            GetWindow<ContentPackBuilderWindow>(true, "MG Content Manager");

            EditorPrefs.SetString(PREF_KEY_PROXY_BASE, DEFAULT_PROXY_BASE);
        }

        private void OnEnable()
        {
            _headerTex = Resources.Load<Texture2D>(HEADER_RESOURCE_NAME);
            _settings = AddressableAssetSettingsDefaultObject.Settings;
            _buildLocation = EditorPrefs.GetString(PREF_KEY_BUILD_LOCATION, DefaultBuildFolderRel);


            _lastChosenAppId = long.TryParse(EditorPrefs.GetString(PREF_KEY_LAST_APP, "0"), out var v) ? v : 0;

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

            CleanAddressableData();

            EditorApplication.projectChanged += OnProjectChanged;

            RevalidateAllItems();

// Detect Steam installs for allowed games
            var ids = ALLOWED_GAMES.Select(g => g.SteamAppId).ToArray();
            _steamInstalls = SteamLocator.TryGetGameInstallPaths(ids);
        }

        private void OnDisable()
        {
            EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation ?? string.Empty);
            EditorPrefs.SetString(PREF_KEY_LAST_APP, _lastChosenAppId.ToString());
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


//  — smaller, tighter cap
        private const float HEADER_MIN = 56f;
        private const float HEADER_MAX = 80f;
        private float _headerMeasuredH = 64f;

        private void DrawHeaderBanner()
        {
            if (_headerTex == null)
                _headerTex = Resources.Load<Texture2D>(HEADER_RESOURCE_NAME);

            if (_headerTex != null)
            {
                float vw = EditorGUIUtility.currentViewWidth;
                float aspect = (float)_headerTex.height / Mathf.Max(1, _headerTex.width);
                float desiredH = Mathf.Clamp(vw * aspect, HEADER_MIN, HEADER_MAX);

                // This reserves layout height and returns the rect we draw into
                Rect r = GUILayoutUtility.GetRect(GUIContent.none, GUIStyle.none,
                    GUILayout.Height(desiredH), GUILayout.ExpandWidth(true));

                _headerMeasuredH = r.height; // <-- cache the height layout actually used

                // background + image + divider (unchanged)
                var card = new Rect(r.x, r.y, r.width, r.height);
                var cardCol = EditorGUIUtility.isProSkin ? new Color(1f, 1f, 1f, 0.035f) : new Color(0f, 0f, 0f, 0.05f);
                EditorGUI.DrawRect(card, cardCol);
                GUI.DrawTexture(card, _headerTex, ScaleMode.ScaleAndCrop, true);
                var div = new Rect(card.x, card.yMax, card.width, 1f);
                EditorGUI.DrawRect(div,
                    EditorGUIUtility.isProSkin ? new Color(1, 1, 1, 0.08f) : new Color(0, 0, 0, 0.12f));
            }
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

        private Vector2 _mainScroll;

        private string _currentGameName;

        private void OnGUI()
        {
            _currentGameName = EditorPrefs.GetString("ModIo.CurrentGame", "this game");

            // 1) Header (fixed, layout-managed)
            DrawHeaderBanner();

            // 2) Middle = one scroll, sized by remaining window height
            float bodyH = Mathf.Max(0f, position.height - FOOTER_H - _headerMeasuredH);

            using (var sv = new EditorGUILayout.ScrollViewScope(_mainScroll, GUILayout.Height(bodyH)))
            {
                _mainScroll = sv.scrollPosition;

                using (new EditorGUILayout.VerticalScope())
                {
                    Header();
                    DrawModIoSection();
                    GUILayout.Space(6);

                    // Validation Rules auto-expands (no inner scroll)
                    DrawRulesOverview();
                    GUILayout.Space(6);

                    DrawCreateSection();
                    GUILayout.Space(6);

                    // Ensure DrawPacksList() has no inner ScrollViewScope
                    DrawPacksList();
                    GUILayout.Space(8);

                    // 👇 NEW: bottom spacer so the last items don’t sit under the footer
                    GUILayout.Space(FOOTER_H + 8f);
                }
            }

            // 3) Footer (fixed): draw in a bottom area so it never scrolls
            var footerRect = new Rect(0, position.height - FOOTER_H, position.width, FOOTER_H);
            using (new GUILayout.AreaScope(footerRect))
            {
                // (optional) subtle bg + divider
                //if (Event.current.type == EventType.Repaint)
                //{
                //    var bg = EditorGUIUtility.isProSkin ? new Color(1,1,1,0.035f) : new Color(0,0,0,0.06f);
                //    EditorGUI.DrawRect(new Rect(0,0,footerRect.width,footerRect.height), bg);
                //    EditorGUI.DrawRect(new Rect(0,0,footerRect.width,1f),
                //        EditorGUIUtility.isProSkin ? new Color(1,1,1,0.08f) : new Color(0,0,0,0.12f));
                //}

                GUILayout.Space(4);
                //DrawBuildRow(); // your centered, larger buttons
                GUILayout.Space(2);
            }
        }


        private void Header()
        {
            if (_helpWrap == null) _helpWrap = new GUIStyle(EditorStyles.helpBox) { wordWrap = true, richText = true };
            if (_errStyle == null)
            {
                _errStyle = new GUIStyle(EditorStyles.boldLabel);
                _errStyle.normal.textColor = new Color(.9f, 0.25f, .25f);
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
                    { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Italic };
                _dropZoneStyle.normal.textColor = new Color(0.75f, 0.75f, 0.75f);
            }

            // EditorGUILayout.LabelField("Content Manager", EditorStyles.boldLabel);
            //GUILayout.Space(4);

            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _targetFoldout = EditorGUILayout.Foldout(_targetFoldout, "Game", true);
                if (!_targetFoldout)
                    return;

                using (new EditorGUILayout.HorizontalScope())
                {
                    GUILayout.Label("Steam Root:", GUILayout.Width(90));
                    var root = SteamLocator.GetSteamRoot() ?? "<not found>";
                    EditorGUILayout.SelectableLabel(root, GUILayout.Height(16));
                    GUILayout.FlexibleSpace();
                    if (GUILayout.Button("Rescan", GUILayout.Width(80)))
                    {
                        var ids = ALLOWED_GAMES.Select(g => g.SteamAppId).ToArray();
                        _steamInstalls = SteamLocator.TryGetGameInstallPaths(ids);
                    }
                }

                GUILayout.Space(4);

                foreach (var g in ALLOWED_GAMES)
                {
                    // Breakpoints you can tune
                    float vw = EditorGUIUtility.currentViewWidth;
                    bool narrow = vw < 520f; // buttons wrap to a new row
                    bool ultraNarrow = vw < 380f; // buttons stack vertically

                    using (new EditorGUILayout.VerticalScope())
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            string install = null;
                            var detected = _steamInstalls.TryGetValue(g.SteamAppId, out install);
                            bool isActive = (_lastChosenAppId == g.SteamAppId) && !string.IsNullOrEmpty(_buildLocation);

                            // left image
                            var tex = Resources.Load<Texture2D>(g.DisplayName + "_Logo");
                            var imgRect = GUILayoutUtility.GetRect(96, 48, GUILayout.Width(96), GUILayout.Height(48));
                            if (Event.current.type == EventType.Repaint)
                            {
                                var bg = isActive
                                    ? new Color(0, 0, 0, 0.14f)
                                    : (EditorGUIUtility.isProSkin
                                        ? new Color(1, 1, 1, 0.00f)
                                        : new Color(0, 0, 0, 0.00f));
                                EditorGUI.DrawRect(imgRect, bg);
                            }

                            if (tex != null) GUI.DrawTexture(imgRect, tex, ScaleMode.ScaleAndCrop);

                            // middle text
                            using (new EditorGUILayout.VerticalScope())
                            {
                                var nameStyle = new GUIStyle(EditorStyles.label);
                                if (isActive) nameStyle.normal.textColor = Color.green;
                                EditorGUILayout.LabelField(g.DisplayName, nameStyle);

                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    var status = detected ? "Installed" : "Not installed";
                                    var statusStyle = new GUIStyle(EditorStyles.miniLabel);
                                    statusStyle.normal.textColor = detected
                                        ? (isActive
                                            ? Color.green
                                            : (EditorGUIUtility.isProSkin
                                                ? new Color(0.7f, 0.7f, 0.7f)
                                                : new Color(0.2f, 0.2f, 0.2f)))
                                        : new Color(0.9f, 0.3f, 0.3f);

                                    EditorGUILayout.LabelField(status, statusStyle, GUILayout.Width(100));
                                    if (isActive)
                                    {
                                        EditorPrefs.SetString("ModIo.ApiBase", g.ModIoApiBase);
                                        EditorPrefs.SetString("ModIo.CurrentGame", g.DisplayName);
                                        var activeStyle = new GUIStyle(EditorStyles.miniBoldLabel);
                                        activeStyle.normal.textColor = Color.green;
                                        EditorGUILayout.LabelField("✓ Active target", activeStyle);
                                    }
                                }
                            }

                            GUILayout.FlexibleSpace();
//
                            // Wide layout: keep buttons on the same row
                            if (!narrow)
                            {
                                using (new EditorGUI.DisabledScope(!detected))
                                {
                                    if (!isActive)
                                        if (GUILayout.Button(isActive ? "Re-set Target" : "Set Game",
                                                GUILayout.MinWidth(110), GUILayout.Height(22)))
                                        {
                                            var sa = StreamingAssetsResolver.TryResolve(install);
                                            if (string.IsNullOrEmpty(sa))
                                            {
                                                EditorUtility.DisplayDialog("StreamingAssets not found",
                                                    $"Could not locate StreamingAssets inside:\n{install}\n\n" +
                                                    "Launch the game once via Steam, then Rescan.", "OK");
                                            }
                                            else
                                            {
                                                var final = StreamingAssetsResolver.AppendSubfolder(sa,
                                                    STREAMING_SUBPATH);
                                                _buildLocation = final;
                                                _lastChosenAppId = g.SteamAppId;
                                                _codeInput = "";
                                                EditorPrefs.SetString("ModIo.ApiBase", g.ModIoApiBase);
                                                EditorPrefs.SetString("ModIo.CurrentGame", g.DisplayName);
                                                _statusMsg = ContentTools.ModIo.ModIoAuth.IsAuthorizedForCurrentGame()
                                                    ? $"✅ mod.io authorized for {g.DisplayName}"
                                                    : $"ℹ️ Please verify mod.io for {g.DisplayName}.";
                                                Repaint();

                                                EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
                                                EditorPrefs.SetString(PREF_KEY_LAST_APP, _lastChosenAppId.ToString());
                                                Debug.Log(
                                                    $"[ContentPackBuilder] Target set to {g.DisplayName}: {_buildLocation}");
                                            }
                                        }
                                }

                                using (new EditorGUI.DisabledScope(!detected))
                                {
                                    if (GUILayout.Button("Open Folder", GUILayout.MinWidth(100), GUILayout.Height(22)))
                                    {
                                        var sa = StreamingAssetsResolver.TryResolve(install);
                                        var final = StreamingAssetsResolver.AppendSubfolder(sa, STREAMING_SUBPATH);
                                        OpenBuildOutputFolder(final);
                                    }
                                }
                            }
                        }

                        // Narrow layout: wrap buttons to a new row and let them expand
                        if (narrow)
                        {
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                // indent under the 96px logo + a small gutter
                                GUILayout.Space(96f + 8f);

                                if (ultraNarrow)
                                {
                                    using (new EditorGUILayout.VerticalScope())
                                    {
                                        using (new EditorGUI.DisabledScope(!_steamInstalls.ContainsKey(g.SteamAppId)))
                                        {
                                            if (GUILayout.Button(
                                                    (_lastChosenAppId == g.SteamAppId &&
                                                     !string.IsNullOrEmpty(_buildLocation))
                                                        ? "Re-set Target"
                                                        : "Set Target",
                                                    GUILayout.ExpandWidth(true), GUILayout.Height(22)))
                                            {
                                                var install = _steamInstalls[g.SteamAppId];
                                                var sa = StreamingAssetsResolver.TryResolve(install);
                                                if (string.IsNullOrEmpty(sa))
                                                {
                                                    EditorUtility.DisplayDialog("StreamingAssets not found",
                                                        $"Could not locate StreamingAssets inside:\n{install}\n\n" +
                                                        "Launch the game once via Steam, then Rescan.", "OK");
                                                }
                                                else
                                                {
                                                    var final = StreamingAssetsResolver.AppendSubfolder(sa,
                                                        STREAMING_SUBPATH);
                                                    _buildLocation = final;
                                                    _lastChosenAppId = g.SteamAppId;
                                                    EditorPrefs.SetString("ModIo.ApiBase", g.ModIoApiBase);
                                                    EditorPrefs.SetString("ModIo.CurrentGame", g.DisplayName);
                                                    _statusMsg =
                                                        ContentTools.ModIo.ModIoAuth.IsAuthorizedForCurrentGame()
                                                            ? $"✅ mod.io authorized for {g.DisplayName}"
                                                            : $"ℹ️ Please verify mod.io for {g.DisplayName}.";
                                                    Repaint();

                                                    EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
                                                    EditorPrefs.SetString(PREF_KEY_LAST_APP,
                                                        _lastChosenAppId.ToString());
                                                    Debug.Log(
                                                        $"[ContentPackBuilder] Target set to {g.DisplayName}: {_buildLocation}");
                                                }
                                            }
                                        }

                                        using (new EditorGUI.DisabledScope(!_steamInstalls.ContainsKey(g.SteamAppId)))
                                        {
                                            if (GUILayout.Button("Open Folder", GUILayout.ExpandWidth(true),
                                                    GUILayout.Height(22)))
                                            {
                                                var install = _steamInstalls[g.SteamAppId];
                                                var sa = StreamingAssetsResolver.TryResolve(install);
                                                var final = StreamingAssetsResolver.AppendSubfolder(sa,
                                                    STREAMING_SUBPATH);
                                                OpenBuildOutputFolder(final);
                                            }
                                        }
                                    }
                                }
                                else
                                {
                                    using (new EditorGUI.DisabledScope(!_steamInstalls.ContainsKey(g.SteamAppId)))
                                    {
                                        if (GUILayout.Button(
                                                (_lastChosenAppId == g.SteamAppId &&
                                                 !string.IsNullOrEmpty(_buildLocation))
                                                    ? "Re-set Target"
                                                    : "Set Target",
                                                GUILayout.ExpandWidth(true), GUILayout.Height(22)))
                                        {
                                            var install = _steamInstalls[g.SteamAppId];
                                            var sa = StreamingAssetsResolver.TryResolve(install);
                                            if (string.IsNullOrEmpty(sa))
                                            {
                                                EditorUtility.DisplayDialog("StreamingAssets not found",
                                                    $"Could not locate StreamingAssets inside:\n{install}\n\n" +
                                                    "Launch the game once via Steam, then Rescan.", "OK");
                                            }
                                            else
                                            {
                                                var final = StreamingAssetsResolver.AppendSubfolder(sa,
                                                    STREAMING_SUBPATH);
                                                _buildLocation = final;
                                                _lastChosenAppId = g.SteamAppId;
                                                EditorPrefs.SetString("ModIo.ApiBase", g.ModIoApiBase);
                                                EditorPrefs.SetString("ModIo.CurrentGame", g.DisplayName);
                                                _statusMsg = ContentTools.ModIo.ModIoAuth.IsAuthorizedForCurrentGame()
                                                    ? $"✅ mod.io authorized for {g.DisplayName}"
                                                    : $"ℹ️ Please verify mod.io for {g.DisplayName}.";
                                                Repaint();

                                                EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
                                                EditorPrefs.SetString(PREF_KEY_LAST_APP, _lastChosenAppId.ToString());
                                                Debug.Log(
                                                    $"[ContentPackBuilder] Target set to {g.DisplayName}: {_buildLocation}");
                                            }
                                        }
                                    }

                                    using (new EditorGUI.DisabledScope(!_steamInstalls.ContainsKey(g.SteamAppId)))
                                    {
                                        if (GUILayout.Button("Open Folder", GUILayout.ExpandWidth(true),
                                                GUILayout.Height(22)))
                                        {
                                            var install = _steamInstalls[g.SteamAppId];
                                            var sa = StreamingAssetsResolver.TryResolve(install);
                                            var final = StreamingAssetsResolver.AppendSubfolder(sa, STREAMING_SUBPATH);
                                            OpenBuildOutputFolder(final);
                                        }
                                    }
                                }
                            }
                        }
                    }
                }

            }
        }

        private void DrawCreateSection()
        {
            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField("Create Pack Data", EditorStyles.boldLabel);

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
                //EditorGUILayout.LabelField("New Pack Data", GUILayout.Width(110));
                _newPackName =
                    EditorGUILayout.TextField(_newPackName, GUILayout.MinWidth(250), GUILayout.ExpandWidth(true));

                GUI.enabled = !string.IsNullOrWhiteSpace(_newPackName);
                if (GUILayout.Button("Create Pack Data", GUILayout.Width(110)))
                {
                    string safe = SanitizePackName(_newPackName);
                    if (string.IsNullOrWhiteSpace(safe))
                    {
                        EditorUtility.DisplayDialog(
                            "Invalid Name",
                            "Name may contain only letters, digits, and spaces (no punctuation or underscores).",
                            "OK");
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
                            $"An Addressables Group named '{safe}' already exists.", "OK");
                    }
                    else
                    {
                        CreatePackWithName(safe);
                    }


                }

                GUI.enabled = true;
            }
        }

        void EnsureWrapStyle()
        {
            if (_wrapLabel != null) return;
            _wrapLabel = new GUIStyle(EditorStyles.label)
            {
                wordWrap = true,
                richText = false
            };
        }

        /// Draw a wrapped line that adapts to the current window width.
        /// leftPadding lets you indent bullets under the header nicely.
        void DrawWrappedLine(string text, float leftPadding = 16f, float rightPadding = 8f)
        {
            EnsureWrapStyle();
            if (string.IsNullOrEmpty(text)) text = "";

            // How much width we actually have inside the current view/helpbox
            float full = EditorGUIUtility.currentViewWidth;
            // Unity adds margins; a small cushion prevents accidental clipping
            float contentWidth = Mathf.Max(80f, full - leftPadding - rightPadding - 20f);

            var gc = new GUIContent(text);
            float h = _wrapLabel.CalcHeight(gc, contentWidth);

            // Reserve a rect and draw
            var r = GUILayoutUtility.GetRect(contentWidth, h, _wrapLabel, GUILayout.ExpandWidth(true));
            r.x += leftPadding;
            r.width = contentWidth;
            EditorGUI.LabelField(r, gc, _wrapLabel);
        }

        // Scroll position for the Part Hierarchy Rules panel
        private Vector2 _rulesHierarchyScroll;


// Subsection foldouts
        private bool _allowedPairsFoldout = true;
        private bool _colorsFoldout = true;

        private void DrawRulesOverview()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _rulesFoldout = EditorGUILayout.Foldout(_rulesFoldout, "Validation Rules", true);
                if (!_rulesFoldout) return;

                if (_rules == null)
                {
                    EditorGUILayout.HelpBox(
                        "No ContentValidationRules asset found. Create one via Tools → MashBox → Create Prefilled Validation Rules, or Assets → Create → Content → Validation Rules.",
                        MessageType.Warning);
                    return;
                }

                // --- SuperTypes ---
                EditorGUILayout.LabelField("SuperTypes", _miniHeader);
                if (_rules.SuperTypes != null && _rules.SuperTypes.Length > 0)
                    EditorGUILayout.LabelField(string.Join(", ", _rules.SuperTypes));
                else
                    EditorGUILayout.LabelField("<none>");

                EditorGUILayout.Space(3);

                // --- Allowed Pairs (foldout, expands naturally) ---
                _allowedPairsFoldout =
                    EditorGUILayout.Foldout(_allowedPairsFoldout, "Allowed Pairs (SuperType → Types)", true);
                if (_allowedPairsFoldout)
                {
                    if (_rules.AllowedPairs != null && _rules.AllowedPairs.Count > 0)
                    {
                        foreach (var pair in _rules.AllowedPairs)
                        {
                            if (pair == null) continue;
                            var types = (pair.Types != null && pair.Types.Length > 0)
                                ? string.Join(", ", pair.Types)
                                : "<none>";
                            DrawWrappedLine($"• {pair.SuperType} → {types}", leftPadding: 16f);
                        }
                    }
                    else
                    {
                        DrawWrappedLine("<none>", leftPadding: 16f);
                    }
                }

                EditorGUILayout.Space(3);

                // --- Colors (foldout, expands naturally) ---
                _colorsFoldout = EditorGUILayout.Foldout(_colorsFoldout, "Colors", true);
                if (_colorsFoldout)
                {
                    if (_rules.Colors != null && _rules.Colors.Length > 0)
                        EditorGUILayout.LabelField(string.Join(", ", _rules.Colors));
                    else
                        EditorGUILayout.LabelField("<none>");
                }

                EditorGUILayout.Space(3);

                // --- Part Hierarchy Rules (NO inner scroll; expands with content) ---
                EditorGUILayout.LabelField("Part Hierarchy Rules", _miniHeader);

                if (_rules.AnchorRules != null && _rules.AnchorRules.Count > 0)
                {
                    var grouped = _rules.AnchorRules
                        .Where(r => r != null)
                        .GroupBy(r => string.IsNullOrEmpty(r.AppliesToSuperType) ? "*" : r.AppliesToSuperType)
                        .OrderBy(g => g.Key);

                    foreach (var g in grouped)
                    {
                        var superKey = g.Key;
                        var foldKey = $"rules.super.{superKey}";
                        if (!_rulesSuperFoldouts.ContainsKey(foldKey))
                            _rulesSuperFoldouts[foldKey] = false;

                        _rulesSuperFoldouts[foldKey] = EditorGUILayout.Foldout(
                            _rulesSuperFoldouts[foldKey],
                            $"{superKey}  ({g.Count()} rule{(g.Count() == 1 ? "" : "s")})",
                            true);

                        if (!_rulesSuperFoldouts[foldKey]) continue;

                        var byType = g.GroupBy(r => string.IsNullOrEmpty(r.AppliesToType) ? "*" : r.AppliesToType)
                            .OrderBy(x => x.Key);

                        foreach (var typeGroup in byType)
                        {
                            var typeKey = typeGroup.Key;
                            var typeFoldKey = $"{foldKey}.type.{typeKey}";
                            if (!_rulesRuleFoldouts.ContainsKey(typeFoldKey))
                                _rulesRuleFoldouts[typeFoldKey] = true;

                            if (!_rulesRuleFoldouts[typeFoldKey]) continue;

                            EditorGUI.indentLevel++;
                            foreach (var r in typeGroup.OrderBy(r => r.AppliesToBrand))
                            {
                                string brandTok = string.IsNullOrEmpty(r.AppliesToBrand) ? "*" : r.AppliesToBrand;
                                string ruleFoldKey = $"{typeFoldKey}.brand.{brandTok}";
                                if (!_rulesRuleFoldouts.ContainsKey(ruleFoldKey))
                                    _rulesRuleFoldouts[ruleFoldKey] = false;

                                using (new EditorGUILayout.HorizontalScope())
                                {
                                    _rulesRuleFoldouts[ruleFoldKey] = EditorGUILayout.Foldout(
                                        _rulesRuleFoldouts[ruleFoldKey],
                                        $"[{superKey}_{typeKey}]",
                                        true);
                                }

                                if (!_rulesRuleFoldouts[ruleFoldKey]) continue;

                                EditorGUI.indentLevel++;
                                var tree = BuildRuleTree(r);
                                bool hasAny =
                                    (r.RequiredChildren != null && r.RequiredChildren.Length > 0) ||
                                    (r.RequiredPatterns != null && r.RequiredPatterns.Length > 0);

                                if (!hasAny)
                                {
                                    EditorGUILayout.LabelField("└─ <none>", EditorStyles.miniLabel);
                                }
                                else
                                {
                                    DrawRuleTree(tree);
                                }

                                EditorGUI.indentLevel--;
                            }

                            EditorGUI.indentLevel--;
                        }
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("<none>");
                }
            }

            EditorGUI.indentLevel = 0;
        }



// Remember per-rule foldouts (SuperType/Type/Brand)
        private readonly Dictionary<string, bool> _rulesRuleFoldouts = new Dictionary<string, bool>();

// Simple tree for showing RequiredChildren & Pattern scopes
        private class RuleTreeNode
        {
            public string Name;

            public SortedDictionary<string, RuleTreeNode> Children =
                new SortedDictionary<string, RuleTreeNode>(StringComparer.Ordinal);

            public List<string> Annotations = new List<string>(); // e.g., pattern summaries at this node
            public bool IsLeaf;

            public RuleTreeNode(string name)
            {
                Name = name;
            }

            public RuleTreeNode GetOrAdd(string key)
            {
                if (!Children.TryGetValue(key, out var n))
                    Children[key] = n = new RuleTreeNode(key);
                return n;
            }
        }

        // Build a tree from one rule's RequiredChildren + RequiredPatterns
        private RuleTreeNode BuildRuleTree(ContentValidationRules.AnchorRule r)
        {
            var root = new RuleTreeNode("<root>");

            // Exact children (root-relative paths)
            if (r.RequiredChildren != null)
            {
                foreach (var path in r.RequiredChildren)
                {
                    if (string.IsNullOrWhiteSpace(path)) continue;
                    var parts = path.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                    var cur = root;
                    for (int i = 0; i < parts.Length; i++)
                    {
                        cur = cur.GetOrAdd(parts[i]);
                        if (i == parts.Length - 1) cur.IsLeaf = true;
                    }
                }
            }

            // Pattern summaries grouped by PathPrefix
            if (r.RequiredPatterns != null)
            {
                foreach (var p in r.RequiredPatterns)
                {
                    if (p == null || string.IsNullOrEmpty(p.NameRegex)) continue;

                    var summary =
                        $"/{p.NameRegex}/ x{(p.Min == p.Max ? p.Min.ToString() : $"{p.Min}..{(p.Max == int.MaxValue ? "∞" : p.Max.ToString())}")} {(p.DirectChildrenOnly ? "[direct]" : "[deep]")}";

                    if (string.IsNullOrEmpty(p.PathPrefix))
                    {
                        // annotate root
                        root.Annotations.Add(summary);
                    }
                    else
                    {
                        var parts = p.PathPrefix.Split(new[] { '/' }, StringSplitOptions.RemoveEmptyEntries);
                        var cur = root;
                        foreach (var seg in parts) cur = cur.GetOrAdd(seg);
                        cur.Annotations.Add(summary);
                    }
                }
            }

            return root;
        }

// Draw the tree nicely with branch marks
        private void DrawRuleTree(RuleTreeNode node, int indent = 0, bool isRoot = true)
        {
            // Show pattern annotations at this node (above its children)
            if (!isRoot && (node.Annotations.Count > 0 || node.IsLeaf))
            {
                // line for the node itself
                DrawTreeLine(node.Name, indent, isLast: false);
            }

            // If this node has annotations, render them beneath it
            if (node.Annotations.Count > 0)
            {
                foreach (var a in node.Annotations)
                    DrawTreeLine($"(pattern) {a}", indent + (isRoot ? 0 : 1), isLast: false, muted: true);
            }

            // Children
            var kids = node.Children.Values.ToList();
            for (int i = 0; i < kids.Count; i++)
            {
                var child = kids[i];
                bool last = (i == kids.Count - 1);

                // Render the child label with branch lines
                string label = child.Name;
                DrawTreeLine(label, indent + (isRoot ? 0 : 1), last);

                // Recurse — draw annotations and grandchildren under this child
                if (child.Annotations.Count > 0 || child.Children.Count > 0)
                {
                    // For grandchildren, increase indent
                    DrawRuleTreeChildren(child, indent + (isRoot ? 0 : 1), last);
                }
            }
        }

// Helper to draw grandchildren with proper connector lines
        private void DrawRuleTreeChildren(RuleTreeNode node, int indent, bool parentLast)
        {
            // annotations
            foreach (var a in node.Annotations)
                DrawTreeLine($"(pattern) {a}", indent + 1, isLast: false, muted: true);

            // grandchildren
            var kids = node.Children.Values.ToList();
            for (int i = 0; i < kids.Count; i++)
            {
                var child = kids[i];
                bool last = (i == kids.Count - 1);

                DrawTreeLine(child.Name, indent + 1, last);
                if (child.Annotations.Count > 0 || child.Children.Count > 0)
                    DrawRuleTreeChildren(child, indent + 1, last);
            }
        }

// Draw a single line with tree glyphs and indentation.
// We keep this simple and robust in IMGUI.
        private void DrawTreeLine(string text, int indent, bool isLast, bool muted = false)
        {
            // Build prefix like "│  " / "├─ " / "└─ "
            string prefix = "";
            if (indent > 0)
            {
                // The last-level connector:
                prefix = (isLast ? "└─ " : "├─ ");
                // Add padding for previous levels
                prefix = new string(' ', (indent - 1) * 2) + prefix;
            }

            var style = muted ? EditorStyles.miniLabel : EditorStyles.label;
            EditorGUILayout.LabelField(prefix + text, style);
        }


        private void DrawPacksList()
        {
            //using (var scroll = new EditorGUILayout.ScrollViewScope(_scroll))
            {
                //_scroll = scroll.scrollPosition;

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


                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            _foldouts[key] = EditorGUILayout.Foldout(_foldouts[key], p.name, true);
                            GUILayout.FlexibleSpace();

                            if (GUILayout.Button("Rename", GUILayout.Width(90)))
                            {
                                // Inside the Rename button callback, replace the validation with:
                                RenameDialog.Show(
                                    "Rename Content Pack",
                                    $"Enter a new name for '{p.name}':",
                                    p.name,
                                    // REPLACE the rename callback body with this stricter flow:
                                    (newName) =>
                                    {
                                        var safe = SanitizePackName(newName);
                                        if (string.IsNullOrWhiteSpace(safe) || safe == p.name) return;

                                        if (PackNameExists(safe, out var existingPath))
                                        {
                                            EditorUtility.DisplayDialog("Duplicate Name",
                                                $"A ContentPackDefinition named '{safe}' already exists:\n{existingPath}",
                                                "OK");
                                            return;
                                        }

                                        if (AddressablesGroupExists(safe))
                                        {
                                            EditorUtility.DisplayDialog("Duplicate Group",
                                                $"An Addressables Group named '{safe}' already exists.", "OK");
                                            return;
                                        }

                                        string assetPath = AssetDatabase.GetAssetPath(p);
                                        AssetDatabase.RenameAsset(assetPath, safe);
                                        AssetDatabase.SaveAssets();
                                        Debug.Log($"[ContentPackBuilder] Renamed pack from '{p.name}' to '{safe}'");
                                    }

                                );
                            }



// Build this single pack
                            if (GUILayout.Button("Build To " + _currentGameName, GUILayout.Width(120)))
                            {
                                // Validate this pack before building
                                var issues = ValidatePack(p, _rules);
                                if (issues.Any(i => i.severity == ContentPackValidator.Severity.Error))
                                {
                                    ContentPackValidator.LogReport(p, issues, "Build blocked");
                                    EditorUtility.DisplayDialog("Build blocked",
                                        $"'{p.name}' has validation errors. See Console.", "OK");
                                }
                                else
                                {
                                    BuildPacks(new List<ContentPackDefinition> { p }, cleanMissing: true);
                                }
                            }

// --- Show "Publish to Mod.io" only if authorized ---
                            if (ContentTools.ModIo.ModIoAuth.IsAuthorizedForCurrentGame())
                            {
                                string currentGame = EditorPrefs.GetString("ModIo.CurrentGame", "Unknown");

                                if (GUILayout.Button($"Publish to {currentGame} Mod.io", GUILayout.Width(190)))
                                {
                                    // 🧩 Validation: must have content
                                    if (p._items == null || p._items.Count == 0)
                                    {
                                        EditorUtility.DisplayDialog(
                                            "Empty Pack",
                                            $"This content pack '{p.name}' is empty.\n\n" +
                                            "You must add content before publishing to Mod.io.",
                                            "OK"
                                        );
                                        Debug.LogWarning(
                                            $"[ContentPackBuilder] Attempted to publish empty pack '{p.name}'.");
                                        return;
                                    }

                                    // 🧩 Validation: must have summary
                                    if (string.IsNullOrWhiteSpace(p.summary))
                                    {
                                        EditorUtility.DisplayDialog(
                                            "Missing Summary",
                                            $"This content pack '{p.name}' does not have a summary.\n\n" +
                                            "A summary is required before publishing to Mod.io.",
                                            "OK"
                                        );
                                        Debug.LogWarning($"[ContentPackBuilder] Missing summary for '{p.name}'.");
                                        return;
                                    }

                                    // 🧩 Validation: must have main screenshot (1920x1080)
                                    if (p.mainScreenshot == null)
                                    {
                                        EditorUtility.DisplayDialog(
                                            "Missing Screenshot",
                                            $"This content pack '{p.name}' does not have a main screenshot.\n\n" +
                                            "A 1920×1080 image is required before publishing to Mod.io.",
                                            "OK"
                                        );
                                        Debug.LogWarning($"[ContentPackBuilder] Missing screenshot for '{p.name}'.");
                                        return;
                                    }

                                    string path = AssetDatabase.GetAssetPath(p.mainScreenshot);
                                    var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                                    if (tex == null || tex.width != 1920 || tex.height != 1080)
                                    {
                                        EditorUtility.DisplayDialog(
                                            "Invalid Screenshot Size",
                                            $"Screenshot for '{p.name}' must be exactly 1920×1080.\n\n" +
                                            $"Current size: {(tex != null ? $"{tex.width}×{tex.height}" : "Unknown")}",
                                            "OK"
                                        );
                                        Debug.LogWarning(
                                            $"[ContentPackBuilder] Invalid screenshot size for '{p.name}'.");
                                        return;
                                    }

                                    // 🧠 Confirmation popup
                                    bool confirm = EditorUtility.DisplayDialog(
                                        "Confirm Mod.io Publish",
                                        $"Are you sure you want to publish this pack to {currentGame} Mod.io?\n\n" +
                                        "This will attach your Mod.io user token to the pack definition.",
                                        "Yes, Publish",
                                        "Cancel"
                                    );

                                    if (confirm)
                                    {
                                        // 1) Persist token on the pack (as you already did)
                                        string token = ContentTools.ModIo.ModIoAuth.CurrentToken;
                                        Undo.RecordObject(p, "Set Mod.io User Token");
                                        p.modioUserToken = token;
                                        p.gameName = currentGame;
                                        EditorUtility.SetDirty(p);
                                        AssetDatabase.SaveAssets();

                                        // 2) Build + upload
                                        try
                                        {
                                            PublishToModioAsync(p, currentGame);
                                        }
                                        catch (System.Exception ex)
                                        {
                                            EditorUtility.ClearProgressBar();
                                            Debug.LogError($"[ContentPackBuilder] Publish failed: {ex.Message}");
                                            EditorUtility.DisplayDialog("Publish Failed", ex.Message, "OK");
                                        }
                                    }

                                    else
                                    {
                                        Debug.Log(
                                            $"[ContentPackBuilder] Publish to Mod.io for '{p.name}' cancelled by user.");
                                    }
                                }

                            }
                            else
                            {
                                GUILayout.Label("🔒 Not authorized with Mod.io", GUILayout.Width(190));
                            }



// Ping the pack asset in the Project window
                            if (GUILayout.Button("PING", GUILayout.Width(70)))
                            {
                                EditorGUIUtility.PingObject(p);
                                Selection.activeObject = p;
                            }

// Only show "Generate Icons" if the pack has any items
                            bool hasItems = p._items != null && p._items.Any(x => x != null);
                            if (hasItems)
                            {
                                if (GUILayout.Button("Generate Icons", GUILayout.Width(120)))
                                    GenerateIconsForPack(p);
                            }

                            if (GUILayout.Button("Delete", GUILayout.Width(70)))
                                DeletePack(p);

                        }

                        GUILayout.Space(20);

                        if (_foldouts.ContainsKey(key))
                            if (_foldouts[key])
                            {

                                EditorGUI.indentLevel++;

                                // === Pack Metadata foldout ===
                                string metaKey = p.name + "_meta";
                                if (!_metaFoldouts.ContainsKey(metaKey))
                                    _metaFoldouts[metaKey] = false;

                                _metaFoldouts[metaKey] =
                                    EditorGUILayout.Foldout(_metaFoldouts[metaKey], "Pack Metadata", true);

                                if (_metaFoldouts[metaKey])
                                {
                                    using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                                    {
                                        EditorGUI.indentLevel++;

                                        // Summary
                                        EditorGUI.BeginChangeCheck();
                                        EditorGUILayout.LabelField("Summary");
                                        string newSummary =
                                            EditorGUILayout.TextArea(p.summary, GUILayout.MinHeight(40));
                                        if (EditorGUI.EndChangeCheck())
                                        {
                                            Undo.RecordObject(p, "Edit Pack Summary");
                                            p.summary = newSummary;
                                            EditorUtility.SetDirty(p);
                                        }

                                        // Screenshot
                                        EditorGUI.BeginChangeCheck();
// === Screenshot Preview ===
                                        EditorGUILayout.LabelField("Main Screenshot (1920x1080)",
                                            EditorStyles.boldLabel);

                                        Rect previewRect =
                                            GUILayoutUtility.GetRect(200, 120, GUILayout.ExpandWidth(true));
                                        GUIStyle boxStyle = new GUIStyle(GUI.skin.box)
                                        {
                                            alignment = TextAnchor.MiddleCenter,
                                            normal = { textColor = Color.gray }
                                        };

// Handle image assignment
                                        if (p.mainScreenshot == null)
                                        {
                                            GUI.Box(previewRect, "Drop Image Here\n(1920×1080 required)", boxStyle);
                                            var dropEvt = Event.current;
                                            if (dropEvt.type == EventType.DragUpdated ||
                                                dropEvt.type == EventType.DragPerform)
                                            {
                                                if (previewRect.Contains(dropEvt.mousePosition))
                                                {
                                                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;

                                                    if (dropEvt.type == EventType.DragPerform)
                                                    {
                                                        DragAndDrop.AcceptDrag();
                                                        foreach (var obj in DragAndDrop.objectReferences)
                                                        {
                                                            if (obj is Texture2D tex)
                                                            {
                                                                Undo.RecordObject(p, "Assign Main Screenshot");
                                                                p.mainScreenshot = tex;
                                                                EditorUtility.SetDirty(p);
                                                                break;
                                                            }
                                                        }
                                                    }

                                                    dropEvt.Use();
                                                }
                                            }
                                        }
                                        else
                                        {
                                            // Draw the image with proper aspect
                                            Texture2D tex = p.mainScreenshot;
                                            Rect imgRect = new Rect(previewRect.x + 4, previewRect.y + 4,
                                                previewRect.width - 8, previewRect.height - 8);
                                            GUI.DrawTexture(imgRect, tex, ScaleMode.ScaleToFit);
                                            GUI.Box(previewRect, GUIContent.none);

                                            // Dimension validation
                                            if (tex.width != 1920 || tex.height != 1080)
                                            {
                                                EditorGUILayout.HelpBox(
                                                    $"⚠️ Image is {tex.width}×{tex.height}. Required size is 1920×1080.",
                                                    MessageType.Warning
                                                );
                                            }

                                            // Allow reassigning / clearing
                                            using (new GUILayout.HorizontalScope())
                                            {
                                                if (GUILayout.Button("Change Screenshot", GUILayout.Width(150)))
                                                {
                                                    var newTex =
                                                        (Texture2D)EditorGUILayout.ObjectField(p.mainScreenshot,
                                                            typeof(Texture2D), false);
                                                    if (newTex != null && newTex != p.mainScreenshot)
                                                    {
                                                        Undo.RecordObject(p, "Change Main Screenshot");
                                                        p.mainScreenshot = newTex;
                                                        EditorUtility.SetDirty(p);
                                                    }
                                                }

                                                if (GUILayout.Button("Clear", GUILayout.Width(70)))
                                                {
                                                    Undo.RecordObject(p, "Clear Main Screenshot");
                                                    p.mainScreenshot = null;
                                                    EditorUtility.SetDirty(p);
                                                }
                                            }
                                        }

                                        //if (EditorGUI.EndChangeCheck())
                                        //{
                                        //    Undo.RecordObject(p, "Change Main Screenshot");
                                        //    p.mainScreenshot = newShot;
                                        //    EditorUtility.SetDirty(p);
                                        //}

                                        // Validate image dimensions
                                        if (p.mainScreenshot != null)
                                        {
                                            string path = AssetDatabase.GetAssetPath(p.mainScreenshot);
                                            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(path);
                                            if (tex != null && (tex.width != 1920 || tex.height != 1080))
                                            {
                                                EditorGUILayout.HelpBox(
                                                    $"⚠️ Image is {tex.width}×{tex.height}. Required size is 1920×1080.",
                                                    MessageType.Warning
                                                );
                                            }
                                        }

                                        EditorGUI.indentLevel--;
                                    }
                                }




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
                                            //var next = (GameObject)EditorGUILayout.ObjectField(go, typeof(GameObject), false);
                                            var next = DrawItemWithIconField(ref go, 40f);
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
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("Refresh", GUILayout.Width(90)))
                    {
                        RefreshPacks();
                    }
                }
            }
        }

        private async void PublishToModioAsync(ContentPackDefinition p, string currentGame)
        {
            try
            {
                EditorUtility.DisplayProgressBar("Publish to Mod.io", "Exporting unitypackage…", 0.25f);
                var packagePath = BuildUnityPackageForPack(p);

                EditorUtility.DisplayProgressBar("Publish to Mod.io", "Requesting upload URL…", 0.45f);
                var (jobId, uploadUrl) = await RequestUploadUrlAsync(Path.GetFileName(packagePath)); // NEW

                // Upload with progress
                var progress = new Progress<float>(f =>
                {
                    // clamp + a little range; show 45–95% during upload
                    var pct = Mathf.Clamp01(f);
                    EditorUtility.DisplayProgressBar("Publish to Mod.io",
                        $"Uploading… {(int)(pct * 100f)}%", 0.45f + pct * 0.50f);
                });

                await UploadFileToSasAsync(packagePath, uploadUrl, progress); // NEW

                EditorUtility.DisplayProgressBar("Publish to Mod.io", "Finalizing…", 0.98f);
                EditorUtility.ClearProgressBar();

                EditorUtility.DisplayDialog(
                    "Publish Complete",
                    $"✅ Your pack '{p.name}' was uploaded successfully to {currentGame}.\n\n" +
                    "Your mod will be set as *hidden*.\n\n" +
                    "Please log in to your mod.io account, find your mod, and toggle it to **Public** when you’re ready to release.",
                    "OK"
                );
                Debug.Log($"[ContentPackBuilder] Uploaded unitypackage: {packagePath} (hidden by default — user must make it public)");
            }
            catch (Exception ex)
            {
                EditorUtility.ClearProgressBar();
                Debug.LogError($"[ContentPackBuilder] Publish failed: {ex.Message}");
                EditorUtility.DisplayDialog("Publish Failed", ex.Message, "OK");
            }
        }
        

private static async Task<(string jobId, string uploadUrl)> RequestUploadUrlAsync(string fileName)
{
    using var http = new HttpClient();
    var form = new FormUrlEncodedContent(new[] { new KeyValuePair<string,string>("fileName", fileName) });
    var req = await http.PostAsync(UploaderEndpoint, form);
    var body = await req.Content.ReadAsStringAsync();
    if (!req.IsSuccessStatusCode)
        throw new Exception($"Proxy request failed: {(int)req.StatusCode} {req.ReasonPhrase}\n{body}");

    var data = JsonUtility.FromJson<UploadResponse>(body);
    if (data == null || string.IsNullOrEmpty(data.uploadUrl))
        throw new Exception("Invalid proxy response (missing uploadUrl).");

    return (data.jobId, data.uploadUrl);
}

// Content that reports progress while HttpClient streams it
private sealed class ProgressStreamContent : HttpContent
{
    private readonly Stream _source;
    private readonly int _bufferSize;
    private readonly IProgress<float> _progress; // 0..1
    private readonly long _contentLength;

    public ProgressStreamContent(Stream source, int bufferSize, IProgress<float> progress)
    {
        _source = source ?? throw new ArgumentNullException(nameof(source));
        _bufferSize = Mathf.Max(8 * 1024, bufferSize);
        _progress = progress;
        _contentLength = source.CanSeek ? source.Length : -1;
        if (_contentLength >= 0) Headers.ContentLength = _contentLength;
        Headers.Add("x-ms-blob-type", "BlockBlob"); // Azure Blob requirement for simple PUT
    }

    protected override async Task SerializeToStreamAsync(Stream target, TransportContext context)
    {
        var buffer = new byte[_bufferSize];
        long total = 0;
        int read;
        while ((read = await _source.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            await target.WriteAsync(buffer, 0, read);
            total += read;
            if (_contentLength > 0) _progress?.Report((float)total / _contentLength);
        }

        _progress?.Report(1f);
    }

    protected override bool TryComputeLength(out long length)
    {
        if (_contentLength >= 0) { length = _contentLength; return true; }
        length = 0;
        return false;
    }
}

private static async Task UploadFileToSasAsync(string filePath, string uploadUrl, IProgress<float> progress)
{
    using var http = new HttpClient();
    using var fs = File.OpenRead(filePath);

    using var content = new ProgressStreamContent(fs, 128 * 1024, progress);
    using var request = new HttpRequestMessage(HttpMethod.Put, uploadUrl) { Content = content };

    // (optional, but helps huge files) start sending without buffering the whole response
    var res = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
    if (!res.IsSuccessStatusCode)
        throw new Exception($"Upload failed: {(int)res.StatusCode} {res.ReasonPhrase}");
}




        private bool _modioFoldout = true;
        private string _emailInput = "";
        private string _codeInput = "";
        private string _statusMsg = "";
        private Texture2D _modioLogo;

        void DrawModIoSection()
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                _modioFoldout = EditorGUILayout.Foldout(_modioFoldout, "mod.io Login", true);
                if (!_modioFoldout) return;

                if (_modioLogo == null) _modioLogo = Resources.Load<Texture2D>("modio_Logo");

                using (new EditorGUILayout.HorizontalScope())
                {
                    var logoWidth = 86f;
                    var logoHeight = 58f;
                    GUILayout.Space(4);
                    var rect = GUILayoutUtility.GetRect(logoWidth, logoHeight, GUILayout.Width(logoWidth),
                        GUILayout.Height(logoHeight));
                    if (_modioLogo != null) GUI.DrawTexture(rect, _modioLogo, ScaleMode.ScaleToFit, true);

                    GUILayout.Space(5);

                    using (new EditorGUILayout.VerticalScope())
                    {

                        if (!ContentTools.ModIo.ModIoAuth.IsAuthorizedForCurrentGame())
                        {
                            // Not authorized for this game: prompt for email/code
                            _emailInput = EditorGUILayout.TextField("Email", _emailInput);
                            _codeInput = EditorGUILayout.TextField("Code", _codeInput);

                            using (new EditorGUILayout.HorizontalScope())
                            {
                                if (GUILayout.Button("Send Code"))
                                    ContentTools.ModIo.ModIoAuth.BeginEmailRequest(_emailInput, s =>
                                    {
                                        _statusMsg = s;
                                        Debug.Log(_statusMsg);
                                        Repaint();
                                    });

                                if (GUILayout.Button("Verify"))
                                    ContentTools.ModIo.ModIoAuth.ExchangeCode(_emailInput, _codeInput, s =>
                                    {
                                        _statusMsg = s;
                                        Debug.Log(_statusMsg);
                                        Repaint();
                                    });
                            }
                        }
                        else
                        {
                            // Already authorized for this game's API base
                            EditorGUILayout.LabelField("✅ Authorized as:", ContentTools.ModIo.ModIoAuth.CurrentEmail);
                            using (new EditorGUILayout.HorizontalScope())
                            {
                                var currentGame = EditorPrefs.GetString("ModIo.CurrentGame", "this game");
                                if (GUILayout.Button($"Logout ({currentGame})", GUILayout.Width(150)))
                                {
                                    ContentTools.ModIo.ModIoAuth.ClearForCurrentGame();
                                    _statusMsg = $"Logged out for {currentGame}.";
                                    Debug.Log(_statusMsg);
                                    Repaint();
                                }
                            }
                        }

                        if (!string.IsNullOrEmpty(_statusMsg))
                        {
                            GUILayout.Space(4);
                            EditorGUILayout.HelpBox(_statusMsg, MessageType.None);
                        }
                    }
                }

                GUILayout.Space(4);
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
                    Debug.LogError(
                        $"[ContentPackBuilder] Icon registration failed for '{p?.name}': {exCollect.Message}");
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

            // Define colored styles for check and x marks
            GUIStyle greenStyle = new GUIStyle(EditorStyles.miniBoldLabel);
            greenStyle.normal.textColor = Color.green;

            GUIStyle redStyle = new GUIStyle(EditorStyles.miniBoldLabel);
            redStyle.normal.textColor = Color.red;

            if (errorCount == 0 && warnCount == 0 && onlyValid)
            {
                GUILayout.Label("✓ Valid", greenStyle, GUILayout.Width(60));
                return;
            }
            else if (onlyValid)
            {
                GUILayout.Label("✗ Invalid", redStyle, GUILayout.Width(60));
                return;
            }


            using (new EditorGUILayout.HorizontalScope())
            {
                if (errorCount == 0 && warnCount == 0)
                {
                    // GUILayout.Label("✓ Valid", greenStyle, GUILayout.Width(60));
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
                    // 🔹 Add indentation here
                    GUILayout.Space(10); // adjust this for how much indent you want

                    foreach (var i in issues)
                    {
                        var style = i.severity == ContentPackValidator.Severity.Error ? _errStyle : _warnStyle;

                        using (new EditorGUILayout.HorizontalScope())
                        {
                            GUILayout.Space(10); // indent bullet and text
                            EditorGUILayout.LabelField("• " + i.message, style);
                        }
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

            // Only find packs inside the forced folder
            string[] guids = AssetDatabase.FindAssets("t:ContentPackDefinition", new[] { FORCED_PACKS_FOLDER });
            foreach (string guid in guids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);

                // 🔒 Safety: ignore anything outside the forced folder
                if (!path.StartsWith(FORCED_PACKS_FOLDER, StringComparison.OrdinalIgnoreCase))
                    continue;

                var pack = AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(path);
                if (pack != null)
                    _packs.Add(pack);
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

// REPLACE the existing SanitizePackName with this:
        private static string SanitizePackName(string raw)
        {
            if (string.IsNullOrWhiteSpace(raw)) return string.Empty;

            var sb = new System.Text.StringBuilder(raw.Length);
            foreach (char c in raw.Trim())
            {
                if (char.IsLetterOrDigit(c) || c == ' ')
                    sb.Append(c);   // allow A–Z a–z 0–9 and space
                // everything else is dropped
            }

            // collapse multiple spaces and trim again
            var s = System.Text.RegularExpressions.Regex.Replace(sb.ToString(), @"\s{2,}", " ").Trim();
            return s;
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

        private void OpenBuildOutputFolder(string path)
        {
            if (string.IsNullOrEmpty(path))
            {
                EditorUtility.DisplayDialog("Open Folder", "Build Output Folder is empty.", "OK");
                return;
            }

            // Ensure it exists so Explorer/Finder opens cleanly
            path = path.Replace("\\", "/");
            if (!Directory.Exists(path))
            {
                try
                {
                    Directory.CreateDirectory(path);
                }
                catch (System.Exception ex)
                {
                    EditorUtility.DisplayDialog("Open Folder", $"Could not create folder:\n{path}\n\n{ex.Message}",
                        "OK");
                    return;
                }
            }

            // Cross-platform open
#if UNITY_EDITOR_WIN
            Process.Start(new ProcessStartInfo("explorer.exe", path.Replace("/", "\\")) { UseShellExecute = true });
#elif UNITY_EDITOR_OSX
    EditorUtility.RevealInFinder(path); // opens Finder at the folder
#else
    EditorUtility.RevealInFinder(path); // Linux/editor support
#endif
        }

// --- Thumbnail cache for icons to keep UI fast ---
        private readonly Dictionary<string, Texture2D> _iconThumbCache = new Dictionary<string, Texture2D>();

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

        private Texture2D GetIconTextureForPrefab(GameObject prefab)
        {
            if (prefab == null) return null;


            string folder;
            var iconPath = ComputeIconPathForPrefab(prefab, out folder);
            if (string.IsNullOrEmpty(iconPath)) return null;

            if (_iconThumbCache.TryGetValue(iconPath, out var cached) && cached != null)
                return cached;

            // Try the generated icon first
            var tex = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);

            // Fall back to Unity's preview if not generated yet
            if (tex == null)
                tex = AssetPreview.GetAssetPreview(prefab) ?? AssetPreview.GetMiniThumbnail(prefab) as Texture2D;

            _iconThumbCache[iconPath] = tex;
            return tex;
        }


        static GUIStyle _wrapLabel;

        private void DrawWrappedHelpBox(string text, float leftPadding = 6f, float rightPadding = 6f,
            float topPadding = 6f, float bottomPadding = 6f)
        {
            if (string.IsNullOrEmpty(text)) return;

            // Container styled like the existing “Validation Rules” box
            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            // Calculate width inside the helpbox
            float fullWidth = EditorGUIUtility.currentViewWidth; // the window’s usable width
            float contentWidth = fullWidth - leftPadding - rightPadding - 20f; // a bit of slack for margins/scrollbars

            // Measure required height for the wrapped text
            var gc = new GUIContent(text);
            float height = _wrapLabel.CalcHeight(gc, contentWidth);

            // Reserve and draw
            var r = GUILayoutUtility.GetRect(contentWidth, height, _wrapLabel, GUILayout.ExpandWidth(true));
            r.x += leftPadding;
            r.width = contentWidth;
            r.y += topPadding;
            r.height = height;

            EditorGUI.LabelField(r, gc, _wrapLabel);

            GUILayout.Space(bottomPadding);
            EditorGUILayout.EndVertical();
        }

        private GameObject DrawItemWithIconField(ref GameObject itemRef, float iconSize = 40f)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                // Reserve a rect for the icon
                var iconRect = GUILayoutUtility.GetRect(iconSize, iconSize, GUILayout.Width(iconSize),
                    GUILayout.Height(iconSize));

                // Figure out which asset we can ping (icon if present, else prefab)
                Texture2D tex = null;
                Object pingTarget = null;

                if (itemRef != null)
                {
                    string folder;
                    var iconPath = ComputeIconPathForPrefab(itemRef, out folder);
                    if (!string.IsNullOrEmpty(iconPath))
                    {
                        tex = AssetDatabase.LoadAssetAtPath<Texture2D>(iconPath);
                        if (tex != null) pingTarget = tex;
                    }

                    if (tex == null)
                    {
                        // fallback preview for display only
                        tex = AssetPreview.GetAssetPreview(itemRef) ??
                              AssetPreview.GetMiniThumbnail(itemRef) as Texture2D;
                        pingTarget = itemRef; // fallback ping target = prefab
                    }
                }

                // Draw subtle background + thumbnail
                if (Event.current.type == EventType.Repaint)
                    EditorGUI.DrawRect(iconRect,
                        EditorGUIUtility.isProSkin ? new Color(1, 1, 1, 0.05f) : new Color(0, 0, 0, 0.06f));
                if (tex != null) GUI.DrawTexture(iconRect, tex, ScaleMode.ScaleToFit);

                // Make it feel clickable
                EditorGUIUtility.AddCursorRect(iconRect, MouseCursor.Link);

                // Click = Ping (left click). Alt+Click also reveals in Finder/Explorer.
                if (GUI.Button(iconRect, GUIContent.none, GUIStyle.none) && pingTarget != null)
                {
                    EditorGUIUtility.PingObject(pingTarget);
                    Selection.activeObject = pingTarget;

                    if (Event.current != null && (Event.current.alt ||
                                                  Event.current.control &&
                                                  Application.platform == RuntimePlatform.OSXEditor))
                    {
                        var path = AssetDatabase.GetAssetPath(pingTarget);
                        if (!string.IsNullOrEmpty(path) && File.Exists(path))
                            EditorUtility.RevealInFinder(path);
                    }
                }

                // The object field
                itemRef = (GameObject)EditorGUILayout.ObjectField(itemRef, typeof(GameObject), false);
            }

            return itemRef;
        }


        private void ClearIconThumbsForPack(ContentPackDefinition pack)
        {
            if (pack == null || pack._items == null) return;

            foreach (var go in pack._items)
            {
                if (!go) continue;
                string folder;
                var path = ComputeIconPathForPrefab(go, out folder);
                if (string.IsNullOrEmpty(path)) continue;
                _iconThumbCache.Remove(path);
            }
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

            ClearIconThumbsForPack(pack);
            Repaint();

        }


private static string BuildUnityPackageForPack(ContentPackDefinition pack)
{
    if (pack == null) throw new ArgumentNullException(nameof(pack));

    // Make sure latest edits (token/summary/screenshot) are on disk
    AssetDatabase.SaveAssets();

    // 1) Seed roots with the pack definition itself
    var roots = new List<string>();
    var defPath = AssetDatabase.GetAssetPath(pack);   // <-- the .asset file
    if (!string.IsNullOrEmpty(defPath)) roots.Add(defPath);

    // Items
    if (pack._items != null)
    {
        foreach (var obj in pack._items)
        {
            if (!obj) continue;
            var p = AssetDatabase.GetAssetPath(obj);
            if (!string.IsNullOrEmpty(p)) roots.Add(p);
        }
    }

    // Main screenshot
    if (pack.mainScreenshot)
    {
        var p = AssetDatabase.GetAssetPath(pack.mainScreenshot);
        if (!string.IsNullOrEmpty(p)) roots.Add(p);
    }

    // Icons (if you have a list)
    TryAddTextureListField(pack, "icons", roots);
    TryAddTextureListField(pack, "_icons", roots);
    TryAddTextureListField(pack, "iconTextures", roots);
    TryAddTextureListField(pack, "additionalImages", roots);

    // 2) Expand with dependencies (keep content-only)
    var toExport = AssetDatabase.GetDependencies(roots.Distinct().ToArray(), true)
        .Where(p => p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
        .Where(p => !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
        .Where(p => !p.Contains("/Editor/"))
        .Distinct()
        .ToList();

    if (toExport.Count == 0)
        throw new Exception("Nothing to export: no assets/screenshot/icons/definition were found.");

    // 3) Export
    var outPath = GetTempUnityPackagePath(pack.name);
    EditorUtility.DisplayProgressBar("Exporting Package",
        $"Creating {Path.GetFileName(outPath)} ({toExport.Count} assets)…", 0.5f);

    AssetDatabase.ExportPackage(toExport.ToArray(), outPath, ExportPackageOptions.Default);
    AssetDatabase.Refresh();

    if (!File.Exists(outPath))
        throw new FileNotFoundException("Export failed (file not created).", outPath);

    EditorUtility.ClearProgressBar();
    return outPath;
}


// Helper: add Texture2D list field by name if it exists on the pack (reflection)
private static void TryAddTextureListField(ContentPackDefinition pack, string fieldName, List<string> roots)
{
    var fi = typeof(ContentPackDefinition).GetField(fieldName,
        System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
    if (fi == null) return;

    var val = fi.GetValue(pack);
    if (val is IEnumerable<Texture2D> texList)
    {
        foreach (var t in texList)
        {
            if (!t) continue;
            var p = AssetDatabase.GetAssetPath(t);
            if (!string.IsNullOrEmpty(p)) roots.Add(p);
        }
    }
}

// Temp file location (Project/Temp/RemoteCook_<pack>.unitypackage)
private static string GetTempUnityPackagePath(string packName)
{
    var projRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
    var tempDir = Path.Combine(projRoot, "Temp");
    if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);

    var safe = SanitizeFileName(string.IsNullOrEmpty(packName) ? "ContentPack" : packName);
    return Path.Combine(tempDir, $"RemoteCook_{safe}.unitypackage");
}

private static string SanitizeFileName(string raw)
{
    var bad = Path.GetInvalidFileNameChars();
    return new string(raw.Select(c => bad.Contains(c) ? '_' : c).ToArray());
}

        
        
        private void CleanAddressableData()
        {
            const string GROUPS_PATH = "Assets/AddressableAssetsData/AssetGroups";
            const string SCHEMAS_PATH = "Assets/AddressableAssetsData/AssetGroups/Schemas";

            // Keep these by default
            HashSet<string> protectedBaseNames = new HashSet<string>
            {
                "Built In Data",
                "Default Local Group"
            };

            // Add content pack names to the protected list
            foreach (var pack in _packs)
            {
                if (pack == null) continue;
                protectedBaseNames.Add(pack.name);
            }

            void CleanFolder(string folderPath)
            {
                if (!AssetDatabase.IsValidFolder(folderPath))
                    return;

                string[] files =
                    System.IO.Directory.GetFiles(folderPath, "*.*", System.IO.SearchOption.TopDirectoryOnly);

                foreach (string filePath in files)
                {
                    if (filePath.EndsWith(".meta")) continue;

                    string fileName = System.IO.Path.GetFileNameWithoutExtension(filePath);
                    bool keep = protectedBaseNames.Any(baseName =>
                        fileName.StartsWith(baseName, System.StringComparison.OrdinalIgnoreCase));

                    if (!keep)
                    {
                        Debug.Log($"[Cleanup] Removing stray Addressables asset: {fileName}");
                        AssetDatabase.DeleteAsset(filePath);
                    }
                }
            }

            // Run cleanup on both folders
            CleanFolder(GROUPS_PATH);
            CleanFolder(SCHEMAS_PATH);

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
        }

// Build a unitypackage containing: items + dependencies (+ screenshot + icons)
        private static List<string> CollectExportPaths(ContentPackDefinition pack)
        {
            var roots = new List<string>();
            if (pack?._items != null)
            {
                foreach (var go in pack._items)
                {
                    if (!go) continue;
                    var p = AssetDatabase.GetAssetPath(go);
                    if (!string.IsNullOrEmpty(p)) roots.Add(p);
                }
            }

            // include main screenshot if set
            if (pack.mainScreenshot)
            {
                var p = AssetDatabase.GetAssetPath(pack.mainScreenshot);
                if (!string.IsNullOrEmpty(p)) roots.Add(p);
            }

            // include any generated icons recorded on the pack
            if (pack._icons != null)
            {
                foreach (var t in pack._icons)
                {
                    if (!t) continue;
                    var p = AssetDatabase.GetAssetPath(t);
                    if (!string.IsNullOrEmpty(p)) roots.Add(p);
                }
            }

            // Dependencies
            var deps = AssetDatabase.GetDependencies(roots.ToArray(), true)
                .Where(p => p.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase))
                .Where(p => !p.EndsWith(".cs", StringComparison.OrdinalIgnoreCase)) // exclude scripts
                .Where(p => !p.Contains("/Editor/")) // exclude Editor assets
                .Distinct()
                .ToList();

            return deps;
        }

        private static string ProjectRoot()
            => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        private static string TempPackagePathFor(string packName)
        {
            var tempDir = Path.Combine(ProjectRoot(), "Temp");
            if (!Directory.Exists(tempDir)) Directory.CreateDirectory(tempDir);
            return Path.Combine(tempDir, $"RemoteCook_{SanitizeFile(packName)}.unitypackage");
        }

        private static string SanitizeFile(string raw)
        {
            if (string.IsNullOrEmpty(raw)) return "ContentPack";
            var bad = Path.GetInvalidFileNameChars();
            return new string(raw.Select(c => bad.Contains(c) ? '_' : c).ToArray());
        }

        private static string ExportUnityPackage(ContentPackDefinition pack)
        {
            var export = CollectExportPaths(pack);
            if (export.Count == 0) throw new Exception("No assets to export.");
            var outPath = TempPackagePathFor(pack.name);

            EditorUtility.DisplayProgressBar("Exporting Package",
                $"Creating {Path.GetFileName(outPath)} ({export.Count} items)…", 0.5f);

            AssetDatabase.ExportPackage(export.ToArray(), outPath, ExportPackageOptions.Default);
            AssetDatabase.Refresh();

            if (!File.Exists(outPath)) throw new FileNotFoundException("Export failed", outPath);
            return outPath;
        }

        [Serializable]
        private class UploadResponse { public string jobId; public string uploadUrl; }

        private static async Task UploadUnityPackageAsync(string filePath)
        {
            using (var http = new HttpClient())
            {
                // 1) Ask proxy for an upload URL (pass a name so your function can store nicely)
                var name = Path.GetFileName(filePath);
                var form = new FormUrlEncodedContent(new[]
                {
                    new KeyValuePair<string,string>("fileName", name),
                });

                var req = await http.PostAsync(UploaderEndpoint, form);
                var body = await req.Content.ReadAsStringAsync();
                if (!req.IsSuccessStatusCode)
                    throw new Exception($"Proxy request failed: {(int)req.StatusCode} {req.ReasonPhrase}\n{body}");

                var data = JsonUtility.FromJson<UploadResponse>(body);
                if (data == null || string.IsNullOrEmpty(data.uploadUrl))
                    throw new Exception("Invalid proxy response (missing uploadUrl).");

                // 2) Upload the bytes to the SAS URL
                var bytes = File.ReadAllBytes(filePath);
                using var content = new ByteArrayContent(bytes);
                content.Headers.Add("x-ms-blob-type", "BlockBlob");
                var putRes = await http.PutAsync(data.uploadUrl, content);

                if (!putRes.IsSuccessStatusCode)
                    throw new Exception($"Upload failed: {(int)putRes.StatusCode} {putRes.ReasonPhrase}");
            }
        }

        
        private Texture2D TryLoadSteamLibraryImage(long appId, int maxHeight = 64)
        {
            if (_steamCapsuleCache.TryGetValue(appId, out var cached) && cached != null)
                return cached;

            var root = SteamLocator.GetSteamRoot();
            if (string.IsNullOrEmpty(root)) return null;

            var libCache = System.IO.Path.Combine(root, "librarycache");
            if (!System.IO.Directory.Exists(libCache)) return null;

            // Common filename patterns in librarycache
            var candidates = new List<string>
            {
                System.IO.Path.Combine(libCache, $"app_{appId}_header.jpg"),
                System.IO.Path.Combine(libCache, $"app_{appId}_header.png"),
                System.IO.Path.Combine(libCache, $"app_{appId}.jpg"),
                System.IO.Path.Combine(libCache, $"app_{appId}.png"),
                System.IO.Path.Combine(libCache, $"library_hero_{appId}.jpg"),
                System.IO.Path.Combine(libCache, $"library_hero_{appId}.png"),
                System.IO.Path.Combine(libCache, $"capsule_{appId}.jpg"),
                System.IO.Path.Combine(libCache, $"capsule_{appId}.png"),
            };

            string hit = null;
            foreach (var p in candidates)
            {
                if (System.IO.File.Exists(p))
                {
                    hit = p;
                    break;
                }
            }

            if (hit == null)
            {
                try
                {
                    var files = System.IO.Directory.GetFiles(libCache, "*.*", SearchOption.TopDirectoryOnly);
                    foreach (var f in files)
                    {
                        if (!(f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                              f.EndsWith(".png", StringComparison.OrdinalIgnoreCase)))
                            continue;
                        var name = System.IO.Path.GetFileNameWithoutExtension(f);
                        if (name != null && name.Contains(appId.ToString()))
                        {
                            hit = f;
                            break;
                        }
                    }
                }
                catch
                {
                }
            }

            if (string.IsNullOrEmpty(hit)) return null;

            byte[] bytes = null;
            try
            {
                bytes = System.IO.File.ReadAllBytes(hit);
            }
            catch
            {
            }

            if (bytes == null) return null;

            var tex = new Texture2D(2, 2, TextureFormat.RGBA32, false);
            if (!tex.LoadImage(bytes, markNonReadable: true)) return null;

            _steamCapsuleCache[appId] = tex;
            return tex;
        }


        [MenuItem("Tools/MashBox/Modio/PrintCurrentUserToken")]
        public static void PrintCurrentUserToken()
        {
            Debug.Log(ContentTools.ModIo.ModIoAuth.CurrentToken);
        }
        
    }
}
#endif
