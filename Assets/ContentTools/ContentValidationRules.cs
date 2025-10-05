#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using UnityEngine;

namespace ContentTools
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
        public class ChildPattern
        {
            [Tooltip("Optional path under the prefab root to search (use '/' like Transform.Find). Leave empty for root.")]
            public string PathPrefix;

            [Tooltip("Regex that matching child names must satisfy, e.g. ^StemBolt_Anchor_\\d+$")]
            public string NameRegex;

            [Tooltip("Minimum number of matches required")]
            public int Min = 1;

            [Tooltip("Maximum number of matches allowed (int.MaxValue = no upper bound)")]
            public int Max = int.MaxValue;

            [Tooltip("If true, only immediate children of PathPrefix are considered. Otherwise, search recursively.")]
            public bool DirectChildrenOnly = true;
        }

        [Serializable]
        public class AnchorRule
        {
            public string AppliesToSuperType;
            public string AppliesToType;
            public string AppliesToBrand;

            // already exists (exact names)
            public string[] RequiredChildren;

            // 🔹 NEW: pattern-based requirements
            public ChildPattern[] RequiredPatterns;
            
            private bool ForbidUnexpectedChildren = true;
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
