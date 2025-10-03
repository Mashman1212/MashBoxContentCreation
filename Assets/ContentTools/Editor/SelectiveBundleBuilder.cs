using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace ContentTools.Editor
{
    public class SelectiveBundleBuilder : EditorWindow
    {
        [MenuItem("Tools/MashBox/Selective AssetBundle Builder")]
        static void Init()
        {
            SelectiveBundleBuilder window = (SelectiveBundleBuilder)EditorWindow.GetWindow(typeof(SelectiveBundleBuilder));
            window.Show();
        }

        void OnGUI()
        {
            //if (GUILayout.Button("Build Selected AssetBundles"))
            //{
            //    BuildAssetBundlesByName(new []{"neon nights"},"Assets/AssetBundles");
            //}
            
            //string [] bundleNames = new []{"neon nights", "colorway two", "xmas bundle", "easter"};
            string[] bundleNames = AssetDatabase.GetAllAssetBundleNames();
            foreach (string bundleName in bundleNames)
            {
                if (GUILayout.Button(bundleName))
                {
                    BuildAssetBundlesByName(new[] { bundleName }, "Assets/AssetBundles");
                }
            }
        }

        public static void BuildAssetBundlesByName(string[] assetBundleNames, string outputPath) 
        {
            // Argument validation
            if (assetBundleNames == null || assetBundleNames.Length == 0)
            {
                return;
            }

            // Remove duplicates from the input set of asset bundle names to build.
            //assetBundleNames = assetBundleNames.Distinct().ToArray();

            List<AssetBundleBuild> builds = new List<AssetBundleBuild>();

            foreach (string assetBundle in assetBundleNames)
            {
                var assetPaths = AssetDatabase.GetAssetPathsFromAssetBundle(assetBundle);

                AssetBundleBuild build = new AssetBundleBuild();
                build.assetBundleName = assetBundle;
                build.assetNames = assetPaths;

                builds.Add(build);
                Debug.Log("assetBundle to build:" + build.assetBundleName);
            }

            BuildPipeline.BuildAssetBundles(outputPath, builds.ToArray(), BuildAssetBundleOptions.None, EditorUserBuildSettings.activeBuildTarget);
        }
    }
}