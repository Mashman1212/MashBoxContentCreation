#if UNITY_EDITOR

using UnityEditor.Rendering.HighDefinition;
using UnityEditor;
using UnityEditor.Rendering;
using UnityEngine;

namespace MGShaders.HDRP.Lit.Griptape.Editor.EditorGui
{
    class GriptapeExtraSurfaceInputsUiBlock : MaterialUIBlock
    {
        ExpandableBit   foldoutBit;

        MaterialProperty wearAlphaCutMap;
        MaterialProperty patternAlphaCutMap;  
        MaterialProperty edgeWearColor;  
        MaterialProperty edgeWearHighlight;  
        MaterialProperty edgeWear;  
        MaterialProperty dirtColor;  
        MaterialProperty gripTapeDirt;  
 
    
        public GriptapeExtraSurfaceInputsUiBlock(ExpandableBit expandableBit)
        {
            foldoutBit = expandableBit;
        }

        public override void LoadMaterialProperties()
        {
            wearAlphaCutMap  = FindProperty("_WearAlphaCutMap");
            patternAlphaCutMap  = FindProperty("_PatternAlphaCutMap");
            edgeWearColor  = FindProperty("_EdgeWearColor");
            edgeWearHighlight  = FindProperty("_EdgeWearHighlight");
            edgeWear  = FindProperty("_EdgeWear");
            dirtColor  = FindProperty("_DirtColor");
            gripTapeDirt  = FindProperty("_GripTapeDirt");

        }

        public override void OnGUI()
        {
            using (var header = new MaterialHeaderScope("Griptape", (uint)foldoutBit, materialEditor))
            {
                if (header.expanded)
                {
                    
                    materialEditor.TexturePropertySingleLine(new GUIContent("Wear Alpha Map"), wearAlphaCutMap);
                    materialEditor.TexturePropertySingleLine(new GUIContent("Pattern Alpha Map"), patternAlphaCutMap);
                    //materialEditor.ShaderProperty(wearAlphaCutMap, "Wear AlphaCut Map");
                    //materialEditor.ShaderProperty(patternAlphaCutMap, "Pattern AlphaCut Map");
                    materialEditor.ShaderProperty(edgeWearColor, "Edge Wear Color");
                    materialEditor.ShaderProperty(edgeWearHighlight, "Edge Wear Highlight");
                    materialEditor.ShaderProperty(edgeWear, "Edge Wear Power");
                    materialEditor.ShaderProperty(dirtColor, "Dirt Color");
                    //materialEditor.ShaderProperty(gripTapeDirt, "Griptape Dirt");
                    materialEditor.TexturePropertySingleLine(new GUIContent("Griptape Dirt Map"), gripTapeDirt);


                    
                }
            }
        }
    }
}


#endif