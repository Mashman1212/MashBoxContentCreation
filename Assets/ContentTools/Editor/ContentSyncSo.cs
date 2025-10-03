using System.IO;
using UnityEditor;
using UnityEngine;
namespace ContentTools
{
    #if UNITY_EDITOR
    [CreateAssetMenu(fileName = "ContentSyncSO", menuName = "ScriptableObjects/ContentSyncSO", order = 1)]
    public class ContentSyncSo : ScriptableObject
    {
        public enum Type
        {
            FBX,
            PNG,
            JPEG
        }

        [SerializeField] private Type _type = Type.FBX;
        
        [SerializeField]
        //[Sirenix.OdinInspector.FilePath]
        private string _sourceDirectory = "";
        
        public string SourceDirectory { get => _sourceDirectory; set => _sourceDirectory = value; }
        public string DestinationDirectory => Path.GetDirectoryName(AssetDatabase.GetAssetPath(this));



        [ContextMenu("Add Assets To Group")]
        public void Sync()
        {
            SyncContent(_type.ToString().ToLower());
        }
        
        public void SyncContent(string extension)
        {
            Debug.Log("[ContentSyncSo] SyncContent()");
            string unityProjectRootDir = Path.GetPathRoot(Application.dataPath);
            Debug.Log("unityProjectRootDir:" + unityProjectRootDir);
    
            string destinationDirectory = DestinationDirectory;
    
            string sourceDirectory = SourceDirectory;
            sourceDirectory = sourceDirectory.Replace(Path.GetPathRoot(SourceDirectory),unityProjectRootDir);

            Debug.Log("SourceDirectoryPath:" + sourceDirectory);
            Debug.Log("DestinationDirectory:" + destinationDirectory);

            try
            {
                var sourceFiles = Directory.GetFiles(sourceDirectory); // Get all files from the source directory
                foreach (var filePath in sourceFiles) // Iterate over all the files
                {
                    // Get the destinationPath
                    var fileName = Path.GetFileName(filePath);
                    var ext = Path.GetExtension(filePath);
                    var destinationPath = Path.Combine(destinationDirectory, fileName);

                    if (fileName.Contains("_UV") || fileName.Contains("_uv"))
                    {
                        continue;
                    }
                    
                    if (!ext.Contains(extension)) 
                    {
                        Debug.Log($"File {fileName} is not a .{extension} file and will not be copied.");
                        continue;
                    }

                    // Add a check to see if the destination file already exists and is not older than source file
                    if (File.Exists(destinationPath) && File.GetLastWriteTime(filePath) <= File.GetLastWriteTime(destinationPath))
                    {
                        Debug.Log($"File {filePath} has not changed. Skip copying.");
                        continue;
                    }
        
                    // Copy the file only if the source is newer
                    File.Copy(filePath, destinationPath, true);
                    Debug.Log($"File copied from {filePath} to {destinationPath}");
            
                    AssetDatabase.Refresh();
                }
            }
            catch (IOException e)
            {
                Debug.LogError("An error occurred while syncing content: " + e.Message);
            }
        }
    }
    
    #endif
}