# Clipmap Texture

### (1)概要 


这是一个基于Unity的Clipmap（裁剪贴图）技术的工程化实现。目的是为了解决超大场景（比如地形或巨型贴图等）的纹理渲染和数据提取问题。在Clipmap的加持下，我们可以将一张分辨率 **10K x 10K**，约**700兆**的png格式纹理在运行时压缩至不到**5兆**，并以相对高效的方式进行采样。Clipmap的基本思路是利用 **Textrue2DArray** 以及多层细节（LOD），让系统仅在热点区域保留最高精度的纹理，并随热点区域向外，逐渐降低纹理精度。Clipmap为提高采样效率，使用了类似环形缓冲区（Toroidal buffer/ Rolling buffer）的概念，使得运行时热点区域变化后，系统能及时以较小代价提交更新。


### (2)运作原理 

形象的说，Clipmap可以被想象成一组“跟随热点(比如主摄像机)移动的同心正方形窗口”，如下图所示：

![Clipmap Texture Overview](https://github.com/bbccyy/clipmaptexture/blob/master/img/A1.png?raw=true)


它具有明显的分层结构（Stack），但是除此之外，其内在还具有环形更新和手动混合等特点。下面逐一解释下：

**首先是金字塔状的分层设计**，Clipmap不会将最高精度的巨型纹理一次性加载到显存（甚至内存中也不会），它会维护一个 Texture2DArray，对应代码中的 `m_ClipmapTex`，其中的每一层（Layer/Slice）代表一个不同的细节等级（Mip Level）。具体来说，`Layer 0 `对应了摄像机脚下相对很小的一块区域，使用巨型纹理中最高等级的清晰度； `Layer 1 `对应了一个面积为上一级4倍，边长为2倍的区域，同样是覆盖在热点区域以及周围。 以此类推，对应的最后一级 `Layer N` 会以一个较低的Mip Level 覆盖原始巨型纹理的全部区域。

如下图所示，蓝色倒立金字塔中的每一片都对应了一个级别的纹理大小，最上面最大的蓝色片对应了最高精度下的巨型纹理全部内容，其需求的资源容量最大，在图中面积也最大，往下每递进一层，蓝色片对应的精度等级将会下降一级，一般而言尺寸缩小一半，容量缩小到上一层的`1/4`。图中依附在蓝色片上的绿色区域就是我们实际上放在Clipmap Textrue中的部分，它们使用相同的分辨率，因此最上层绿色区域能表述的纹理占比是最小的，仅为 `1/ (2^N)`，对应了摄像机附近最小且精度最高的一块区域。随着等级上升，占比最终会来到`1/(2^0) = 1 / 1 = 1`，也就是图中底部的绿色区域，该区域和其对应的蓝色片完全重合，意味着该层级的Mip以给定大小的尺寸能够完全覆盖原始巨型纹理对应的全部区域了。

![Clipmap Texture Pyramid](https://github.com/bbccyy/clipmaptexture/blob/master/img/A2.png?raw=true)

这些层级在空间上是重叠的，摄像机到它们各自的中心点都将在一定比例范围内，这就自然引出了第二个要点“**环形更新**（Toroidal Update / Rolling Buffer）”：

在Clipmap系统重，摄像机移动时，物理显存中的纹理并不会整体移动。而是采用了增量加载配合循环利用的方式来为纹理更新减负。所谓增量加载，既系统在检测到摄像机位移后，通过计算新进入视野的区域（代码中的 `CalcBestLDCornerOfMip`），达到只加载这部分新的“图块（Tile/Block）”的目的，而不用全量加载和拷贝整个Layer。所谓的循环利用，本质是让旧的、移出视野的图块对应的内存中的位置，被新图块以覆盖，这在内存中表现为一个类似于“环形缓冲”的样式。最后在Shader采样时，由于物理纹理是环形的，Shader 利用 `TextureWrapMode.Repeat` 和 UV 偏移量（`LayerAnchor`）来正确采样，使得在视觉上纹理看起来是连续无限的。


![Layer update](https://github.com/bbccyy/clipmaptexture/blob/master/img/A3.png?raw=true)


如上图，我们把一层Layer细分为 `4 X 4 = 16` 个Block，当Layer中心点开始向右上方偏移后，在**Step 0**中的黄色部分作为新增纹理，将会安装如 **Step 1**中箭头的方式替换 **Step 0** 中浅蓝色区域（移出视野的区域）。形成环形（首尾相接）的形式。

最后是**手动混合**（Manual Blending），简单来说就是GPU端采样Clipmap时不再需要计算 `ddx` 和 `ddy`的偏导求解 Mipmap Leval，而是通过当前像素点对应在巨型纹理中的全局UV，以及各个Layer的覆盖区域（由锚点`anchor`指明），来计算最佳采样Layer等级（参考示例shader代码中的`CalcBestMipOfClipmap`采样方法）。换句话说，手动混合通过判断当前像素落在哪一层有效范围内，优先选择最高清晰度的一层。


### (3)工程特性 

* 理论上支持无限大纹理
    - 支持远超 GPU 显存限制的虚拟纹理尺寸（如 10240 x 10240 或更大）。
* 增量更新
    - 当视口移动时，仅计算并加载新进入视野的图块（Block），而非重绘整个纹理。
    - 支持自定义构成Layer的Block尺寸，支持4 X 4， 5 X 5 等规格的GridSize。
* 异步加载管线
    - 支持自定义 IClipmapAssetLoader 接口实现异步读取（本地文件或网络请求）。
    - 使用 SynchronizationContext 确保数据回调并在主线程安全上传。
* 其他性能优化
    - LRU 缓存 -> 定期自动修剪旧的图块请求，控制内存总占用率。
    - 分帧上传 -> 通过 MaxUploadsPerFrame 限制每帧向 GPU 上传的图块数量，防止掉帧。
    - Dummy 填充 -> 对于越界（Out Of Boundary）区域自动填充黑色 Dummy 纹理。
    - Shader 友好 -> 自动计算并传递层级锚点（Anchor）信息，便于 Shader 进行 UV 偏移采样。


### (4)系统结构 

#### 4.1 核心组件

* Clipmap
    * 主控制器
    * 负责更新和计算视口（Probe）位置
    * 负责调度层级更新
    * 管理加载队列
    * 提供绑定Shader参数的接口 
* ClipmapLayer
    * 对应Clipmap Texture中的一个层级（Layer）
    * 管理和更新Layer锚点
    * 维护Block状态，维护生命周期（LRU）
* TileRequest
    * 基础数据单元
    * 描述一个具体Layer下的一个具体Block
    * 定义Block的状态（请求加载/加载中/上传中/使用中）
* Texture2DArray
    * 所有层级的纹理的容器 
    * 注意Mali的某些GPU型号可能对此数据结构支持不佳 

#### 4.2 核心参数

| Name   | Description | Example     |
|--------|------|----------|
| ClipmapName   | 定义了纹理名称，会在Shader和Asset中使用。   | ClipmapTex     |
| DataPath   | 资源路径。  | Assets/Resources/ClipmapData     |
| TextureSize   | 对应Texture2DArray中每层Layer的尺寸。   | 640     |
| BlockSize   | 对应每层Layer的细分Block尺寸。如160对应了一张Layer可以等分为 4 X 4 = 16 Block。   | 160     |
| StackDepth   | 对应Texture2DArray的第三维长度，对应了定义了多少个Mip Level层级。   | 5     |
| WorldSize   | Clipmap所表述的巨型纹理的原始分辨率。   | 10240      |
| UpdateThreshold   | 触发更新的阈值系数，当热点的UV坐标到Layer中心点的UV坐标的距离超过了该参数倍标准UV距离(基准等于一个Block的UV距离)，则触发新一轮的Layer增量更新。建议(0.5 ~ 1.0)   | 0.75     |
| Format   | Clipmap Texture 纹理格式。   | ARGB32      |
| {ClipmapName}_LayerAnchor[mip]   | 由Clipmap主逻辑绑定到Shader中的各层锚点参数：[xy：当前层级左下角的Global UV Anchor， z: Layer UV Length, w：Texel UV Size]   | [略]     |

#### 4.3 文件命名规范

文件以ClipmapTexture要求的最小更新单位（Block）存放。每个Block的纹理被编码成raw data的形式，后缀对应“.bytes”。当然，文件的格式视情况可由项目自由修改。

文件路径需要满足如下要求：

```
{dataPath}/{clipmapName}_{globalY}_{globalX}_{mip}.bytes
```

其中`dataPath`和`clipmapName`参考4.1核心参数；`globalX`，`globalY`是该`mip`下，当前Block在全局的**列**和**行**的索引坐标，注意索引坐标起始位置在全局纹理的左下角，**0-based**。而`mip`就是当前的mip层级，如果`StackDepth = 5`， 则mip需要时`{0,1,2,3,4}`中的一个，`mip=0`对应精度最高的等级。



### (5)快速开始

##### 5.1 资源加载器

首先，必须实现 `IClipmapAssetLoader` 接口，我们需要它读取原始纹理数据（Block）。

```C#
using Babeltime.Clipmap;
using System;
using System.IO;
using System.Threading.Tasks;

public class ExampleProjectAssetLoader : IClipmapAssetLoader
{
    public void LoadRawData(string aAssetPath, Action<byte[]> onComplete)
    {
        // 本方法主要由项目应用方接管，使用项目自己的AssetLoaderMgr;
        // 这里简单模拟异步加载 -> 回调;
        // string path = $"Assets/Res/ClipmapTex_{y}_{x}_{mip}.bytes";
        //  -> 使用 Task 在线程池中执行 IO 操作，避免阻塞主线程;
        Task.Run(() =>
        {
            try
            {
                if (File.Exists(aAssetPath))
                {
                    byte[] fileData = File.ReadAllBytes(aAssetPath);
                    onComplete?.Invoke(fileData);
                }
                else
                {
                    Debug.LogError($"[Loader] File not found: {aAssetPath}");
                    onComplete?.Invoke(null);
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"[Loader] Error loading {aAssetPath}:  {ex.Message}");
                onComplete?.Invoke(null);
            }
        });
    }
}
```

##### 5.2 初始化Clipmap

```C#
// 配置参数 
ClipmapParams createParams = new ClipmapParams(){ 
    clipmapName = "ClipmapTex", // Shader 属性名 
    dataPath = Application.streamingAssetsPath + "/TerrainTiles", 
    textureSize = 640,          // Clipmap 物理纹理大小 
    blockSize = 160,            // 单个图块大小 
    stackDepth = 5,             // Mip 层级数 
    worldSize = 10240,          // 虚拟世界总尺寸 
    updateThreshold = 0.75f,    // 更新阈值 
    format = TextureFormat.ARGB32 
}; 
// 创建 Clipmap 
_clipmap = new Clipmap(createParams, new ExampleProjectAssetLoader()); 
_clipmap.Initialize(); 
```

##### 5.3 每帧更新 （以ScriptableRenderPass为例）

```C#
public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
{
    CommandBuffer cmd = CommandBufferPool.Get("UpdateClipmap");

    //calc probe into UV scale
    //获取摄像机或者探针的具体位置，将其转化到 Clipmap Texture 下的全局 UV 坐标 (0~1)
    //坐标归一化 
    CalcProbeUV(ref probeUV);

    _clipmap.Update(probeUV, cmd);  //clipmap update main logic

    _clipmap.BindShaderParams(Mat); //bind texture and params to material

    context.ExecuteCommandBuffer(cmd);

    CommandBufferPool.Release(cmd);
}
```

##### 5.4 销毁

```C#
void OnDestroy(){
   _clipmap?.Dispose();
}
```


### (6)Shader 集成方案 

##### 6.1 必要参数

以总共5层(`StackDepth`)，纹理名(`ClipmapName`)为“ClipmapTex”为例，Shader中需要如下定义：

```java
float4 ClipmapTex_LayerAnchor[5];

TEXTURE2D_ARRAY(ClipmapTex);
SAMPLER(sampler_ClipmapTex);
```

Clipmap 系统会向 Material传递的这两个属性
* Texture2DArray 
    * 会分帧增量更新 
    * 每次更新的最小单位是隶属于一个Layer中的若干个Block 
* `{ClipmapTexName}_LayerAnchor[mip]`
    * 由Clipmap主逻辑绑定到Shader中的各层锚点参数 
    * xy：当前层级左下角的Global UV Anchor 
    * z：Layer UV Length 
    * w：Texel UV Size 


##### 6.2 采样示例
```java
half4 Frag (Varyings input) : SV_Target
{
    //调用提供的接口，输入Clipmap Texture期望的全局UV坐标，计算最合适的Mip level 
    int bestMip = CalcBestMipOfClipmap5(input.uv);

    //Rolling buffer核心采样uv计算逻辑，需要配合纹理Wrap模式为“Repeat”
    float2 sampleUV = input.uv / ClipmapTex_LayerAnchor[bestMip].zz;    //rolling buffer mechanism

    //提供正确的采样uv和mip Level，执行Clipmap采样
    half4 col = SAMPLE_TEXTURE2D_ARRAY(ClipmapTex, sampler_ClipmapTex, sampleUV, bestMip).rgba;

    return col;
}
```

补充：方法`CalcBestMipOfClipmap5`是专门针对5层MipLevel设计的，其具体实现如下：

```java
int CalcBestMipOfClipmap5(float2 uv)
{
    //考虑到可能需要采样当前纹素 + 右侧 + 上方等(+1)临近纹素信息;
    //这里对右侧和上方边界做了缩减处理(缩减1个纹素的尺寸);
    //确保所有落在当前Mip等级的采样能获取到符合要求的领域数据;
    int mip = 4;    
    int2 flag = (uv.xy >= ClipmapTex_LayerAnchor[3].xy && uv.xy < (ClipmapTex_LayerAnchor[3].xy + ClipmapTex_LayerAnchor[3].zz - ClipmapTex_LayerAnchor[3].ww)) ? 1 : 0;
    mip = mip - flag.x * flag.y;
    flag = (uv.xy >= ClipmapTex_LayerAnchor[2].xy && uv.xy < (ClipmapTex_LayerAnchor[2].xy + ClipmapTex_LayerAnchor[2].zz - ClipmapTex_LayerAnchor[2].ww)) ? 1 : 0;
    mip = mip - flag.x * flag.y;
    flag = (uv.xy >= ClipmapTex_LayerAnchor[1].xy && uv.xy < (ClipmapTex_LayerAnchor[1].xy + ClipmapTex_LayerAnchor[1].zz - ClipmapTex_LayerAnchor[1].ww)) ? 1 : 0;
    mip = mip - flag.x * flag.y;
    flag = (uv.xy >= ClipmapTex_LayerAnchor[0].xy && uv.xy < (ClipmapTex_LayerAnchor[0].xy + ClipmapTex_LayerAnchor[0].zz - ClipmapTex_LayerAnchor[0].ww)) ? 1 : 0;
    mip = mip - flag.x * flag.y;
    return mip;
}
```

如果追加或者减少`mip level`，需要根据方法实现模式，进行定制修改。例如当 `mip = 3` 时，必须使用如下改进后方法：

```java
int CalcBestMipOfClipmap3(float2 uv)
{
    int mip = 2;    
    int2 flag = (uv.xy >= ClipmapTex_LayerAnchor[1].xy && uv.xy < (ClipmapTex_LayerAnchor[1].xy + ClipmapTex_LayerAnchor[1].zz - ClipmapTex_LayerAnchor[1].ww)) ? 1 : 0;
    mip = mip - flag.x * flag.y;
    flag = (uv.xy >= ClipmapTex_LayerAnchor[0].xy && uv.xy < (ClipmapTex_LayerAnchor[0].xy + ClipmapTex_LayerAnchor[0].zz - ClipmapTex_LayerAnchor[0].ww)) ? 1 : 0;
    mip = mip - flag.x * flag.y;
    return mip;
}
```

### (7)其他Clipmap的全局属性

* MaxUploadsPerFrame
    * 默认是`16`，对应了 `4 X 4 = 16` Block的一个完整Layer大小。
    * 调大 -> 加载速度变快，但可能导致主线程卡顿（帧生成时间变长）。
    * 调小 -> 帧率平滑，但快速移动时可能出现黑块（加载跟不上）。
* IsValid
    * 只有当所有层级都完成了初始加载（状态非 Init/Pending），系统才被标记为 Valid。在此之前建议不进行渲染或渲染 Loading 画面。


### 参考：
1. [The Clipmap: A Virtual Mipmap](https://notkyon.moe/vt/Clipmap.pdf)
2. [能下载超大尺寸纹理的网站，可用于测试](https://sbcode.net/topoearth/blue-marble-texture-21600x10800/)