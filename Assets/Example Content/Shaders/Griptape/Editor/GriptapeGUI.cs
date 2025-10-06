#if UNITY_EDITOR
using UnityEditor.Rendering.HighDefinition;
namespace MGShaders.HDRP.Lit.Griptape.Editor.EditorGui
{
    public class GriptapeGUI : LightingShaderGraphGUI
    {

        

        public GriptapeGUI()
        {

            // Remove the ShaderGraphUIBlock to avoid having duplicated properties in the UI.
            uiBlocks.RemoveAll(b => b is ShaderGraphUIBlock);

            // Add our own stuff
            uiBlocks.Insert(1, new GriptapeGUISurfaceInputsUiBlock(MaterialUIBlock.ExpandableBit.Input));
            
           uiBlocks.Insert(2, new GriptapeExtraSurfaceInputsUiBlock(MaterialUIBlock.ExpandableBit.User0));
            
    

        }


    }
}

#endif