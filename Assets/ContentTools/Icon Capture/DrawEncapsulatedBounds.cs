using UnityEngine;

namespace ContentTools.Icon_Capture
{
    public class DrawEncapsulatedBounds : MonoBehaviour
    {
        private Renderer[] renderers;

        private Renderer _objectRenderer;

        private Bounds _localBounds;
        private Bounds _worldBounds;
        private Bounds _bounds;
        // Calculate the total bounds by encapsulating bounds of all child renderers
        void UpdateBounds()
        {
            _objectRenderer = GetComponentInChildren<Renderer>();
            _localBounds = new Bounds(_objectRenderer.localBounds.center, _objectRenderer.localBounds.size);
            
            MeshFilter[] meshFilters = GetComponentsInChildren<MeshFilter>();
            _worldBounds = new Bounds(transform.position, Vector3.zero);
    
            foreach (MeshFilter mf in meshFilters)
            {
                foreach (Vector3 vertex in mf.sharedMesh.vertices)
                {
                    Vector3 worldVertex = mf.transform.TransformPoint(vertex);
                    _worldBounds.Encapsulate(worldVertex);
                }
            }
        }

        // Called by Unity to draw gizmos in the editor
        void OnDrawGizmos()
        {
            UpdateBounds();
            // Draw local bounds
            Gizmos.color = Color.red;
            Matrix4x4 oldMatrix = Gizmos.matrix;
            Gizmos.matrix = _objectRenderer.transform.localToWorldMatrix;
            Gizmos.DrawWireCube(_localBounds.center, _localBounds.size);

            // Draw world bounds
            Gizmos.matrix = oldMatrix;
            Gizmos.color = Color.green;
            Gizmos.DrawWireCube(_worldBounds.center, _worldBounds.size); // Draws world bounds as a green wire cube

            Gizmos.matrix = oldMatrix;
        }
                
 
    }
}