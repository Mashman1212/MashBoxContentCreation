using UnityEngine;

namespace ContentTools
{
    [CreateAssetMenu(fileName = "ContentPack", menuName = "Addressables/Content Pack", order = 2000)]
    public class ContentPackDefinition : ScriptableObject
    {
        public string packName => this.name;           // used in paths & Player Version Override
        //public string version = "1.0.0";               // semantic version for clarity

        [Header("Addressables Groups")]
        [Tooltip("Exact Addressables Group names that belong to this pack")]
        public string[] groupNames;

        [Header("Labels (optional)")]
        [Tooltip("Optional labels you will use at runtime to query assets from this pack")]
        public string[] labels;

        [Header("Output (per-pack overrides)")]
        [Tooltip("Optional subfolder under Remote paths. If empty, packName is used.")]
        public string remoteSubfolderOverride;

        [Tooltip("If set, overrides the profile's Remote.BuildPath root. Supports tokens: [BuildTarget], {pack}")]
        public string remoteBuildRootOverride; // e.g., D:/addr_builds/[BuildTarget]/{pack}

        [Tooltip("If set, overrides the profile's Remote.LoadPath root (URL). Supports tokens: [BuildTarget], {pack} and {Application.streamingAssetsPath}")]
        public string remoteLoadRootOverride;  // e.g., {Application.streamingAssetsPath}/Addressables/Customization/{pack}

        [Header("Advanced")]
        [Tooltip("If true, we will copy the generated catalog.json path to clipboard after build.")]
        public bool copyCatalogPathToClipboard = true;
    }
}