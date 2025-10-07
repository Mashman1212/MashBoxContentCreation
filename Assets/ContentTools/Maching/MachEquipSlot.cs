using UnityEngine;
using System.Globalization;

namespace ContentTools.Maching
{
    [ExecuteAlways]
    public class MachEquipSlot : MonoBehaviour
    {
        [SerializeField] private string slotTag = "default";
        [SerializeField] private GameObject equippedItem;

        private void OnEnable()
        {
            // Auto-detect first child if no equipped item assigned.
            if (equippedItem == null && transform.childCount > 0)
            {
                equippedItem = transform.GetChild(0).gameObject;
#if UNITY_EDITOR
                Debug.Log($"[MachEquipSlot] {name} detected '{equippedItem.name}' as equipped item.");
#endif
            }
        }

        /// <summary>
        /// Attempts to equip a new item to this slot.
        /// Only allows items whose name begins with this slot's tag (case-insensitive).
        /// </summary>
        public void Equip(GameObject item)
        {
            if (item == null)
            {
                Debug.LogWarning($"[MachEquipSlot] Tried to equip null item on {name}");
                return;
            }

            // Case-insensitive tag check
            string itemName = item.name.ToLower(CultureInfo.InvariantCulture);
            string tagLower = slotTag.ToLower(CultureInfo.InvariantCulture);

            if (!itemName.Contains("_" +tagLower + "_"))
            {
                Debug.LogWarning($"[MachEquipSlot] '{item.name}' does not match slot tag '{slotTag}' on {name}. Equip canceled.");
                return;
            }

            ClearChildren();

            GameObject newItem = Instantiate(item, transform);
            newItem.name = item.name;
            equippedItem = newItem;

#if UNITY_EDITOR
            Debug.Log($"[MachEquipSlot] Equipped {item.name} on {name}");
#endif
        }

        public void Unequip()
        {
            ClearChildren();
            equippedItem = null;
        }

        public GameObject GetEquippedItem() => equippedItem;

        private void ClearChildren()
        {
            for (int i = transform.childCount - 1; i >= 0; i--)
            {
#if UNITY_EDITOR
                if (!Application.isPlaying)
                    DestroyImmediate(transform.GetChild(i).gameObject);
                else
                    Destroy(transform.GetChild(i).gameObject);
#else
                Destroy(transform.GetChild(i).gameObject);
#endif
            }
        }

#if UNITY_EDITOR
        private void OnValidate()
        {
            if (string.IsNullOrWhiteSpace(slotTag))
                slotTag = "default";
        }
#endif
    }
}
