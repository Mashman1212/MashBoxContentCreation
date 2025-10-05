#if UNITY_EDITOR

using System.Collections.Generic;
using System.Reflection;
using System.Linq;
using UnityEditor;
using UnityEditor.AddressableAssets.Settings;
using UnityEditor.AddressableAssets.Settings.GroupSchemas;
using UnityEngine;

namespace ContentTools
{
    [CreateAssetMenu(fileName = "ContentPack", menuName = "Addressables/Content Pack", order = 2000)]
    public class ContentPackDefinition : ScriptableObject
    {
        public string PackName => _packName;
        private string _packName;

        [Header("Content")] [Tooltip("Drop prefabs here to include them in this pack.")]
        public List<GameObject> _items = new List<GameObject>();

        [Header("Icons")]
        [Tooltip("Auto-populated list of icon textures generated for this pack.")]
        public List<Texture2D> _icons = new List<Texture2D>();
        
        string addressablesGroupName;

        private bool autoSyncOnValidate = true;


        // ---------------- Editor-only sync logic ----------------

        // Re-entrancy guard to prevent Validate -> Sync -> Validate loops
        private static bool _syncInProgress;
        private static bool _syncScheduled;

        private static readonly HashSet<string> LabelStopWords =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
                { "bean" }; // ignore "Bean" per your examples; edit as you like
        
        // Detect if this is an Asset Import Worker process. Use reflection to support multiple Unity versions.
        private static bool IsAssetImportWorkerProcess()
        {
            var t = typeof(UnityEditor.AssetDatabase);
            var mi = t.GetMethod("IsAssetImportWorkerProcess",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic);
            if (mi != null)
            {
                try { return (bool)mi.Invoke(null, null); } catch { }
            }

            // Fallback heuristic: treat compile/update as unsafe for AssetDatabase mutations
            if (UnityEditor.EditorApplication.isCompiling || UnityEditor.EditorApplication.isUpdating)
                return true;

            return false;
        }

        private static IEnumerable<string> LabelsFromAssetName(string name)
        {
            if (string.IsNullOrEmpty(name)) yield break;
            var seen = new HashSet<string>(System.StringComparer.OrdinalIgnoreCase);
            var parts = name.Split(new[] { '_' }, System.StringSplitOptions.RemoveEmptyEntries);
            foreach (var raw in parts)
            {
                var tok = raw.Trim();
                if (tok.Length == 0) continue;
                if (LabelStopWords.Contains(tok)) continue;
                if (seen.Add(tok)) yield return tok;
            }
        }

        public void OnValidate()
        {
            _packName = name;


            if (!autoSyncOnValidate) return;
            if (Application.isPlaying) return; // don't mutate Addressables in play mode
            if (_syncInProgress) return; // currently syncing from a previous call

            // Bail out if editor is busy importing/compiling or we're in an import worker
            if (UnityEditor.EditorApplication.isCompiling ||
                UnityEditor.EditorApplication.isUpdating ||
                IsAssetImportWorkerProcess())
                return;

            // Only schedule one sync tick; we'll clear the flag when it runs.
            if (_syncScheduled) return;
            _syncScheduled = true;

            UnityEditor.EditorApplication.delayCall += () =>
            {
                _syncScheduled = false;
                if (this == null) return; // asset deleted
                if (_syncInProgress) return;

                try
                {
                    // Double-check just before running
                    if (UnityEditor.EditorApplication.isCompiling ||
                        UnityEditor.EditorApplication.isUpdating ||
                        IsAssetImportWorkerProcess() ||
                        Application.isPlaying)
                        return;

                    SyncToAddressables();
                }
                catch (System.Exception ex)
                {
                    Debug.LogError($"[{nameof(ContentPackDefinition)}] Auto-sync exception:\n{ex}");
                }
            };

        }

        [ContextMenu("Addressables/Sync Now")]
        public void SyncToAddressables()
        {


            // Never mutate Addressables from import worker / compile / update / playmode
            if (IsAssetImportWorkerProcess() ||
                UnityEditor.EditorApplication.isCompiling ||
                UnityEditor.EditorApplication.isUpdating ||
                Application.isPlaying)
            {
                return;
            }

            _packName = this.name;
            //if (_syncInProgress) return;
            _syncInProgress = true;
            try
            {
                var settings = UnityEditor.AddressableAssets.AddressableAssetSettingsDefaultObject.Settings;
                if (settings == null)
                {
                    Debug.LogError("Addressables settings not found.");
                    return;
                }

                if (string.IsNullOrEmpty(PackName))
                {
                    Debug.LogError("Pack name is empty; cannot sync.");
                    return;
                }

                // Decide which group name to use
                string groupName = PackName;

                // Ensure group exists (with the correct name) and has a BundledAssetGroupSchema
                var group = settings.FindGroup(groupName);
                if (group == null)
                {
                    // If a stray default group was made (e.g. "New Group"), remove it and recreate properly
                    var stray = settings.groups.FirstOrDefault(g => g != null && g.Name == "New Group");
                    if (stray != null && (stray.entries == null || stray.entries.Count == 0))
                        settings.RemoveGroup(stray);

                    group = settings.CreateGroup(
                        groupName,
                        setAsDefaultGroup: false,
                        readOnly: false,
                        postEvent: false,
                        schemasToCopy: null,
                        typeof(UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema)
                    );
                }
                else if (group.Name != groupName)
                {
                    // If we somehow found a group but with the wrong name (e.g., "New Group")
                    // and it has no content yet, recreate it with the correct name.
                    if (group.entries == null || group.entries.Count == 0)
                    {
                        settings.RemoveGroup(group);
                        group = settings.CreateGroup(
                            groupName,
                            setAsDefaultGroup: false,
                            readOnly: false,
                            postEvent: false,
                            schemasToCopy: null,
                            typeof(UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema)
                        );
                    }
                }

                // Ensure schema exists and tweak defaults if needed
                var schema = group.GetSchema<BundledAssetGroupSchema>();
                if (schema == null)
                {
                    schema = group.AddSchema<BundledAssetGroupSchema>();
                }

                // ---- Configure schema to match your screenshot ----
                var prof = settings.profileSettings;

                // Build Path: Content/[BuildTarget]
                string buildVar = EnsureProfileVar(prof, "Content_BuildPath", "Content/[BuildTarget]");
                schema.BuildPath.SetVariableByName(settings, buildVar);

                // Load Path: {Application.streamingAssetsPath}/Addressables/Customization
                string loadVar = EnsureProfileVar(prof, "Customization_LoadPath",
                    "{Application.streamingAssetsPath}/Addressables/Customization");
                schema.LoadPath.SetVariableByName(settings, loadVar);

                // Common settings (attempt when available; reflection keeps this version-agnostic)
                SetEnumIfExists(schema, "Compression", "LZ4");
                SetBoolIfExists(schema, "IncludeInBuild", true);
                SetBoolIfExists(schema, "UseAssetBundleCache", true);
                SetBoolIfExists(schema, "UseAssetBundleCrc", false);
                SetBoolIfExists(schema, "UseAssetBundleCrcForCachedBundles", true);
                SetBoolIfExists(schema, "IncludeAddressesInCatalog", true);
                SetBoolIfExists(schema, "IncludeGUIDInCatalog", true);
                SetBoolIfExists(schema, "IncludeLabelsInCatalog", true);
                SetEnumIfExists(schema, "BundleMode", "PackTogether");
                SetEnumIfExists(schema, "BundleNaming", "Filename", "FileName", "NoHash");
                SetEnumIfExists(schema, "InternalIdNamingMode", "Filename", "FileName");
                SetEnumIfExists(schema, "InternalAssetNamingMode", "Filename", "FileName");

                UnityEditor.EditorUtility.SetDirty(schema);
                UnityEditor.EditorUtility.SetDirty(group);
                UnityEditor.AssetDatabase.SaveAssets();
                UnityEditor.AssetDatabase.Refresh();
                UnityEditor.AssetDatabase.RenameAsset(UnityEditor.AssetDatabase.GetAssetOrScenePath(group), PackName);

                // Copy entries list BEFORE we modify the group to avoid collection modification issues
                var existingEntries = group.entries != null
                    ? group.entries.ToList()
                    : new List<UnityEditor.AddressableAssets.Settings.AddressableAssetEntry>();
                var usedAddresses =
                    new HashSet<string>(existingEntries.Select(e => e.address).Where(a => !string.IsNullOrEmpty(a)));

                // Add/Move each prefab as an Addressable entry in this group
                foreach (var go in _items)
                {
                    if (go == null) continue;
                    var path = UnityEditor.AssetDatabase.GetAssetPath(go);
                    if (string.IsNullOrEmpty(path)) continue;

                    var guid = UnityEditor.AssetDatabase.AssetPathToGUID(path);
                    if (string.IsNullOrEmpty(guid)) continue;

                    var entry = settings.FindAssetEntry(guid);
                    if (entry == null || entry.parentGroup != group)
                    {
                        entry = settings.CreateOrMoveEntry(guid, group);
                    }

                    // Assign address equal to asset name (unique within the group)
                    string addressBase = go.name;
                    string address = addressBase;
                    int i = 1;
                    while (usedAddresses.Contains(address))
                    {
                        address = $"{addressBase}_{i++}";
                    }

                    if (address == addressBase)
                    {
                        usedAddresses.Add(address);

                        entry.address = address;

                        // Apply labels based on asset name tokens
                        foreach (var lab in LabelsFromAssetName(go.name))
                        {
                            entry.SetLabel(lab, true, true);
                        }

                        UnityEditor.EditorUtility.SetDirty(entry.TargetAsset);
                    }

                }

                // Remove entries that are no longer referenced
                var currentGuids = new HashSet<string>(_items
                    .Where(x => x != null)
                    .Select(x => UnityEditor.AssetDatabase.AssetPathToGUID(UnityEditor.AssetDatabase.GetAssetPath(x)))
                    .Where(g => !string.IsNullOrEmpty(g)));

                foreach (var old in existingEntries)
                {
                    if (!currentGuids.Contains(old.guid))
                    {
                        settings.RemoveAssetEntry(old.guid);
                    }
                }

                UnityEditor.EditorUtility.SetDirty(group);
                UnityEditor.AssetDatabase.SaveAssets();
                UnityEditor.AssetDatabase.Refresh();



                // ===== ICONS: keep icons in a separate "{PackName}_Icons" group =====
                var iconGroupName = PackName + "_Icons";
                var iconGroup = EnsureBundledGroup(settings, iconGroupName);

// Copy current entries & used addresses
                var iconExisting = iconGroup.entries != null
                    ? iconGroup.entries.ToList()
                    : new List<UnityEditor.AddressableAssets.Settings.AddressableAssetEntry>();
                var iconUsedAddrs = new HashSet<string>(
                    iconExisting.Select(e => e.address).Where(a => !string.IsNullOrEmpty(a))
                );

// Add/move each Texture2D in _icons to icon group
                if (_icons != null)
                {
                    foreach (var tex in _icons)
                    {
                        if (tex == null) continue;
                        var texPath = UnityEditor.AssetDatabase.GetAssetPath(tex);
                        if (string.IsNullOrEmpty(texPath)) continue;

                        var guid = UnityEditor.AssetDatabase.AssetPathToGUID(texPath);
                        if (string.IsNullOrEmpty(guid)) continue;

                        var entry = settings.FindAssetEntry(guid);
                        if (entry == null || entry.parentGroup != iconGroup)
                        {
                            entry = settings.CreateOrMoveEntry(guid, iconGroup);
                        }

                        string addressBase = tex.name;
                        string address = addressBase;
                        int i = 1;
                        while (iconUsedAddrs.Contains(address))
                            address = $"{addressBase}_{i++}";

                        if (address == addressBase)
                        {
                            iconUsedAddrs.Add(address);
                            entry.address = address;

                            foreach (var lab in LabelsFromAssetName(tex.name))
                                entry.SetLabel(lab, true, true);

                            EditorUtility.SetDirty(entry.TargetAsset);
                        }
                    }
                }

// Remove icon entries no longer referenced by _icons
                var currentIconGuids = new HashSet<string>(
                    (_icons ?? new List<Texture2D>())
                    .Where(x => x != null)
                    .Select(x => UnityEditor.AssetDatabase.AssetPathToGUID(UnityEditor.AssetDatabase.GetAssetPath(x)))
                    .Where(g => !string.IsNullOrEmpty(g))
                );

                foreach (var old in iconExisting)
                {
                    if (!currentIconGuids.Contains(old.guid))
                        settings.RemoveAssetEntry(old.guid);
                }

                EditorUtility.SetDirty(iconGroup);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            finally
            {
                _syncInProgress = false;
            }
        }


        // add inside class (utility)
        private AddressableAssetGroup EnsureBundledGroup(AddressableAssetSettings settings, string groupName)
        {
            var group = settings.FindGroup(groupName);
            if (group == null)
            {
                // clean stray "New Group" if empty
                var stray = settings.groups.FirstOrDefault(g => g != null && g.Name == "New Group");
                if (stray != null && (stray.entries == null || stray.entries.Count == 0))
                    settings.RemoveGroup(stray);

                group = settings.CreateGroup(
                    groupName,
                    setAsDefaultGroup: false,
                    readOnly: false,
                    postEvent: false,
                    schemasToCopy: null,
                    typeof(BundledAssetGroupSchema)
                );
            }

            // Ensure schema + profile vars like your primary group
            var schema = group.GetSchema<BundledAssetGroupSchema>() ?? group.AddSchema<BundledAssetGroupSchema>();
            var prof = settings.profileSettings;

            string buildVar = EnsureProfileVar(prof, "Content_BuildPath", "Content/[BuildTarget]");
            schema.BuildPath.SetVariableByName(settings, buildVar);

            string loadVar = EnsureProfileVar(prof, "Customization_LoadPath",
                "{Application.streamingAssetsPath}/Addressables/Customization");
            schema.LoadPath.SetVariableByName(settings, loadVar);

            SetEnumIfExists(schema, "Compression", "LZ4");
            SetBoolIfExists(schema, "IncludeInBuild", true);
            SetBoolIfExists(schema, "UseAssetBundleCache", true);
            SetBoolIfExists(schema, "UseAssetBundleCrc", false);
            SetBoolIfExists(schema, "UseAssetBundleCrcForCachedBundles", true);
            SetBoolIfExists(schema, "IncludeAddressesInCatalog", true);
            SetBoolIfExists(schema, "IncludeGUIDInCatalog", true);
            SetBoolIfExists(schema, "IncludeLabelsInCatalog", true);
            SetEnumIfExists(schema, "BundleMode", "PackTogether");
            SetEnumIfExists(schema, "BundleNaming", "Filename", "FileName", "NoHash");
            SetEnumIfExists(schema, "InternalIdNamingMode", "Filename", "FileName");
            SetEnumIfExists(schema, "InternalAssetNamingMode", "Filename", "FileName");

            EditorUtility.SetDirty(schema);
            EditorUtility.SetDirty(group);
            return group;
        }


        // -------------- Utility: enum assignment helpers (as in your original) --------------
        private static bool TryAssignEnum(System.Type enumType, string[] candidates, out object value)
        {
            value = null;
            foreach (var c in candidates)
            {
                try
                {
                    value = System.Enum.Parse(enumType, c, true);
                    return true;
                }
                catch { }
            }
            return false;
        }

        private static void TryAssignEnumToPropertyOrField(object obj, string prop, params string[] candidates)
        {
            if (obj == null || candidates == null || candidates.Length == 0) return;
            var t = obj.GetType();

            // Try property first
            var pi = t.GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pi != null && pi.PropertyType.IsEnum)
            {
                if (TryAssignEnum(pi.PropertyType, candidates, out var val))
                {
                    pi.SetValue(obj, val, null);
                    return;
                }
            }

            // Then try field
            var fi = t.GetField(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi != null && fi.FieldType.IsEnum)
            {
                if (TryAssignEnum(fi.FieldType, candidates, out var val))
                {
                    fi.SetValue(obj, val);
                }
            }
        }
        // ---- Added helpers (non-breaking) ----
        private static void SetEnumIfExists(object obj, string prop, params string[] candidates)
        {
            TryAssignEnumToPropertyOrField(obj, prop, candidates);
        }
        
        private static string EnsureProfileVar(UnityEditor.AddressableAssets.Settings.AddressableAssetProfileSettings prof, string name, string defaultValueIfCreated)
        {
            try { prof.CreateValue(name, defaultValueIfCreated); } catch { }
            return name; // we always refer to variables by NAME
        }

        private static void SetBoolIfExists(object obj, string prop, bool value)
        {
            if (obj == null) return;
            var t = obj.GetType();
            var pi = t.GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pi != null && pi.PropertyType == typeof(bool)) { pi.SetValue(obj, value, null); return; }
            var fi = t.GetField(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (fi != null && fi.FieldType == typeof(bool)) { fi.SetValue(obj, value); }
        }
        
        [ContextMenu("Content/Clean Missing References")]
        public void RemoveMissingReferences()
        {
            if (_items == null || _items.Count == 0) return;
            //UnityEditor.Undo.RecordObject(this, "Remove Missing References from Content Pack");
            int removed = 0;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                if (_items[i] == null)
                {
                    _items.RemoveAt(i);
                    removed++;
                }
            }
            if (removed > 0)
            {
                UnityEditor.EditorUtility.SetDirty(this);
                UnityEditor.AssetDatabase.SaveAssets();
                Debug.Log($"[{nameof(ContentPackDefinition)}] Removed {removed} missing reference(s) from {_packName}.");
            }
        }

    }
}

#endif
