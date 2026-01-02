using System;
using System.Collections.Generic;
using UnityEngine;

namespace bbccyy.Clipmap
{
    public class TileRequest
    {
        public enum RequestStatus
        {
            Init = 0,
            InUse = 1,
            WaitingToLoad = 2,
            Loading = 3,
            WaitingToUpload = 4,
        }
        public RequestStatus status;
        public long hash;
        /// <summary>
        /// 触发下一个状态后，所有与下一个状态关联的Block都会被标记，不论是InUse还是其他; 
        /// </summary>
        public bool prepareToUseFlag; 
        public int mipLevel;
        public int globalRow, globalCol;    // 在该级Mip世界空间中的下标; 
        public int destX, destY;            // Texture2dArray中Layer域的目标位置(XY对应下标); 
        public string fullPath;
        public string name;
        public Texture2D data;
        public bool isDummy;
        public ClipmapLayer layer;

        public static long GetTileHash(int mip, int x, int y)
        {
            return ((long)mip << 50) ^ ((long)x << 25) ^ (long)y;
        }

        public void InitRequest(int aMip, int aGlobalX, int aGlobalY, int aDestX, int aDestY, string aPath, string aName)
        {
            status = RequestStatus.Init;
            hash = GetTileHash(aMip, aGlobalX, aGlobalY);
            prepareToUseFlag = true;
            mipLevel = aMip;
            globalRow = aGlobalY;
            globalCol = aGlobalX;
            destX = aDestX;
            destY = aDestY;
            fullPath = aPath;
            name = aName;
            isDummy = false;
            data = null;
        }
    }

    public class ClipmapLayer
    {
        public enum LayerStatus
        {
            Init = 0,           //首次加载的最初状态，需要快速过渡; 
            Pending = 1,        //从Init过渡到Normal的中间不可用状态; 
            Normal = 2,         //正常可访问Texture状态，没有待更新的下一个状态;
            NormalPending = 3   //从当前Texture过渡到下一个更新状态的中间态(可以正常访问，数据是当前状态的); 
        }

        public int mipLevel;
        public Vector2 anchor;                  //global UV of current layer's left-down corner 
        public Vector2 nextAnchor;              //when system is preparing with new layout, this value will be a corret one in the future 
        public Vector2Int leftDownCornerXY;     //global block index XY of current layer's left-down corner 
        public Vector2Int nextLeftDownCornerXY; //when system is preparing with new layout, this value will be a corret one in the future 
        public float BlockUVDist;               //per block side lenth described by UV 
        public int totalBlockSize;              //number of blocks arranged on X or Y axis under current mip level 
        public LayerStatus status;

        //for LRU 
        private Dictionary<long, TileRequest> _requestMap;
        private LinkedList<TileRequest> _lruList;
        public int MaxCacheCout;
        public IEnumerable<TileRequest> Requests => _lruList;

        public ClipmapLayer()
        {
            _requestMap = new Dictionary<long, TileRequest>();
            _lruList = new LinkedList<TileRequest>();
            MaxCacheCout = 25;
        }

        public TileRequest TryGetRequestByHash(long aHash)
        {
            if (_requestMap.TryGetValue(aHash, out TileRequest req))
            {
                touch(req);
                return req;
            }
            return null;
        }

        private void touch(TileRequest req)
        {
            _lruList.Remove(req);
            _lruList.AddFirst(req);
        }

        public void AddRequest(TileRequest req)
        {
            if (_requestMap.ContainsKey(req.hash))
                return;

            _lruList.AddFirst(req);
            _requestMap.Add(req.hash, req);
        }

        public void PruneLRU()
        {
            while(_requestMap.Count > MaxCacheCout)
            {
                var targetReq = _lruList.Last.Value;

                if (targetReq.status == TileRequest.RequestStatus.InUse || 
                    targetReq.prepareToUseFlag ||
                    targetReq.status == TileRequest.RequestStatus.Loading)
                    break;

                DisposeRequest(targetReq);

                _requestMap.Remove(targetReq.hash);
                _lruList.RemoveLast();
            }
        }


        private void DisposeRequest(TileRequest req)
        {
            req.prepareToUseFlag = false;
            if (req.data && !req.isDummy)
            {
                UnityEngine.Object.DestroyImmediate(req.data);
                req.data = null;
            }
        }


        public void Dispose()
        {
            foreach (var req in _lruList)
            {
                DisposeRequest(req);
            }
            _requestMap.Clear();
            _lruList.Clear();
        }

    }

    // 外部资产加载接口，由项目方实现; 
    public interface IClipmapAssetLoader
    {
        void LoadRawData(string aAssetPath, Action<byte[]> onComplete);
    }

    [Serializable]
    public struct ClipmapParams
    {
        public string clipmapName;
        public string dataPath;
        public int textureSize;         //Page size in texture2dArray 
        public int blockSize;           //Page will be devided into blocks, set block size here 
        public int stackDepth;          //clipmap stack depth 
        public int worldSize;           //full size of target texture 
        public float updateThreshold;   //threshold that controlls update, 0.5 < threshold < 1.0
        public TextureFormat format;    //Page format 
    }

}