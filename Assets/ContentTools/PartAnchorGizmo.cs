using System;
using UnityEngine;
namespace ContentTools
{
    public class PartAnchorGizmo : MonoBehaviour
    {
        [SerializeField] [Range(0.001f,.1f)]private float _radius = 0.05f;//
        // Start is called before the first frame update
        void Start()
        {
        
        }
        private void OnDrawGizmos()
        {
            Gizmos.color = Color.red;
            DrawGizmosForDeepChildren(transform);
        }

        private void DrawGizmosForDeepChildren(Transform parent)
        {
            foreach(Transform child in parent) 
            {
                if(child.name.Contains("_Anchor")) 
                {
                    Gizmos.DrawSphere(child.position, _radius);
                }
                DrawGizmosForDeepChildren(child);
            }
        }
    }
}