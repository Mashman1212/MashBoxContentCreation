#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace ContentTools.Editor
{
    [Serializable]
    public class ContentPackManifest
    {
        [Serializable]
        public class ItemRef
        {
            public string guid;                   // Unity GUID of the prefab asset
            public string type = "prefab";        // Always "prefab"
        }

        public List<ItemRef> items = new List<ItemRef>();

        /// <summary>
        /// Save this manifest as a JSON file.
        /// </summary>
        public void SaveToJson(string path)
        {
            try
            {
                var json = JsonUtility.ToJson(this, true);
                File.WriteAllText(path, json);
                Debug.Log($"[ContentPackManifest] ✅ Saved manifest: {path}");
                AssetDatabase.Refresh();
            }
            catch (Exception ex)
            {
                Debug.LogError($"[ContentPackManifest] ❌ Failed to save manifest: {ex.Message}");
            }
        }

        /// <summary>
        /// Generate a manifest from prefab GUIDs and save it to disk.
        /// </summary>
        public static void GenerateManifest(List<string> prefabGuids, string outputPath)
        {
            var manifest = new ContentPackManifest();
            foreach (var g in prefabGuids)
                manifest.items.Add(new ItemRef { guid = g });
            manifest.SaveToJson(outputPath);
        }
    }
}
#endif