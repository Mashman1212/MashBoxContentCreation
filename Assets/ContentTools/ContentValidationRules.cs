#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ContentTools.Editor
{
    /// <summary>
    /// Project-wide configuration for validating item names and required anchors.
    /// Create via: Assets → Create → Content → Validation Rules.
    /// Brand is NOT validated; we only parse it to scope anchor rules if you want.
    /// </summary>
    [CreateAssetMenu(fileName = "ContentValidationRules",
        menuName = "Content/Validation Rules", order = 2100)]
    public class ContentValidationRules : ScriptableObject
    {
        [Header("Allowed tokens (case-sensitive)")]
        [Tooltip("Optional master list. Type validation uses AllowedPairs below.")]
        public string[] SuperTypes;   // e.g. ["Scooter","BMX"]

        [Tooltip("Optional color whitelist. Leave empty to allow any.")]
        public string[] Colors;       // e.g. ["Black","Blue","Vanilla_Bean"]

        [Serializable]
        public class SuperTypeTypes
        {
            public string SuperType;  // e.g. "Scooter"
            public string[] Types;    // e.g. ["Deck","Bars","Clamp", ...]
        }

        [Header("Allowed SuperType → Types (paired)")]
        public List<SuperTypeTypes> AllowedPairs = new();

        [Serializable]
        public class AnchorRule
        {
            [Tooltip("Leave blank to wildcard")]
            public string AppliesToSuperType;   // optional
            public string AppliesToType;        // optional
            public string AppliesToBrand;       // optional (Brand not validated)

            [Tooltip("Exact child names under the prefab root that must exist")]
            public string[] RequiredChildren;   // e.g. "Bars_Anchor", "StemBolt_Anchor_1", ...
        }

        [Header("Anchor requirements")]
        public List<AnchorRule> AnchorRules = new();

        // ---------- helpers ----------
        public bool IsAllowed(string tok, string[] list)
            => !string.IsNullOrEmpty(tok) && list != null && Array.IndexOf(list, tok) >= 0;

        public bool IsAllowedPair(string superType, string type)
        {
            if (string.IsNullOrEmpty(superType) || string.IsNullOrEmpty(type)) return false;
            var entry = AllowedPairs.Find(p => p.SuperType == superType);
            return entry != null && Array.IndexOf(entry.Types, type) >= 0;
        }

        public IEnumerable<AnchorRule> RulesFor(string superType, string type, string brand)
        {
            return AnchorRules.Where(r =>
                (string.IsNullOrEmpty(r.AppliesToSuperType) || r.AppliesToSuperType == superType) &&
                (string.IsNullOrEmpty(r.AppliesToType)      || r.AppliesToType      == type) &&
                (string.IsNullOrEmpty(r.AppliesToBrand)     || r.AppliesToBrand     == brand));
        }
    }
}
#endif
