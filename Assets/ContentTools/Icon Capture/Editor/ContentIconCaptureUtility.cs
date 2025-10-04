using System.IO;
using UnityEditor;
using UnityEngine;

namespace Content_Icon_Capture.Editor
{
    public static class ContentIconCaptureUtility
    {

        public enum ImageType
        {
            PNG,
            JPG,
            TGA
        }
        
        public static void CaptureAndSaveIcon(string outputPath, Camera captureCamera, int renderSize, int outputSize, ImageType imageType)
        {
            if (captureCamera == null)
            {
                Debug.LogError("[ContentIconCaptureUtility] CaptureAndSaveDepthImage: No capture camera provided.");
                return;
            }

            // Step 1: Set up the render texture for capturing depth
            var squareRenderTexture = new RenderTexture(renderSize, renderSize, 24, RenderTextureFormat.ARGB32);
            captureCamera.targetTexture = squareRenderTexture;
            captureCamera.depthTextureMode = DepthTextureMode.Depth;
            captureCamera.Render();

            // Step 2: Create a texture from the render texture to capture depth data
            var squareTexture = new Texture2D(renderSize, renderSize, TextureFormat.RGBA32, false);
            RenderTexture.active = squareRenderTexture;
            squareTexture.ReadPixels(new Rect(0, 0, renderSize, renderSize), 0, 0);
            squareTexture.Apply();
            RenderTexture.active = null;
            captureCamera.targetTexture = null;
            squareRenderTexture.Release();

            // Step 3: Scale the captured texture to the desired output size
            var scaledRenderTexture = new RenderTexture(outputSize, outputSize, 24, RenderTextureFormat.ARGB32);
            Graphics.Blit(squareTexture, scaledRenderTexture);
            var scaledTexture = new Texture2D(outputSize, outputSize, TextureFormat.RGBA32, false);
            RenderTexture.active = scaledRenderTexture;
            scaledTexture.ReadPixels(new Rect(0, 0, outputSize, outputSize), 0, 0);
            scaledTexture.Apply();
            RenderTexture.active = null;
            scaledRenderTexture.Release();

            // Step 4: Save the scaled texture as a PNG to the specified output path
            try
            {
                outputPath = outputPath + "." + imageType.ToString().ToLower();
                
                if (imageType == ImageType.JPG)
                {
                    File.WriteAllBytes(outputPath, scaledTexture.EncodeToJPG());
                }
                else if (imageType == ImageType.PNG)
                {
                    File.WriteAllBytes(outputPath, scaledTexture.EncodeToPNG());
                }
                else  if(imageType == ImageType.TGA)
                {
                    File.WriteAllBytes(outputPath, scaledTexture.EncodeToTGA());
                }
                
                
                Debug.Log($"[ContentIconCaptureUtility] Depth image saved to: {outputPath}");
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ContentIconCaptureUtility] Failed to save depth image: {ex.Message}");
            }

            ApplyIconImportSettings(outputPath);
            
            // Step 5: Cleanup
            Object.DestroyImmediate(squareTexture);
            Object.DestroyImmediate(scaledTexture);
        }

        /// <summary>
        /// Ensures the output directory exists.
        /// </summary>
        /// <param name="directory">The directory path to verify or create.</param>
        /// <returns>True if the directory exists or was created successfully, false otherwise.</returns>
        public static bool PrepareDirectory(string directory)
        {
            if (Directory.Exists(directory)) return true;
            try
            {
                Directory.CreateDirectory(directory);
                return true;
            }
            catch (System.Exception ex)
            {
                Debug.LogError($"[ContentIconCaptureUtility] Failed to create directory '{directory}': {ex.Message}");
                return false;
            }
        }
        
#if UNITY_EDITOR
        private static void ApplyIconImportSettings(string absoluteOrAssetPath)
        {
            // Convert absolute path -> project-relative "Assets/..." if needed
            string assetPath = absoluteOrAssetPath;
            if (System.IO.Path.IsPathRooted(assetPath))
            {
                var dataPath = Application.dataPath.Replace('\\','/');
                assetPath = assetPath.Replace('\\','/');

                if (assetPath.StartsWith(dataPath))
                    assetPath = "Assets" + assetPath.Substring(dataPath.Length);
            }

            // Make sure Unity knows about the file
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = AssetImporter.GetAtPath(assetPath) as TextureImporter;
            if (importer == null)
            {
                Debug.LogWarning($"[ContentIconCaptureUtility] Importer not found for {assetPath}");
                return;
            }

            // Texture Type: 2D and UI (Sprite)
            importer.textureType = TextureImporterType.Sprite;

            // Max texture size: 256
            importer.maxTextureSize = 256;

            // Optional sensible defaults for crisp UI icons
            importer.mipmapEnabled = false;
            importer.textureCompression = TextureImporterCompression.Uncompressed; // keep icons sharp

            importer.SaveAndReimport();
        }
#endif

    }
}