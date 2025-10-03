using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ContentTools.Editor
{
    public class BundleGrouper : EditorWindow
    {
        private string brandName = "Saturday";
        private string bundleID = "Vanilla";
        private string searchFolder = "Assets/MG Content/Vanilla";

        [MenuItem("Tools/MashBox/Bundle Grouper")]
        static void Init()
        {
            BundleGrouper window = (BundleGrouper)EditorWindow.GetWindow(typeof(BundleGrouper));
            window.Show();

        }

        void OnGUI()
        {
            GUILayout.Label ("Brand Settings", EditorStyles.boldLabel);
            brandName = EditorGUILayout.TextField("Brand Name", brandName);
            bundleID = EditorGUILayout.TextField("Bundle ID", bundleID);
            searchFolder = EditorGUILayout.TextField("Search Folder", searchFolder); // Added search folder input
            if (GUILayout.Button("Group By Brand"))
            {
                GroupAssetsByBrand(brandName, bundleID, searchFolder);
            }
        }
        public static void GroupAssetsByBrand(string brandName,string bundleID, string searchFolder) // Added search folder parameter
        {
            
            
            string bundleName = $"{Application.productName}-{brandName}-{bundleID}";

            // Use searchFolder as root search directory
            string[] guids = AssetDatabase.FindAssets("t:prefab", new[] { searchFolder });
            foreach (string guid in guids)
            {
                string assetPath = AssetDatabase.GUIDToAssetPath(guid);
                string fileName = System.IO.Path.GetFileNameWithoutExtension(assetPath);
                if (System.IO.Path.GetFileName(System.IO.Path.GetDirectoryName(assetPath)) == "Prefabs")
                    if (fileName.ToLowerInvariant().Contains($"_{brandName.ToLowerInvariant()}_"))
                    {
                        AssetImporter.GetAtPath(assetPath).assetBundleName = bundleName;
                    }
            }

            AssetDatabase.Refresh();
        }
    }
}