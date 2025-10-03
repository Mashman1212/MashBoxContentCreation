using UnityEditor;

namespace ContentTools.Editor
{
    public class CreateAssetBundles : UnityEditor.Editor
    {
        [MenuItem("Tools/MashBox/AssetBundles/BuildAllAssetBundles")]
        public static void BuildAllAssetBundles()
        {
            //
            BuildAssetBundleOptions buildAssetBundleOptions = BuildAssetBundleOptions.UncompressedAssetBundle;
            BuildPipeline.BuildAssetBundles("Assets/../Asset Bundles", buildAssetBundleOptions, EditorUserBuildSettings.activeBuildTarget);
        }
    }
}