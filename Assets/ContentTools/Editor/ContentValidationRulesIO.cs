#if UNITY_EDITOR
using System.IO;
using UnityEditor;
using UnityEngine;
using System;
using System.Linq;

namespace ContentTools.Editor
{
    /// <summary>
    /// Import/Export JSON for ContentValidationRules.
    /// Adds menu items:
    ///  • Tools/MashBox/Content/Export Validation Rules to JSON
    ///  • Tools/MashBox/Content/Import Validation Rules from JSON
    /// </summary>
    public static class ContentValidationRulesIO
    {
        // ---------- Plain-serializable DTOs (mirror your ScriptableObject types) ----------
        [Serializable]
        private class RulesData
        {
            public string[] SuperTypes;
            public string[] Colors;
            public SuperTypeTypesData[] AllowedPairs;
            public AnchorRuleData[] AnchorRules;

            [Serializable]
            public class SuperTypeTypesData
            {
                public string SuperType;
                public string[] Types;
            }

            [Serializable]
            public class ChildPatternData
            {
                public string PathPrefix;
                public string NameRegex;
                public int    Min = 1;
                public int    Max = int.MaxValue;
                public bool   DirectChildrenOnly = true;
            }

            [Serializable]
            public class AnchorRuleData
            {
                public string AppliesToSuperType;
                public string AppliesToType;
                public string AppliesToBrand;
                public string[] RequiredChildren;
                public ChildPatternData[] RequiredPatterns;
                public bool ForbidUnexpectedChildren;
            }

            // -------- Conversion helpers --------
            public static RulesData From(ContentValidationRules src)
            {
                var d = new RulesData
                {
                    SuperTypes = src.SuperTypes,
                    Colors = src.Colors,
                    AllowedPairs = src.AllowedPairs?.Select(p => new SuperTypeTypesData
                    {
                        SuperType = p.SuperType,
                        Types = p.Types
                    }).ToArray(),
                    AnchorRules = src.AnchorRules?.Select(r => new AnchorRuleData
                    {
                        AppliesToSuperType = r.AppliesToSuperType,
                        AppliesToType      = r.AppliesToType,
                        AppliesToBrand     = r.AppliesToBrand,
                        RequiredChildren   = r.RequiredChildren,
                        RequiredPatterns   = r.RequiredPatterns?.Select(cp => new ChildPatternData
                        {
                            PathPrefix         = cp.PathPrefix,
                            NameRegex          = cp.NameRegex,
                            Min                = cp.Min,
                            Max                = cp.Max,
                            DirectChildrenOnly = cp.DirectChildrenOnly
                        }).ToArray(),
                    }).ToArray()
                };
                return d;
            }

            public void ApplyTo(ContentValidationRules dst)
            {
                dst.SuperTypes = SuperTypes ?? Array.Empty<string>();
                dst.Colors     = Colors     ?? Array.Empty<string>();

                dst.AllowedPairs = (AllowedPairs ?? Array.Empty<SuperTypeTypesData>()).Select(p =>
                {
                    var t = new ContentValidationRules.SuperTypeTypes();
                    t.SuperType = p.SuperType;
                    t.Types     = p.Types ?? Array.Empty<string>();
                    return t;
                }).ToList();

                dst.AnchorRules = (AnchorRules ?? Array.Empty<AnchorRuleData>()).Select(a =>
                {
                    var ar = new ContentValidationRules.AnchorRule();
                    ar.AppliesToSuperType = a.AppliesToSuperType;
                    ar.AppliesToType      = a.AppliesToType;
                    ar.AppliesToBrand     = a.AppliesToBrand;
                    ar.RequiredChildren   = a.RequiredChildren ?? Array.Empty<string>();

                    // Patterns
                    ar.RequiredPatterns = (a.RequiredPatterns ?? Array.Empty<ChildPatternData>()).Select(cp =>
                    {
                        var x = new ContentValidationRules.ChildPattern();
                        x.PathPrefix         = cp.PathPrefix;
                        x.NameRegex          = cp.NameRegex;
                        x.Min                = cp.Min;
                        x.Max                = cp.Max <= 0 ? int.MaxValue : cp.Max;
                        x.DirectChildrenOnly = cp.DirectChildrenOnly;
                        return x;
                    }).ToArray();
                    
                    return ar;
                }).ToList();
            }
        }

        // ---------- Menu: Export ----------
        [MenuItem("Tools/MashBox/Content/Export Validation Rules to JSON")]
        private static void ExportRulesToJson()
        {
            var rules = Selection.activeObject as ContentValidationRules;
            if (rules == null)
            {
                // Try auto-find first available rules asset
                var guids = AssetDatabase.FindAssets("t:ContentValidationRules");
                if (guids.Length > 0)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guids[0]);
                    rules = AssetDatabase.LoadAssetAtPath<ContentValidationRules>(path);
                }
            }

            if (rules == null)
            {
                EditorUtility.DisplayDialog("Export Rules", "Select a ContentValidationRules asset first.", "OK");
                return;
            }

            var savePath = EditorUtility.SaveFilePanel(
                "Export Validation Rules to JSON",
                Application.dataPath,
                rules.name,
                "json");
            if (string.IsNullOrEmpty(savePath)) return;

            var data = RulesData.From(rules);
            var json = JsonUtility.ToJson(data, true);
            File.WriteAllText(savePath, json);
            EditorUtility.RevealInFinder(savePath);
            Debug.Log($"[ContentValidationRulesIO] Exported rules to:\n{savePath}", rules);
        }

        // ---------- Menu: Import ----------
        [MenuItem("Tools/MashBox/Content/Import Validation Rules from JSON")]
        private static void ImportRulesFromJson()
        {
            var openPath = EditorUtility.OpenFilePanel("Import Validation Rules (JSON)", Application.dataPath, "json");
            if (string.IsNullOrEmpty(openPath)) return;

            string json;
            try { json = File.ReadAllText(openPath); }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Import Rules", $"Could not read file:\n{ex.Message}", "OK");
                return;
            }

            RulesData data;
            try { data = JsonUtility.FromJson<RulesData>(json); }
            catch (System.Exception ex)
            {
                EditorUtility.DisplayDialog("Import Rules", $"Invalid JSON:\n{ex.Message}", "OK");
                return;
            }

            // Ask where to save the asset
            var dstPath = EditorUtility.SaveFilePanelInProject(
                "Create Validation Rules asset",
                "ContentValidationRules",
                "asset",
                "Choose where to save the imported rules asset");
            if (string.IsNullOrEmpty(dstPath)) return;

            var asset = ScriptableObject.CreateInstance<ContentValidationRules>();
            data.ApplyTo(asset);

            AssetDatabase.CreateAsset(asset, dstPath);
            AssetDatabase.SaveAssets();
            Selection.activeObject = asset;
            EditorGUIUtility.PingObject(asset);

            Debug.Log($"[ContentValidationRulesIO] Imported rules from JSON:\n{openPath}\n→ {dstPath}", asset);
        }
    }
}
#endif
