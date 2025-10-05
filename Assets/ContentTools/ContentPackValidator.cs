#if UNITY_EDITOR
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

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

// ... inside ValidateItem(GameObject go, ContentValidationRules rules, List<Issue> buffer = null)

            if (rules != null)
            {
                bool superKnown = rules.IsAllowed(superType, rules.SuperTypes) ||
                                  rules.AllowedPairs.Exists(p => p.SuperType == superType);

                if (!superKnown)
                    issues.Add(new Issue
                    {
                        severity = Severity.Error, message = $"{go.name}: unknown SuperType '{superType}'", context = go
                    });

                if (!rules.IsAllowedPair(superType, type))
                    issues.Add(new Issue
                    {
                        severity = Severity.Error,
                        message = $"{go.name}: invalid Type '{type}' for SuperType '{superType}'", context = go
                    });

                if (rules.Colors != null && rules.Colors.Length > 0 && !rules.IsAllowed(color, rules.Colors))
                    issues.Add(new Issue
                        { severity = Severity.Error, message = $"{go.name}: unknown Color '{color}'", context = go });

                foreach (var rule in rules.RulesFor(superType, type, brand))
                {
                    // 1) Exact child requirements
                    if (rule.RequiredChildren != null)
                    {
                        foreach (var req in rule.RequiredChildren)
                        {
                            if (string.IsNullOrWhiteSpace(req)) continue;
                            if (go.transform.Find(req) == null)
                                issues.Add(new Issue
                                {
                                    severity = Severity.Error,
                                    message = $"{go.name}: missing child '{req}'",
                                    context = go
                                });
                        }
                    }

                    // 2) Pattern-based requirements (regex + counts)
                    if (rule.RequiredPatterns != null)
                    {
                        foreach (var pat in rule.RequiredPatterns)
                        {
                            if (pat == null || string.IsNullOrEmpty(pat.NameRegex)) continue;

                            // resolve search root for this pattern
                            Transform searchRoot = go.transform;
                            if (!string.IsNullOrEmpty(pat.PathPrefix))
                            {
                                var sub = go.transform.Find(pat.PathPrefix);
                                if (sub == null)
                                {
                                    issues.Add(new Issue
                                    {
                                        severity = Severity.Error,
                                        message =
                                            $"{go.name}: missing subtree '{pat.PathPrefix}' required for pattern '{pat.NameRegex}'",
                                        context = go
                                    });
                                    continue;
                                }

                                searchRoot = sub;
                            }

                            IEnumerable<Transform> candidates = pat.DirectChildrenOnly
                                ? searchRoot.Cast<Transform>() // immediate children
                                : searchRoot.GetComponentsInChildren<Transform>(true); // recursive
                            if (!pat.DirectChildrenOnly)
                                candidates = candidates.Where(t => t != searchRoot);

                            int matches = 0;
                            foreach (var t in candidates)
                                if (Regex.IsMatch(t.name, pat.NameRegex))
                                    matches++;

                            int min = Math.Max(0, pat.Min);
                            int max = pat.Max <= 0 ? int.MaxValue : pat.Max;
                            if (matches < min || matches > max)
                            {
                                string where = string.IsNullOrEmpty(pat.PathPrefix) ? "<root>" : pat.PathPrefix;
                                issues.Add(new Issue
                                {
                                    severity = Severity.Error,
                                    message =
                                        $"{go.name}: expected {min}..{(max == int.MaxValue ? "∞" : max.ToString())} child(ren) matching /{pat.NameRegex}/ under {where}, found {matches}.",
                                    context = go
                                });
                            }
                        }
                    }

                    // 3) Forbid unexpected children under validated scope(s)
                    if (1 == 1)
                    {
                        // Build a quick look-up of exact required names (root-relative)
                        var requiredExact = new HashSet<string>(rule.RequiredChildren ?? Array.Empty<string>());

                        // Group patterns by scope so we can evaluate “extras” once per scope.
                        // Key = (PathPrefix, DirectChildrenOnly)
                        var scopeMap = new Dictionary<(string path, bool direct), List<Regex>>();
                        if (rule.RequiredPatterns != null)
                        {
                            foreach (var pat in rule.RequiredPatterns)
                            {
                                if (pat == null || string.IsNullOrEmpty(pat.NameRegex)) continue;
                                var key = (pat.PathPrefix ?? string.Empty, pat.DirectChildrenOnly);
                                if (!scopeMap.TryGetValue(key, out var list))
                                {
                                    list = new List<Regex>();
                                    scopeMap[key] = list;
                                }
                                list.Add(new Regex(pat.NameRegex));
                            }
                        }

                        // 🔹 Derive scopes from RequiredChildren so nested paths are enforced
                        // e.g., "FrontWheel_Anchor/Front_Left_Peg_Anchor" -> scope "FrontWheel_Anchor" (direct children only)
                        foreach (var req in requiredExact)
                        {
                            var slash = req.LastIndexOf('/');
                            if (slash > 0)
                            {
                                var prefix = req.Substring(0, slash);
                                var key = (prefix, true);
                                if (!scopeMap.ContainsKey(key))
                                    scopeMap[key] = new List<Regex>(); // no patterns; only exact names allowed here
                            }
                        }

                        // Include a default root scope if there are exact root children but no patterns for root
                        if ((rule.RequiredChildren?.Any(s => !s.Contains("/")) ?? false) &&
                            !scopeMap.ContainsKey((string.Empty, true)))
                        {
                            scopeMap[(string.Empty, true)] = new List<Regex>(); // only exact names allowed at root
                        }

                        foreach (var kv in scopeMap)
                        {
                            string pathPrefix = kv.Key.path;
                            bool directOnly = kv.Key.direct;
                            var regexes = kv.Value;

                            // resolve scope root
                            Transform scopeRoot = go.transform;
                            if (!string.IsNullOrEmpty(pathPrefix))
                            {
                                var sub = go.transform.Find(pathPrefix);
                                if (sub == null)
                                {
                                    // Missing subtree is already reported above during pattern checks; skip.
                                    continue;
                                }
                                scopeRoot = sub;
                            }

                            // collect candidates to test for "extra"
                            IEnumerable<Transform> candidates = directOnly
                                ? scopeRoot.Cast<Transform>()
                                : scopeRoot.GetComponentsInChildren<Transform>(true).Where(t => t != scopeRoot);

                            foreach (var t in candidates)
                            {
                                // 1) allow if exact root-relative path is in RequiredChildren
                                string rootRelative = string.IsNullOrEmpty(pathPrefix)
                                    ? t.name
                                    : $"{pathPrefix}/{t.name}";
                                bool allowedByExact = requiredExact.Contains(rootRelative);

                                // 2) allow if it matches ANY pattern for this scope
                                bool allowedByPattern = regexes.Any(rx => rx.IsMatch(t.name));

                                if (!allowedByExact && !allowedByPattern)
                                {
                                    string scope = string.IsNullOrEmpty(pathPrefix) ? "<root>" : pathPrefix;
                                    issues.Add(new Issue
                                    {
                                        severity = Severity.Error,
                                        message = $"{go.name}: unexpected child '{rootRelative}' under {scope}.",
                                        context = t.gameObject
                                    });
                                }
                            }
                        }
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
