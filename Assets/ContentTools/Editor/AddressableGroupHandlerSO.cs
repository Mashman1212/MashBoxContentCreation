using System.IO;
using UnityEditor;
using UnityEditor.AddressableAssets;
using UnityEditor.AddressableAssets.Settings;
using UnityEngine;

namespace ContentTools 
{
    [CreateAssetMenu(menuName = "ScriptableObjects/AddressableGroupHandler")]
    public class AddressableGroupHandlerSo : ScriptableObject 
    {
        [SerializeField] private AddressableAssetGroup _addressableGroup;
        
        [SerializeField] private string _assetsFolder;
        
        [ContextMenu("Add Assets To Group")]
        public void AddAssetsToGroup()
        {
            
            _assetsFolder = AssetDatabase.GetAssetPath(this);
            _assetsFolder = _assetsFolder.Replace("/" + Path.GetFileName(_assetsFolder), "");
            var assetGUIDs = AssetDatabase.FindAssets("", new[]{_assetsFolder});

            if (_addressableGroup == null)
            {
                return;
            }
            
            foreach (var guid in assetGUIDs)
            {
                var assetPath = AssetDatabase.GUIDToAssetPath(guid);

                if (assetPath != AssetDatabase.GetAssetPath(this))
                {
                    var settings = AddressableAssetSettingsDefaultObject.Settings;
                    var entry = settings.CreateOrMoveEntry(guid, _addressableGroup);

                    // Set the address to the name of the file
                    entry.address = Path.GetFileNameWithoutExtension(assetPath);

                    settings.SetDirty(AddressableAssetSettings.ModificationEvent.EntryMoved, entry, true, true); 
                } 
            }
        }

        private void OnValidate()
        {
            AddAssetsToGroup();
        }
    }
}