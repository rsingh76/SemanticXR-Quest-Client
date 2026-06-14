using System;
using System.Linq;
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

        // Gate for the native poll. The runtime is only initialized for an
        // ILLIXR-connected session; in gRPC/Tcp sessions (or before any
        // connect) illixr_unity_get_query_response would P/Invoke into an
        // uninitialized runtime every frame. The orchestrator sets this true
        // only while connected over the ILLIXR transport.
        public bool Active { get; set; }

        // Max point clouds we allocate for per poll — resize if needed
        const int MaxPointClouds  = 64;
        const int TextQueryBufLen = 512;

        readonly float[] _centroids     = new float[MaxPointClouds * 3];
        readonly float[] _colors     = new float[MaxPointClouds];
        readonly byte[]  _textQueryBuf  = new byte[TextQueryBufLen];

        void Update()
        {
            // Only poll when the ILLIXR runtime is up (see Active). Otherwise
            // this is a native call into an uninitialized runtime every frame.
            if (!Active) return;

            // Log every N frames to avoid flooding — adjust as needed
            bool log_this_frame = Time.frameCount % 60 == 0;

            if (log_this_frame)
                Debug.Log($"[ILLIXRResponsePoller] Polling... frame={Time.frameCount}");

            int result = ILLIXRBridge.illixr_unity_get_query_response(
                out ulong queryId,
                _centroids,
                out int numClouds,
                _colors,
                MaxPointClouds,
                out float serverLatency,
                _textQueryBuf,
                TextQueryBufLen);

            if (log_this_frame)
                Debug.Log($"[ILLIXRResponsePoller] Poll result={result}");

            if (result == 0) return;

            string textQuery = System.Text.Encoding.UTF8.GetString(_textQueryBuf).TrimEnd('\0');
            Debug.Log($"[ILLIXRResponsePoller] Response received: " +
                      $"queryId={queryId} " +
                      $"numClouds={numClouds} " +
                      $"serverLatency={serverLatency:F3} " +
                      $"textQuery='{textQuery}' " +
                      $"centroids=[{string.Join(", ", _centroids.Take(numClouds * 3).Select(f => f.ToString("F3")))}]");

            OnStatus?.Invoke($"Response: {numClouds} objects found. Query: '{textQuery}'");
            OnResponse?.Invoke(new QueryResponseData
            {
                QueryId       = queryId,
                NumClouds     = numClouds,
                Centroids     = _centroids,
                Colors        = _colors,
                ServerLatency = serverLatency,
                TextQuery     = textQuery,
            });
        }
        
    }

    // Mirrors allPointClouds from protobuf — used by existing UI handlers
    public class QueryResponseData
    {
        public ulong   QueryId;
        public int     NumClouds;
        public float[] Centroids;   // flat [x0,y0,z0, x1,y1,z1, ...]
        public float[] Colors;
        public float   ServerLatency;
        public string  TextQuery;
    }
}