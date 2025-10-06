#if UNITY_EDITOR

using UnityEditor;
using UnityEngine;
using UnityEditor.Rendering;
using UnityEditor.Rendering.HighDefinition;
namespace MGShaders.HDRP.Lit.Griptape.Editor.EditorGui
{
    class GriptapeGUISurfaceInputsUiBlock : MaterialUIBlock
    {
        ExpandableBit   foldoutBit;

        MaterialProperty baseColor;
        MaterialProperty baseMap;
        MaterialProperty MaskMap;
        MaterialProperty normalMap;
        MaterialProperty normalStrength;
        MaterialProperty metallicRemap;
        MaterialProperty smoothnessRemap; 
        MaterialProperty aoRemap;

            internal class Styles
            {

                public static GUIContent normalMapText = new GUIContent("Normal Map", "Specifies the Normal Map for this Material (BC7/BC5/DXT5(nm)) and controls its strength.");
                public static GUIContent maskMapSText = new GUIContent("Mask Map", "Specifies the Mask Map for this Material - Metallic (R), Ambient occlusion (G), Detail mask (B), Smoothness (A).");
                public static GUIContent baseColorText = new GUIContent("Base Map", "Specifies the base color (RGB) and opacity (A) of the Material.");
                public static GUIContent metallicRemappingText = new GUIContent("Metallic Remapping", "Controls a remap for the metallic channel in the Mask Map.");
                public static GUIContent smoothnessRemappingText = new GUIContent("Smoothness Remapping", "Controls a remap for the smoothness channel in the Mask Map.");
                public static GUIContent aoRemappingText = new GUIContent("Ambient Occlusion Remapping", "Controls a remap for the ambient occlusion channel in the Mask Map.");

            }


        public GriptapeGUISurfaceInputsUiBlock(ExpandableBit expandableBit)
        {
            foldoutBit = expandableBit;
        }
        
        public override void LoadMaterialProperties()
        {
            baseColor = FindProperty("_BaseColor");
            baseMap = FindProperty("_BaseColorMap");
            MaskMap = FindProperty("_MaskMap");
            normalMap = FindProperty("_NormalMap");
            normalStrength = FindProperty("_NormalStrength");
            smoothnessRemap = FindProperty("_SmoothnessRemap");
            metallicRemap = FindProperty("_MetallicRemap");
            aoRemap = FindProperty("_AORemap");
        }

        public override void OnGUI()
        {
            using (var header = new MaterialHeaderScope("Surface Inputs", (uint)foldoutBit, materialEditor))
            {
                if (header.expanded)
                {
                    materialEditor.TexturePropertySingleLine(Styles.baseColorText, baseMap, baseColor );

                
                    materialEditor.TexturePropertySingleLine(Styles.maskMapSText, MaskMap);

                    materialEditor.TexturePropertySingleLine(Styles.normalMapText, normalMap, normalStrength);

                    
                    materialEditor.MinMaxShaderProperty(metallicRemap, 0.0f, 1.0f, Styles.metallicRemappingText);
                    materialEditor.MinMaxShaderProperty(smoothnessRemap, 0.0f, 1.0f, Styles.smoothnessRemappingText);
                    materialEditor.MinMaxShaderProperty(aoRemap, 0.0f, 1.0f, Styles.aoRemappingText);



                    
                }
            }
        }
    }
}

#endif