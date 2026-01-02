using UnityEngine;
using System.Collections.Generic;
using UnityEngine.Rendering;
using System.Threading;
using System;

namespace bbccyy.Clipmap
{

    public class Clipmap
    {

        [Header("Clipmap Settings")]
        private string dataPath = @"Path\To\Src";
        private string name;
        public string Name
        {
            get { return name; }
        }

        private int _clipmapTexID;
        public int ClipmapTexID
        {
            get { return _clipmapTexID;}
        }

        private int _layerAnchorID;
        public int LayerAnchorID
        {
            get { return _layerAnchorID; }
        }

        private Vector2 probeUV;
        public Vector2 Probe
        {
            set { 
                probeUV.x = Mathf.Clamp01(value.x);
                probeUV.y = Mathf.Clamp01(value.y);
            }
        }

        private int textureSize = 640;
        public int ClipmapTextureSize
        {
            get { return textureSize; }
        }

        private int blockSize = 160;
        private int stackDepth = 5;
        public int ClipmapStackDepth
        {
            get { return stackDepth; }
        }

        private int worldSize = 10240;
        private float updateThreshold = 0.75f;
        private TextureFormat format = TextureFormat.ARGB32;

        private IClipmapAssetLoader m_Loader;

        private Texture2D m_dummy;

        private bool m_isValid;
        public bool IsValid
        {
            get {
                if (m_isValid)
                    return true;
                foreach(var layer in m_Layers)
                {
                    if (layer.status == ClipmapLayer.LayerStatus.Init || 
                        layer.status == ClipmapLayer.LayerStatus.Pending)
                    {
                        return false;
                    }
                }
                m_isValid = true;
                return m_isValid; 
            }
        }

        [Header("Performance")]
        private int maxUploadsPerFrame = 16; // 限制每帧上传GPU的块数，防止掉帧; 
        public int MaxUploadsPerFrame
        {
            get { return maxUploadsPerFrame; }
            set
            {
                if (value <= 0)
                    maxUploadsPerFrame = 1;
                else
                    maxUploadsPerFrame = value;
            }
        }

        // 运行时数据 
        private Texture2DArray m_ClipmapTex;
        public Texture2DArray ClipmapTex
        {
            get { return m_ClipmapTex; }
        }

        private int m_GridSize;              // 640/160 = 4 

        private Queue<TileRequest> m_LoadQueue; 
        private ClipmapLayer[] m_Layers;
        private readonly SynchronizationContext m_MainThreadContext;

        public Clipmap(ClipmapParams param, IClipmapAssetLoader loader)
        {
            this.name = param.clipmapName;              //ClipmapTex
            this.dataPath = param.dataPath;             //"Assets/Resources/ClipmapData"
            this.textureSize = param.textureSize;       //640
            this.blockSize = param.blockSize;           //160
            this.stackDepth = param.stackDepth;         //5
            this.worldSize = param.worldSize;           //10240
            this.updateThreshold = param.updateThreshold;   //0.75 
            this.format = param.format;                 //TextureFormat.ARGB32
            m_Loader = loader;
            m_ClipmapTex = null;
            m_dummy = null;
            _clipmapTexID = Shader.PropertyToID(name);
            _layerAnchorID = Shader.PropertyToID(string.Format($"{name}_LayerAnchor"));
            m_MainThreadContext = SynchronizationContext.Current;
        }

        #region API

        public Vector4[] GetAnchorID()
        {
            Vector4[] an = new Vector4[stackDepth];
            for (int mip = 0; mip < stackDepth; ++mip)
            {
                an[mip] = new Vector4(
                    m_Layers[mip].anchor.x,
                    m_Layers[mip].anchor.y,
                    m_Layers[mip].BlockUVDist * m_GridSize,
                    m_Layers[mip].BlockUVDist / blockSize);
            }
            return an;
        }

        public void BindShaderParams(Material mat)
        {
            if (mat == null || !IsValid)
                return;
            mat.SetTexture(ClipmapTexID, ClipmapTex);
            mat.SetVectorArray(LayerAnchorID, GetAnchorID());
        }

        public void Initialize()
        {
            probeUV = new Vector2(0.5f, 0.5f);

            m_GridSize = textureSize / blockSize;       // 4 ->a Page contains one 4 by 4 block matrix 

            m_Layers = new ClipmapLayer[stackDepth];
            for (int mip = 0; mip < stackDepth; mip++)
            {
                var layer = new ClipmapLayer();
                layer.mipLevel = mip;
                layer.anchor = new Vector2(-1.0f, -1.0f);
                layer.leftDownCornerXY = new Vector2Int(-1, -1);
                layer.nextLeftDownCornerXY = new Vector2Int(-1, -1);
                layer.BlockUVDist = (float)blockSize * (1 << mip) / (float)worldSize;
                layer.totalBlockSize = (worldSize / blockSize) >> mip;
                layer.status = ClipmapLayer.LayerStatus.Init;
                layer.MaxCacheCout = m_GridSize * m_GridSize + m_GridSize;
                m_Layers[mip] = layer;
            }

            m_ClipmapTex = new Texture2DArray(textureSize, textureSize, stackDepth, format, false, true);
            m_ClipmapTex.filterMode = FilterMode.Point;
            m_ClipmapTex.wrapMode = TextureWrapMode.Repeat;
            m_ClipmapTex.name = name;
            m_ClipmapTex.Apply(false, false);

            m_dummy = new Texture2D(blockSize, blockSize, format, false, true);
            m_dummy.SetPixel(0, 0, Color.black);
            m_dummy.Apply(false, false);

            m_LoadQueue = new Queue<TileRequest>();
        }

        public void Dispose()
        {
            if (m_ClipmapTex) UnityEngine.Object.DestroyImmediate(m_ClipmapTex);
            if (m_dummy) UnityEngine.Object.Destroy(m_dummy);
            foreach (var layer in m_Layers)
            {
                layer.Dispose();
            }
            m_Layers = null;
            m_LoadQueue.Clear();
        }

        /// <summary>
        /// Called by rander feature every frame 
        /// </summary>
        /// <param name="aProbeUV">probe position in image's uv space</param>
        /// <param name="cmd">pass a cmd to clipmap</param>
        public void Update(Vector2 aProbeUV, CommandBuffer cmd)
        {
            if (m_ClipmapTex == null)
            {
                Debug.LogWarning("Init clipmap first!");
                Initialize();
            }

            Probe = aProbeUV;

            //loop on reversed mip order 
            for (int mip = stackDepth - 1; mip >= 0; --mip)
            {
                if (!LayerNeedUpdate(mip))
                {
                    continue;
                }

                ScheduleIncrementalUpdate(mip);
            }

            processLoad();  //controlled by maxUploadsPerFrame as a global setting 

            for (int mip = stackDepth - 1; mip >= 0; --mip)
            {
                if (processUpload(cmd, mip))
                {
                    break;  //one full layer uploaded per frame? 
                }
            }

            //prune tile requests via lru 
            for (int mip = 0; mip < stackDepth; ++mip)
            {
                m_Layers[mip].PruneLRU();
            }
        }

        #endregion API

        Vector2Int CalcBestLDCornerOfMip(int aMip)
        {
            bool isOdd = m_GridSize % 2 > 0;
            var layer = m_Layers[aMip];
            Vector2Int centerXY = new Vector2Int(
                Mathf.FloorToInt(probeUV.x / layer.BlockUVDist),
                Mathf.FloorToInt(probeUV.y / layer.BlockUVDist)
                );

            if (centerXY.x >= layer.totalBlockSize) centerXY.x = layer.totalBlockSize - 1;
            if (centerXY.y >= layer.totalBlockSize) centerXY.y = layer.totalBlockSize - 1;

            Vector2Int leftDown = new Vector2Int(
                    centerXY.x - m_GridSize / 2,
                    centerXY.y - m_GridSize / 2);

            if (isOdd)
            {
                return leftDown;
            }

            Vector2 centerUV = new Vector2(centerXY.x * layer.BlockUVDist, centerXY.y * layer.BlockUVDist);
            Vector2 delt = probeUV - centerUV;
            if (delt.x > layer.BlockUVDist / 2)
            {
                centerXY.x = centerXY.x > (layer.totalBlockSize - 2) ? (layer.totalBlockSize - 1) : (centerXY.x + 1);
            }
            if (delt.y > layer.BlockUVDist / 2)
            {
                centerXY.y = centerXY.y > (layer.totalBlockSize - 2) ? (layer.totalBlockSize - 1) : (centerXY.y + 1);
            }

            leftDown.x = centerXY.x - m_GridSize / 2;
            leftDown.y = centerXY.y - m_GridSize / 2;

            return leftDown;
        }

        bool LayerNeedUpdate(int aMip)
        {
            var layer = m_Layers[aMip];
            if (layer.status == ClipmapLayer.LayerStatus.Init)
            {
                return true;
            }
            if (layer.status == ClipmapLayer.LayerStatus.Pending)
            {
                return false;
            }
            if (layer.status == ClipmapLayer.LayerStatus.Normal && 
                layer.totalBlockSize == m_GridSize)
            {
                return false;
            }

            float halfLenUV = layer.BlockUVDist * (float)m_GridSize / 2;
            Vector2 centerUV = layer.anchor + new Vector2(halfLenUV, halfLenUV);
            Vector2 delt = probeUV - centerUV;
            float maxDist = Mathf.Max(Mathf.Abs(delt.x), Mathf.Abs(delt.y));
            bool need = maxDist > layer.BlockUVDist * updateThreshold;

            //在还没有完成下一个状态的更新前，又回到了当前(上一个)状态，触发如下回滚逻辑; 
            if (!need && layer.status == ClipmapLayer.LayerStatus.NormalPending)
            {
                foreach (var req in layer.Requests)
                {
                    req.prepareToUseFlag = false;
                }
                layer.status = ClipmapLayer.LayerStatus.Normal;
            }

            return need;
        }

        void ScheduleIncrementalUpdate(int mip)
        {
            var layer = m_Layers[mip];
            bool isInit = layer.status == ClipmapLayer.LayerStatus.Init;
            var bestLDCornerXY = CalcBestLDCornerOfMip(mip);
            if (!isInit && bestLDCornerXY == layer.nextLeftDownCornerXY)
            {
                return;
            }

            // do has tile change! 
            foreach (var req in layer.Requests)
            {
                req.prepareToUseFlag = false;   //reset flags before schedule 
            }

            layer.nextLeftDownCornerXY = bestLDCornerXY;
            layer.nextAnchor = new Vector2(bestLDCornerXY.x * layer.BlockUVDist, bestLDCornerXY.y * layer.BlockUVDist);
            if (isInit)
            {
                layer.leftDownCornerXY = layer.nextLeftDownCornerXY;
                layer.anchor = layer.nextAnchor;
            }
            
            //loop on every layer blocks 
            for (int y = 0; y < m_GridSize; y++)
            {
                for (int x = 0; x < m_GridSize; x++)
                {
                    // block's global XY
                    int globalBlockX = bestLDCornerXY.x + x;
                    int globalBlockY = bestLDCornerXY.y + y;

                    // was in old range? 
                    bool wasInOldRange = false;

                    if (!isInit)    // init requires all change 
                    {
                        wasInOldRange = globalBlockX >= layer.leftDownCornerXY.x && 
                            globalBlockX < layer.leftDownCornerXY.x + m_GridSize && 
                            globalBlockY >= layer.leftDownCornerXY.y && 
                            globalBlockY < layer.leftDownCornerXY.y + m_GridSize; 
                    }

                    // no in the old? -> try find or load it! 
                    if (!wasInOldRange)
                    {
                        RequestBlockLoad(mip, globalBlockX, globalBlockY);
                    }
                    else
                    {
                        long hash = TileRequest.GetTileHash(mip, globalBlockX, globalBlockY);
                        var oldReq = layer.TryGetRequestByHash(hash);
                        if (oldReq != null)
                        {
                            oldReq.prepareToUseFlag = true;
                        }
                        else
                        {
                            Debug.LogError("error");
                        }
                    }
                }
            }

            //update layer status 
            if (isInit)
            {
                layer.status = ClipmapLayer.LayerStatus.Pending;
            }
            else
            {
                layer.status = ClipmapLayer.LayerStatus.NormalPending;
            }
        }

        void RequestBlockLoad(int mip, int globalX, int globalY)
        {
            long hash = TileRequest.GetTileHash(mip, globalX, globalY);
            var layer = m_Layers[mip];
            
            // check if has been requested before 
            var tileReq = layer.TryGetRequestByHash(hash);
            if (tileReq != null)
            {
                tileReq.prepareToUseFlag = true;
                return;
            }

            // init tile request 
            // 计算在 Texture2DArray 中的物理位置 (Rolling Buffer)
            // formuler -> (Global % Grid) * BlockSize
            // 注意 globalXY 可以为负数，求取余数后亦为负，因此使用 (globalX % m_GridSize) + m_GridSize 优化负数结果; 
            int modX = ((globalX % m_GridSize) + m_GridSize) % m_GridSize;
            int modY = ((globalY % m_GridSize) + m_GridSize) % m_GridSize;
            int destX = modX * blockSize;
            int destY = modY * blockSize;
            tileReq = new TileRequest();    //TODO: poolize it 
            var fileName = string.Format($"{Name}_{globalY}_{globalX}_{mip}");
            var extend = "bytes";    //todo ...
            var fullPath = string.Format($"{dataPath}/{fileName}.{extend}");
            tileReq.InitRequest(mip, globalX, globalY, destX, destY, fullPath, fileName);
            tileReq.layer = layer;
            layer.AddRequest(tileReq);

            // check if target block is out of boundary (OOB) 
            int totalBlocksAtMip = layer.totalBlockSize;
            bool isOOB = (globalX < 0 || globalX >= totalBlocksAtMip ||
                          globalY < 0 || globalY >= totalBlocksAtMip);
            if (isOOB)
            {
                tileReq.data = m_dummy;
                tileReq.isDummy = true;
                QueueUpload(tileReq);   // waiting to upload 
                return;
            }

            // add to queue (waiting to load) 
            QueueLoad(tileReq); 
        }

        void QueueUpload(TileRequest aReq)
        {
            aReq.status = TileRequest.RequestStatus.WaitingToUpload;
        }

        void QueueLoad(TileRequest aReq)
        {
            aReq.status = TileRequest.RequestStatus.WaitingToLoad;
            m_LoadQueue.Enqueue(aReq);
        }

        private void DispatchToMainThread(Action action)
        {
            // 如果当前已经是主线程（UnityWebRequest的情况），直接执行;
            if (SynchronizationContext.Current == m_MainThreadContext)
            {
                action?.Invoke();
            }
            else
            {
                // 如果是在后台线程，使用 Post 发送到主线程队列;
                // Post 是异步的，Send 是同步的。可以用 Post 防止死锁; 
                m_MainThreadContext.Post(_ => action?.Invoke(), null);
            }
        }
        private void doQueueLoad(TileRequest aReq)
        {
            aReq.status = TileRequest.RequestStatus.Loading;

            Action<byte[]> onComplete = (rawdata) =>
            {
                // Callback 
                if (rawdata != null)
                {
                    aReq.data = new Texture2D(blockSize, blockSize, format, false);
                    aReq.data.name = aReq.name;
                    aReq.data.LoadRawTextureData(rawdata);  //fastest way to reconstruct a image 
                    aReq.data.Apply(false, false);  //CPU -> GPU without CPU Memory 
                    QueueUpload(aReq);
                }
            };

            Action<byte[]> mainThreadCallback = (data) =>
            {
                DispatchToMainThread(() => onComplete?.Invoke(data));
            };

            // do load 
            m_Loader.LoadRawData(aReq.fullPath, mainThreadCallback);
        }

        private void processLoad()
        {
            int processedCt = 0;
            while (m_LoadQueue.Count > 0 && processedCt < maxUploadsPerFrame)
            {
                var req = m_LoadQueue.Dequeue();
                doQueueLoad(req);
                if (req.layer.status != ClipmapLayer.LayerStatus.Pending)   //when init, do all at once! 
                    processedCt++;
            }
        }

        private bool processUpload(CommandBuffer cmd, int aMip)
        {
            var layer = m_Layers[aMip];
            if (layer.status == ClipmapLayer.LayerStatus.Normal || 
                layer.status == ClipmapLayer.LayerStatus.Init)
                return false;

            bool allDone = true;
            foreach (var req in layer.Requests)
            {
                if (req.prepareToUseFlag && 
                    req.status != TileRequest.RequestStatus.InUse && 
                    req.status != TileRequest.RequestStatus.WaitingToUpload)
                {
                    allDone = false;
                    break;
                }
            }

            if (allDone)
            {
                foreach (var req in layer.Requests)
                {
                    if (!req.prepareToUseFlag && req.status == TileRequest.RequestStatus.InUse)
                    {
                        //handle old yet unused block status -> ready to upload to layer 
                        req.status = TileRequest.RequestStatus.WaitingToUpload;
                    }
                    if (req.prepareToUseFlag && req.status != TileRequest.RequestStatus.InUse)
                    {
                        cmd.CopyTexture(req.data, 0, 0, 0, 0, blockSize, blockSize,
                            m_ClipmapTex, req.mipLevel, 0, req.destX, req.destY);
                        req.status = TileRequest.RequestStatus.InUse;
                    }
                    req.prepareToUseFlag = false;   //reset when finished 
                }

                if (layer.status == ClipmapLayer.LayerStatus.Pending)
                    allDone = false;        //trick -> allow upload all layers at once! 

                //update new anchor info and layer status 
                layer.leftDownCornerXY = layer.nextLeftDownCornerXY;
                layer.anchor = layer.nextAnchor;
                layer.status = ClipmapLayer.LayerStatus.Normal;
            }

            return allDone;
        }

    }
}