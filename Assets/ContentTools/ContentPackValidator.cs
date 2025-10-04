#if UNITY_EDITOR
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace ContentTools.Editor
{
    /// <summary>
    /// Validates:
    ///  • Prefab asset (not a scene object)
    ///  • Name format: [SuperType]_[Type]_[Brand]_[Color]
    ///  • SuperType↔Type pair (from rules)
    ///  • Color whitelist (optional)
    ///  • Required anchor children (from rules)
    /// Brand is parsed but NOT validated.
    /// </summary>
    public static class ContentPackValidator
    {
        public enum Severity { Info, Warning, Error }

        public class Issue
        {
            public Severity severity;
            public string message;
            public Object context; // offending asset (prefab or pack)
        }

        public static List<Issue> ValidatePack(ContentPackDefinition pack, ContentValidationRules rules)
        {
            var issues = new List<Issue>();
            if (!pack)
            {
                issues.Add(new Issue { severity = Severity.Error, message = "Pack is null" });
                return issues;
            }

            if (pack._items == null || pack._items.Count == 0)
            {
                issues.Add(new Issue { severity = Severity.Warning, message = $"Pack '{pack.name}' has no items.", context = pack });
                return issues;
            }

            foreach (var go in pack._items)
                ValidateItem(go, rules, issues);

            return issues;
        }

        public static List<Issue> ValidateItem(GameObject go, ContentValidationRules rules, List<Issue> buffer = null)
        {
            var issues = buffer ?? new List<Issue>();

            if (!go)
            {
                issues.Add(new Issue { severity = Severity.Error, message = "Null item reference." });
                return issues;
            }

            // Must be a prefab asset (not a scene object)
            var pType = PrefabUtility.GetPrefabAssetType(go);
            if (pType == PrefabAssetType.NotAPrefab || pType == PrefabAssetType.MissingAsset)
            {
                issues.Add(new Issue { severity = Severity.Error, message = $"'{go.name}' is not a prefab asset.", context = go });
                return issues;
            }

            // Split by underscores; require at least 4 tokens
            var parts = go.name.Split('_');
            if (parts.Length < 4)
            {
                issues.Add(new Issue { severity = Severity.Error, message = $"{go.name}: name must be [SuperType]_[Type]_[Brand]_[Color].", context = go });
                return issues;
            }

            string superType = parts[0];
            string type      = parts[1];
            string color     = parts[^1];
            string brand     = string.Join("_", parts.Skip(2).Take(parts.Length - 3)); // not validated; usable for anchors

            if (rules != null)
            {
                bool superKnown = rules.IsAllowed(superType, rules.SuperTypes) ||
                                  rules.AllowedPairs.Exists(p => p.SuperType == superType);

                if (!superKnown)
                    issues.Add(new Issue { severity = Severity.Error, message = $"{go.name}: unknown SuperType '{superType}'", context = go });

                if (!rules.IsAllowedPair(superType, type))
                    issues.Add(new Issue { severity = Severity.Error, message = $"{go.name}: invalid Type '{type}' for SuperType '{superType}'", context = go });

                if (rules.Colors != null && rules.Colors.Length > 0 && !rules.IsAllowed(color, rules.Colors))
                    issues.Add(new Issue { severity = Severity.Error, message = $"{go.name}: unknown Color '{color}'", context = go });

                foreach (var rule in rules.RulesFor(superType, type, brand))
                {
                    if (rule.RequiredChildren == null) continue;
                    foreach (var req in rule.RequiredChildren)
                    {
                        if (string.IsNullOrWhiteSpace(req)) continue;
                        if (go.transform.Find(req) == null)
                            issues.Add(new Issue { severity = Severity.Error, message = $"{go.name}: missing child '{req}'", context = go });
                    }
                }
            }

            return issues;
        }

        public static void LogReport(Object owner, IEnumerable<Issue> issues, string title = "Validate")
        {
            var list = issues?.ToList() ?? new List<Issue>();
            if (list.Count == 0)
            {
                Debug.Log($"[{title}] ✓ No issues found.", owner);
                return;
            }

            int errors = 0, warnings = 0;
            foreach (var i in list)
            {
                var ctx = i.context ? i.context : owner;
                switch (i.severity)
                {
                    case Severity.Error:   errors++;   Debug.LogError(i.message, ctx); break;
                    case Severity.Warning: warnings++; Debug.LogWarning(i.message, ctx); break;
                    default:                              Debug.Log(i.message, ctx); break;
                }
            }
            Debug.Log($"[{title}] → {errors} error(s), {warnings} warning(s).", owner);
        }
    }
}
#endif
