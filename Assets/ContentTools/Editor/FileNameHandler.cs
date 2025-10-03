using System.IO;
using UnityEditor;
using UnityEngine;

namespace ContentTools.Editor
{
    public class FileNameHandler : UnityEditor.EditorWindow
    {
        private string find = "_Head_";
        private string replaceWith = "_Face_";

        [MenuItem("Tools/MashBox/Replace File Names")]
        private static void ReplaceNames()
        {
            FileNameHandler window = GetWindow<FileNameHandler>("Replace File Names");
            window.Show();
        }

        private void OnGUI()
        {
            GUILayout.Label("Replace part of file names for selected files", EditorStyles.boldLabel);

            find = EditorGUILayout.TextField("Search For: ", find);
            replaceWith = EditorGUILayout.TextField("Replace With: ", replaceWith);

            if (GUILayout.Button("Replace"))
            {
                RenameSelectedAssets();
            }
        }

        private void RenameSelectedAssets()
        {
            foreach (UnityEngine.Object obj in Selection.objects)
            {
                string assetPath = AssetDatabase.GetAssetPath(obj);
                string directory = Path.GetDirectoryName(assetPath);
                string oldFileName = Path.GetFileName(assetPath);
                if (oldFileName.Contains(find))
                {
                    string newFileName = oldFileName.Replace(find, replaceWith);
                    string newPath = Path.Combine(directory, newFileName);
                    string renameResult = AssetDatabase.RenameAsset(assetPath, newFileName);
                    if (!string.IsNullOrEmpty(renameResult))
                    {
                        Debug.LogWarning("Failed to rename: " + assetPath +", error message: "+ renameResult);
                    }
                    else
                    {
                        AssetDatabase.Refresh();
                    }
                }
            }
        }
    }
}