#if UNITY_EDITOR
using System.Linq;
using System.Collections.Generic;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace ContentTools.Editor
{
    public class ContentPackBuilderWindow : EditorWindow
    {
        // ---- EditorPrefs key for persistence ----
        private const string PREF_KEY_BUILD_LOCATION = "ContentPackBuilder.BuildLocation";

        private ContentPackDefinition[] _packs;
        private bool[] _selected;

        private AddressableAssetSettings _settings;
        private int _profileIndex;
        private string[] _profileNames;
        private string[] _profileIds;

        // Single, simple destination for bundles/manifests
        private string _buildLocation = string.Empty; // filesystem folder; tokens: [BuildTarget], {pack} supported by AddressablesPackBuilder

        [MenuItem("Window/Addressables/Content Pack Builder")]
        public static void Open()
        {
            GetWindow<ContentPackBuilderWindow>(true, "Content Pack Builder");
        }

        private void OnEnable()
        {
            _settings = AddressableAssetSettingsDefaultObject.Settings;
            // Load persisted build location
            _buildLocation = EditorPrefs.GetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
            RefreshProfiles();
            RefreshPacks();
        }

        private void OnDisable()
        {
            // Persist current value when window closes / domain reloads
            if (!string.IsNullOrEmpty(_buildLocation))
                EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
            else
                EditorPrefs.DeleteKey(PREF_KEY_BUILD_LOCATION);
        }

        private void RefreshProfiles()
        {
            if (_settings == null) return;
            var ps = _settings.profileSettings;

            // Minimal & robust: use active profile as fallback
            var ids = new List<string>();
            var names = new List<string>();

            // Try active + its name
            var activeId = _settings.activeProfileId;
            var activeName = ps.GetProfileName(activeId);
            if (string.IsNullOrEmpty(activeName)) activeName = "Default";

            ids.Add(activeId);
            names.Add(activeName);

            _profileIds = ids.ToArray();
            _profileNames = names.ToArray();

            _profileIndex = System.Array.IndexOf(_profileIds, _settings.activeProfileId);
            if (_profileIndex < 0) _profileIndex = 0;
        }

        private void RefreshPacks()
        {
            var guids = AssetDatabase.FindAssets("t:ContentPackDefinition");
            _packs = guids.Select(g => AssetDatabase.LoadAssetAtPath<ContentPackDefinition>(AssetDatabase.GUIDToAssetPath(g))).ToArray();
            _selected = new bool[_packs.Length];
            for (int i = 0; i < _selected.Length; i++) _selected[i] = true;
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

            // Profile
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Profile", EditorStyles.boldLabel);
            _profileIndex = EditorGUILayout.Popup("Build Profile", _profileIndex, _profileNames);

            // Build Location (single field)
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Build Location", EditorStyles.boldLabel);
            EditorGUILayout.BeginHorizontal();

            EditorGUI.BeginChangeCheck();
            var newBuildLocation = EditorGUILayout.TextField(
                new GUIContent("Build Location", "Filesystem folder where this tool will emit bundles for the selected packs.\n" +
                                                  "You can use tokens like [BuildTarget] and {pack}."),
                _buildLocation);
            if (EditorGUI.EndChangeCheck())
            {
                _buildLocation = (newBuildLocation ?? string.Empty).Replace("\\", "/");
                // Save on change
                if (!string.IsNullOrEmpty(_buildLocation))
                    EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation);
                else
                    EditorPrefs.DeleteKey(PREF_KEY_BUILD_LOCATION);
            }

            if (GUILayout.Button("Browse", GUILayout.MaxWidth(70)))
            {
                string picked = EditorUtility.OpenFolderPanel("Select Build Location", 
                    string.IsNullOrEmpty(_buildLocation) ? Application.dataPath : _buildLocation, "");
                if (!string.IsNullOrEmpty(picked))
                {
                    _buildLocation = picked.Replace("\\", "/");
                    EditorPrefs.SetString(PREF_KEY_BUILD_LOCATION, _buildLocation); // Save immediately after browse
                }
            }
            EditorGUILayout.EndHorizontal();

            // Packs list
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Packs", EditorStyles.boldLabel);
            if (_packs == null || _packs.Length == 0)
            {
                EditorGUILayout.HelpBox("No ContentPackDefinition assets found. Create one via Create → Addressables → Content Pack.", MessageType.Info);
                if (GUILayout.Button("Refresh")) RefreshPacks();
            }
            else
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll, GUILayout.MinHeight(180));
                for (int i = 0; i < _packs.Length; i++)
                {
                    EditorGUILayout.BeginVertical("box");
                    var p = _packs[i];
                    _selected[i] = EditorGUILayout.ToggleLeft($"Build {p.PackName}", _selected[i]);
                    EditorGUI.indentLevel++;
                    //EditorGUILayout.LabelField("Groups:", string.Join(", ", p.groupNames ?? new string[0]));
                    //EditorGUILayout.LabelField("Subfolder:", string.IsNullOrEmpty(p.remoteSubfolderOverride) ? p.packName : p.remoteSubfolderOverride);
                    if (p.labels != null && p.labels.Length > 0)
                        EditorGUILayout.LabelField("Labels:", string.Join(", ", p.labels));
                    EditorGUI.indentLevel--;
                    EditorGUILayout.EndVertical();
                }
                EditorGUILayout.EndScrollView();
            }

            // Actions
            EditorGUILayout.Space();
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Build Selected")) BuildSelected(false);
            if (GUILayout.Button("Build All")) BuildSelected(true);
            if (GUILayout.Button("Refresh")) { RefreshProfiles(); RefreshPacks(); }
            EditorGUILayout.EndHorizontal();
        }

        private void BuildSelected(bool all)
        {
            if (_profileIds == null || _profileIds.Length == 0)
            {
                Debug.LogError("No Addressables profile available.");
                return;
            }
            if (_profileIndex < 0 || _profileIndex >= _profileIds.Length)
            {
                Debug.LogError("Invalid profile index.");
                return;
            }

            var opts = new AddressablesPackBuilder.BuildOptions
            {
                profileId = _profileIds[_profileIndex],
                rebuildPlayerContent = true,
                enableRemoteCatalog = true,
                disableOtherGroups = true,
                writeManifestJson = true,
                manifestFileName = null,
                setPlayerVersionOverride = true,

                // Single, simple field wired here:
                sessionRemoteBuildRootOverride = string.IsNullOrEmpty(_buildLocation) ? null : _buildLocation,
                sessionRemoteLoadRootOverride = "{UnityEngine.AddressableAssets.Addressables.RuntimePath}",
                //forceLocalPaths = true, // preserved if you re-enable this later
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
    }
}
#endif
