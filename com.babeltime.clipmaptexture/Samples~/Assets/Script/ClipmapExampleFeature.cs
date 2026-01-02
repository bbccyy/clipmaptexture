using UnityEngine;
using UnityEngine.Rendering;
using UnityEngine.Rendering.Universal;
using Babeltime.Clipmap;
using System;
using System.IO;
using System.Threading.Tasks;

public class ClipmapExampleFeature : ScriptableRendererFeature
{
    class ClipmapRenderPass : ScriptableRenderPass
    {
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
                        Debug.LogError($"[Loader] Error loading {aAssetPath}: {ex.Message}");
                        onComplete?.Invoke(null);
                    }
                });

            }
        }

        public ClipmapParams param;
        public Transform probe;
        public Vector2 ScopeLeftDownPos;
        public Vector2 ScopeRightUpPos;
        public Material Mat;

        private Clipmap _clipmap;
        public Clipmap Clipmap
        {
            get { return _clipmap; }
        }

        private Vector2 probeUV;

        public void Init()
        {
            _clipmap = new Clipmap(param, new ExampleProjectAssetLoader());
            _clipmap.Initialize();
            probeUV = new Vector2(0, 0);
        }

        public void Dispose()
        {
            _clipmap.Dispose();
            _clipmap = null;

        }

        private void CalcProbeUV(ref Vector2 pUV)
        {
            pUV.x = Mathf.Clamp(probe.position.x, ScopeLeftDownPos.x, ScopeRightUpPos.x);
            pUV.y = Mathf.Clamp(probe.position.z, ScopeLeftDownPos.y, ScopeRightUpPos.y);
            pUV.x = (pUV.x - ScopeLeftDownPos.x) / (ScopeRightUpPos.x - ScopeLeftDownPos.x);
            pUV.y = (pUV.y - ScopeLeftDownPos.y) / (ScopeRightUpPos.y - ScopeLeftDownPos.y);
        }

        // This method is called before executing the render pass.
        // It can be used to configure render targets and their clear state. Also to create temporary render target textures.
        // When empty this render pass will render to the active camera render target.
        // You should never call CommandBuffer.SetRenderTarget. Instead call <c>ConfigureTarget</c> and <c>ConfigureClear</c>.
        // The render pipeline will ensure target setup and clearing happens in a performant manner.
        public override void OnCameraSetup(CommandBuffer cmd, ref RenderingData renderingData)
        {
        }

        // Here you can implement the rendering logic.
        // Use <c>ScriptableRenderContext</c> to issue drawing commands or execute command buffers
        // https://docs.unity3d.com/ScriptReference/Rendering.ScriptableRenderContext.html
        // You don't have to call ScriptableRenderContext.submit, the render pipeline will call it at specific points in the pipeline.
        public override void Execute(ScriptableRenderContext context, ref RenderingData renderingData)
        {
            CommandBuffer cmd = CommandBufferPool.Get("UpdateClipmap"); 

            //calc probeUV 
            CalcProbeUV(ref probeUV); 

            _clipmap.Update(probeUV, cmd);  //clipmap update main logic 

            _clipmap.BindShaderParams(Mat); //set texture and params to material 

            context.ExecuteCommandBuffer(cmd); 

            CommandBufferPool.Release(cmd); 
        }

        // Cleanup any allocated resources that were created during the execution of this render pass.
        public override void OnCameraCleanup(CommandBuffer cmd)
        {
        }
    }

    [SerializeField]
    public ClipmapParams param;

    private GameObject Probe;

    public Vector2 ScopeLeftDownPos;

    public Vector2 ScopeRightUpPos;

    public Material Mat;

    ClipmapRenderPass m_ScriptablePass;

    /// <inheritdoc/>
    public override void Create()
    {
        var found = GameObject.Find("Probe");
        if (found != null)
        {
            Probe = found;
        }
        else
        {
            Debug.LogError("not found go");
            return;
        }

        m_ScriptablePass = new ClipmapRenderPass();

        // 如果使用Clipmap的shader发生在Opaque阶段，那么绘制Clipmap的时机最好在此之后;
        // 确保跨帧后应用方才访问更新后的Clipmap texture和material binding数据; 
        m_ScriptablePass.renderPassEvent = RenderPassEvent.BeforeRenderingPostProcessing; 

        m_ScriptablePass.param = param;
        m_ScriptablePass.probe = Probe.transform;
        m_ScriptablePass.ScopeLeftDownPos = ScopeLeftDownPos;
        m_ScriptablePass.ScopeRightUpPos = ScopeRightUpPos;
        m_ScriptablePass.Mat = Mat;

        m_ScriptablePass.Init();
    }

    // Here you can inject one or multiple render passes in the renderer.
    // This method is called when setting up the renderer once per-camera.
    public override void AddRenderPasses(ScriptableRenderer renderer, ref RenderingData renderingData)
    {
        if (m_ScriptablePass != null)
            renderer.EnqueuePass(m_ScriptablePass);
    }

    private void OnDestroy()
    {
        if (m_ScriptablePass != null)
            m_ScriptablePass.Dispose();
    }

}


