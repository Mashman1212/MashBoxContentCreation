using System.Collections.Generic;
using System.Reflection;
using System.Linq;
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

        string addressablesGroupName;

        private bool autoSyncOnValidate = true;

#if UNITY_EDITOR
        // ---------------- Editor-only sync logic ----------------

        // Re-entrancy guard to prevent Validate -> Sync -> Validate loops
        private static bool _syncInProgress;
        private static bool _syncScheduled;

        private static readonly HashSet<string> LabelStopWords =
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase)
                { "bean" }; // ignore "Bean" per your examples; edit as you like

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
            SyncToAddressables();
            if (!autoSyncOnValidate) return;
            if (Application.isPlaying) return; // don't mutate Addressables in play mode
            if (_syncInProgress) return; // currently syncing from a previous call

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
                            groupName, false, false, false, null,
                            typeof(UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema)
                        );
                    }
                    // (If it had entries you’d migrate them here; usually new packs won’t.)
                }

// Ensure schema and apply your required settings/paths
                var schema =
                    group.GetSchema<UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema>();
                if (schema == null)
                    schema = group
                        .AddSchema<UnityEditor.AddressableAssets.Settings.GroupSchemas.BundledAssetGroupSchema>();

// Build Path: Content/[BuildTarget]
                var prof = settings.profileSettings;
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

                    // Simplify address: use file name without extension; ensure unique within group
                    var baseName = System.IO.Path.GetFileNameWithoutExtension(path);
                    string address = baseName;
                    //int n = 2;
                    //while (usedAddresses.Contains(address))
                    //    address = $"{baseName}_{n++}";

                    if (entry.address != address)
                    {
                        entry.SetAddress(address);
                        usedAddresses.Add(address);
                    }

                    // Auto-labels: filename tokens + pack-level labels
                    foreach (var lab in LabelsFromAssetName(baseName))
                        entry.SetLabel(lab, true, true);


                }

                UnityEditor.AssetDatabase.SaveAssets();
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[{nameof(ContentPackDefinition)}] Sync failed:\n{ex}");
            }
            finally
            {
                _syncInProgress = false;
            }
        }

        // ---- helpers ----

        private static string EnsureProfileVar(
            UnityEditor.AddressableAssets.Settings.AddressableAssetProfileSettings prof, string name,
            string defaultValueIfCreated)
        {
            try
            {
                prof.CreateValue(name, defaultValueIfCreated);
            }
            catch
            {
            }

            return name; // we always refer to variables by NAME
        }

        private static void SetBoolIfExists(object obj, string prop, bool value)
        {
            var t = obj.GetType();
            var p = t.GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (p != null && p.PropertyType == typeof(bool) && p.CanWrite)
            {
                p.SetValue(obj, value, null);
                return;
            }

            var f = t.GetField(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (f != null && f.FieldType == typeof(bool)) f.SetValue(obj, value);
        }

        // Flexible across Addressables versions: tries multiple enum value spellings.
        private static void SetEnumIfExists(object obj, string prop, params string[] candidates)
        {
            var t = obj.GetType();
            // Try property first
            var pi = t.GetProperty(prop, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
            if (pi != null && pi.CanWrite && pi.PropertyType.IsEnum)
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

        private static bool TryAssignEnum(System.Type enumType, string[] candidates, out object value)
        {
            value = null;
            var names = System.Enum.GetNames(enumType);
            string Normalize(string s) => new string(s.ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

            foreach (var cand in candidates)
            {
                foreach (var name in names)
                {
                    if (string.Equals(name, cand, System.StringComparison.OrdinalIgnoreCase) ||
                        Normalize(name) == Normalize(cand))
                    {
                        value = System.Enum.Parse(enumType, name);
                        return true;
                    }
                }
            }

            return false;
        }
        
#if UNITY_EDITOR
        [ContextMenu("Content/Clean Missing References")]
        public void RemoveMissingReferences()
        {
            if (_items == null || _items.Count == 0) return;

            // Record for undo in the editor
            UnityEditor.Undo.RecordObject(this, "Remove Missing References from Content Pack");

            int removed = 0;
            for (int i = _items.Count - 1; i >= 0; i--)
            {
                // In Unity, a broken or destroyed reference compares equal to null
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
#endif

#endif
    }
}
