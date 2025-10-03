using UnityEditor;
using UnityEngine;

namespace ContentTools.Editor
{
    public class CopyPasteMaterials : ScriptableObject
    {
        private static Material[] copiedMaterials;

        [MenuItem("Edit/Copy Material(s) &c", false, 150)]
        private static void CopyMaterials()
        {
            if (Selection.activeGameObject != null)
            {
                Renderer renderer = Selection.activeGameObject.GetComponent<Renderer>();
                if (renderer != null)
                {
                    copiedMaterials = renderer.sharedMaterials;
                }
                else
                {
                    Debug.LogWarning("Selected GameObject has no MeshRenderer.");
                }
            }
        }
        [MenuItem("Edit/Paste Material(s) &v", false, 151)]
        private static void PasteMaterials()
        {
            if (Selection.activeGameObject != null)
            {
                Renderer renderer = Selection.activeGameObject.GetComponent<Renderer>();
                if (renderer != null && copiedMaterials != null)
                {
                    // Record the current state of renderer for undo operation
                    // Mention the name of operation
                    Undo.RecordObject(renderer, "Paste Materials");

                    // Apply the changes
                    renderer.sharedMaterials = copiedMaterials;
            
                    Debug.Log("Pasted Material(s) to Selected GameObject.");
                }
                else
                {
                    Debug.LogWarning("Either Selected GameObject has no MeshRenderer or no Material(s) have been copied yet.");
                }
            }
        }
    }
}