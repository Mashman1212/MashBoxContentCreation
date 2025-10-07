
using UnityEngine;

namespace ContentTools.Maching
{
    [ExecuteAlways]
    public class Anchor : MonoBehaviour
    {
        [SerializeField] private string _rootingPrefix = "";
        [SerializeField] private string _directionID = "";
        
        [SerializeField] private bool _ingnoreRotation = false;
        [SerializeField] private bool _yaw180 = false;
        [SerializeField] private bool _yawVisual180 = false;
        [SerializeField] private bool _pitch180 = false;
        [SerializeField] private bool _roll180 = false;
        [SerializeField] private bool _ignoreScale;

        private Transform _anchorTrans;
        private string _anchorHuntName;
        void Update()
        {
        
            HuntAnchor();
            SnapToAnchor();

            for (int i = 0; i < this.transform.childCount; ++i)
            {
                this.transform.GetChild(i).SetLocalPositionAndRotation(Vector3.zero, Quaternion.identity);
            }
        }
        

        public void HuntAnchor()
        {
            if (_anchorTrans)
            {
                return;
            }
            
            _anchorHuntName = _rootingPrefix.Replace(" ", "").ToLower();
            
            MachVehicle machVehicle = GetComponentInParent<MachVehicle>();

            if (!machVehicle)
            {
                return;
            }

            Transform searchRoot = machVehicle.transform;
            _anchorTrans = FindAnchor(searchRoot,_anchorHuntName);
        }
        
        Transform FindAnchor(Transform aParent, string aName)
        {
            var result = aParent.Find(aName);
            if (result != null && result.gameObject.activeInHierarchy)
                return result;
            
            string parentName = aParent.name;
            parentName = parentName.Replace(" ", "").Replace("_", "").Replace("(", "").Replace(")", "").ToLower();
            
            if (parentName.StartsWith(aName.ToLower() + "anchor"))
            {
                if (string.IsNullOrEmpty(_directionID))
                {
                    if (aParent.gameObject.activeInHierarchy) return aParent;
                }
                else
                {
                    if (FindDirectionID(aParent,_directionID) && aParent.gameObject.activeInHierarchy)
                    {
                        return aParent;
                    }
                }
            }
            foreach (Transform child in aParent)
            {
                result = FindAnchor(child,aName);
                if (result != null && result.gameObject.activeInHierarchy)
                    return result;
            }
            return null;
        }



        void SnapToAnchor()
        {
            if (!_anchorTrans)
            {
                return;
            }
            
            this.transform.position = _anchorTrans.position;

            if (!_ignoreScale)
                this.transform.localScale = _anchorTrans.localScale;

            if (!_ingnoreRotation)
            {
                this.transform.rotation = _anchorTrans.rotation;

                if (_yaw180)
                {
                    this.transform.localRotation *= Quaternion.AngleAxis(180, Vector3.up);
                }

                if (_pitch180)
                {
                    this.transform.localRotation *= Quaternion.AngleAxis(180, Vector3.right);
                }

                if (_roll180)
                {
                    this.transform.localRotation *= Quaternion.AngleAxis(180, Vector3.forward);
                }
            }
            
        }
        
        
        Transform FindDirectionID(Transform aChild, string directionID)
        {
            if (aChild.name.ToLower().EndsWith(directionID.ToLower())) 
            {
                return aChild;
            }
        
            Transform aParent = aChild.parent ? aChild.parent : null;
            
            if (aParent == null) 
            {
                return null;
            }
            
            string parentName = aParent.name;
            
            if (parentName.ToLower().EndsWith(directionID.ToLower()) || parentName.ToLower().StartsWith(directionID.ToLower())) 
            {
                return aParent;
            }
            
            return FindDirectionID(aParent, directionID);
        }

        private void OnValidate()
        {
            _anchorTrans = null;
        }
    }
}