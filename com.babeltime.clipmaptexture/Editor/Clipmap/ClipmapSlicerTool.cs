using UnityEngine;
using UnityEditor;
using System.IO;

public class ClipmapSlicerTool : ScriptableWizard
{
    [Header("Input Settings")]
    public Texture2D sourceTexture; // 输入的 10240x10240 纹理;

    [Header("Output Settings")]
    public string outputDirectory = "Assets/Resources/ClipmapData"; // 输出路径; 
    public string filePrefix = "ClipmapTex"; // 文件名前缀; 

    [Header("Configuration")]
    public int baseSize = 10240;      // Mip0 尺寸;
    public int blockSize = 160;       // 单个Block尺寸;
    public int totalMips = 5;         // 生成多少级Mip (0-4);

    // 默认输出格式，RGBA32兼容性最好。如果是纯高度图想省内存，可改为 R16 (需要源图支持或shader配合);
    public TextureFormat exportFormat = TextureFormat.RGBA32; 

    [MenuItem("Tools/Clipmap/Texture Slicer")]
    static void CreateWizard()
    {
        ScriptableWizard.DisplayWizard<ClipmapSlicerTool>("Slice Clipmap texture", "Generate and Slice");
    }

    void OnWizardUpdate()
    {
        helpString = "Input texture must be readable (or not, since we use Blit). \nTarget Mip4 will be 640x640.";
        isValid = sourceTexture != null;
    }

    void OnWizardCreate()
    {
        if (sourceTexture.width != baseSize || sourceTexture.height != baseSize)
        {
            if (!EditorUtility.DisplayDialog("Warning",
                $"Source texture size is {sourceTexture.width}x{sourceTexture.height}, but expected {baseSize}x{baseSize}. Continue?", "Yes", "Cancel"))
            {
                return;
            }
        }

        SliceAndExport();
    }

    void SliceAndExport()
    {
        if (!Directory.Exists(outputDirectory))
        {
            //Directory.CreateDirectory(outputDirectory);
            Debug.LogError(string.Format($"{outputDirectory} not found"));
            return;
        }

        // 确保不被伽马校正干扰数据，使用 Linear 模式的 RenderTexture;
        // 如果是法线或颜色贴图，可能需要视情况调整;
        RenderTextureDescriptor rtDesc = new RenderTextureDescriptor(baseSize, baseSize, RenderTextureFormat.ARGB32, 0);
        rtDesc.sRGB = false;
        rtDesc.useMipMap = false;
        rtDesc.autoGenerateMips = false;

        //float progress = 0f;

        try
        {
            for (int mip = 0; mip < totalMips; mip++)
            {
                int currentSize = baseSize >> mip; // 10240, 5120, 2560, 1280, 640
                int gridCount = currentSize / blockSize; // Block 的行列数;

                // 创建临时的 RenderTexture 用于降采样;
                RenderTexture mipRT = RenderTexture.GetTemporary(currentSize, currentSize, 0, RenderTextureFormat.ARGB32, RenderTextureReadWrite.Linear);
                mipRT.filterMode = FilterMode.Bilinear; // 降采样使用双线性过滤;

                // 填充数据 (降采样);
                // 如果是 Mip0，直接Blit源图；如果是其他Mip，Blit也是源图自动缩放;
                // (更精确的做法是逐级Blit，但源图Blit对于高质量源图通常足够);
                Graphics.Blit(sourceTexture, mipRT);

                // 准备切片读取;
                RenderTexture.active = mipRT;

                // 用于读取单个Block的小纹理;
                Texture2D tileTex = new Texture2D(blockSize, blockSize, exportFormat, false);

                for (int y = 0; y < gridCount; y++)
                {
                    for (int x = 0; x < gridCount; x++)
                    {
                        // 显示进度条;
                        float step = (float)(mip * baseSize + y * gridCount + x) / (totalMips * baseSize); // 估算进度
                        EditorUtility.DisplayProgressBar("Slicing Clipmap", $"Processing Mip {mip}: Block _{y}_{x}, app_path:{Application.dataPath}", step);

                        // 读取像素 (ReadPixels 使用左下角为 0,0，这正是我们需要的);
                        // Rect: x, y, width, height
                        tileTex.ReadPixels(new Rect(x * blockSize, y * blockSize, blockSize, blockSize), 0, 0);
                        tileTex.Apply();

                        // 获取原始字节;
                        byte[] bytes = tileTex.GetRawTextureData();

                        // 写入文件;
                        // 命名格式: heightmap_{row}_{col}_{mip};
                        // 这里 y 是 row (从下往上), x 是 col (从左往右);
                        string filename = $"{filePrefix}_{y}_{x}_{mip}.bytes";
                        string path = Path.Combine(outputDirectory, filename);

                        File.WriteAllBytes(path, bytes);
                    }
                }

                // 清理当前Mip资源;
                RenderTexture.active = null;
                RenderTexture.ReleaseTemporary(mipRT);
                DestroyImmediate(tileTex);
            }
        }
        catch (System.Exception e)
        {
            Debug.LogError($"Slicing failed: {e.Message}");
        }
        finally
        {
            RenderTexture.active = null;
            EditorUtility.ClearProgressBar();
            AssetDatabase.Refresh();
        }

        Debug.Log($"Clipmap slicing complete! Output to: {outputDirectory}");
    }
}