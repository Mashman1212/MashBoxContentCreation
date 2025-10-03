using System.Collections;
using UnityEngine;
using UnityEngine.AddressableAssets;
using UnityEngine.ResourceManagement.AsyncOperations;

namespace ContentTools
{
    public class SampleAddressableLoader : MonoBehaviour
    {
        [SerializeField] private string _assetAddress;
        private void Start()
        {
            StartCoroutine(InitializeAddressables());
        }

        IEnumerator InitializeAddressables()
        {
            // Initialize Addressables system and set the runtime path  
            var asyncOperation = Addressables.InitializeAsync();
            yield return asyncOperation;
        
        
            // Once we're sure the Addressables system is ready, proceed to load an asset

            // Don't forget the address of the asset must match exactly the address originally set
            Addressables.LoadAssetAsync<GameObject>(_assetAddress).Completed += OnAssetLoaded;

        }

        private void OnAssetLoaded(AsyncOperationHandle<GameObject> obj)
        {
            // When the asset is loaded successfully, instantiate it into the scene
            if (obj.Status == AsyncOperationStatus.Succeeded)
            {
                Instantiate(obj.Result);
            }
            else 
            {
                Debug.LogError($"Failed to load asset at address {obj.DebugName}. Error: {obj.OperationException}");
            }
        }
    }
}