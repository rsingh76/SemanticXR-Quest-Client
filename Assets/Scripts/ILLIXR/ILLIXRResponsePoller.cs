using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SemanticXR.UI
{
    public class ILLIXRResponsePoller : MonoBehaviour
    {
        // Fired when a query response arrives — same signature as
        // AudioStreamController.OnPointClouds so existing UI handlers work unchanged
        public event Action<QueryResponseData> OnResponse;
        public event Action<string>            OnStatus;

        // Max point clouds we allocate for per poll — resize if needed
        const int MaxPointClouds  = 64;
        const int TextQueryBufLen = 512;

        readonly float[]  _centroids     = new float[MaxPointClouds * 3];
        readonly byte[]   _textQueryBuf  = new byte[TextQueryBufLen];

        void Update()
        {
            int result = ILLIXRBridge.illixr_unity_get_query_response(
                out ulong queryId,
                _centroids,
                out int numClouds,
                out float serverLatency,
                _textQueryBuf,
                TextQueryBufLen);

            if (result == 0) return;   // no new response

            GCHandle handle = GCHandle.Alloc(_textQueryBuf, GCHandleType.Pinned);
            try {
                string textQuery = Marshal.PtrToStringUTF8(handle.AddrOfPinnedObject()) ?? string.Empty;
                OnStatus?.Invoke($"Response: {numClouds} objects found. Query: '{textQuery}'");
                Debug.Log($"[ILLIXR] Response id={queryId} numClouds={numClouds} latency={serverLatency:F3}s");

                OnResponse?.Invoke(new QueryResponseData
                {
                    QueryId       = queryId,
                    NumClouds     = numClouds,
                    Centroids     = _centroids,
                    ServerLatency = serverLatency,
                    TextQuery     = textQuery,
                });
            }
            finally {
                handle.Free();
            }
        }
    }

    // Mirrors allPointClouds from protobuf — used by existing UI handlers
    public class QueryResponseData
    {
        public ulong   QueryId;
        public int     NumClouds;
        public float[] Centroids;   // flat [x0,y0,z0, x1,y1,z1, ...]
        public float   ServerLatency;
        public string  TextQuery;
    }
}