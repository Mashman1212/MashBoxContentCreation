#if UNITY_EDITOR
using ContentTools.Editor;
using UnityEditor;
using UnityEngine;

namespace ContentTools
{
    public static class ContentValidationRulesBootstrap
    {
        [MenuItem("Tools/MashBox/Content/Create Prefilled Validation Rules")]
        public static void CreatePrefilledRules()
        {
            var path = EditorUtility.SaveFilePanelInProject(
                "Create Validation Rules",
                "ContentValidationRules",
                "asset",
                "Choose where to save the rules asset");

            if (string.IsNullOrEmpty(path)) return;

            var asset = ScriptableObject.CreateInstance<ContentValidationRules>();

            asset.SuperTypes = new[] { "Scooter", "BMX" };

            asset.AllowedPairs.Add(new ContentValidationRules.SuperTypeTypes
            {
                SuperType = "Scooter",
                Types = new[]
                {
                    "Deck","Bars","Clamp","Forks","Headset","Griptape","Wheel","Urethane","Bar End" // :contentReference[oaicite:2]{index=2}
                }
            });

            asset.AllowedPairs.Add(new ContentValidationRules.SuperTypeTypes
            {
                SuperType = "BMX",
                Types = new[]
                {
                    "Frame","Bars","Bar End","Forks","Headset","BB","Chain","CrankArm","Front Hub",
                    "Rear Hub","Grip","Hub Guard","Mag","Nipples","Pedal","Peg","Rim","Seat","Seat Clamp",
                    "Seat Post","Spokes","Sprocket","Stem","Stem Bolt","Stem Cap","Tire","Valve Cap" // :contentReference[oaicite:3]{index=3}
                }
            });

            // Optional: seed some common colors (edit as you like)
            asset.Colors = new[] { "Black", "Blue", "Red", "Green", "Vanilla_Bean", "Raw", "Chrome" };

            AssetDatabase.CreateAsset(asset, path);
            AssetDatabase.SaveAssets();
            EditorGUIUtility.PingObject(asset);
            Selection.activeObject = asset;

            Debug.Log("Created prefilled ContentValidationRules with Scooter + BMX Type pairs.");
        }
    }
}
#endif

