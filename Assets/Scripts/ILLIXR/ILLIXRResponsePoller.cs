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
        const int MaxTotalPoints  = 100000;
        const int TextQueryBufLen = 512;

        readonly int[]   _pointsPerCloud = new int[MaxPointClouds];
        readonly float[] _centroids     = new float[MaxPointClouds * 3];
        readonly float[] _colors         = new float[MaxPointClouds * 3];
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

            int result = ILLIXRBridge.illixr_unity_get_query_response_info(
                out ulong queryId,
                out int   numClouds,
                out int   totalPoints,
                _pointsPerCloud,
                MaxPointClouds,
                _centroids,
                _colors,
                MaxPointClouds * 3,
                out int   numColors,
                out float serverLatency,
                _textQueryBuf,
                TextQueryBufLen);

            if (result == 0) return;

            // Allocate exact buffer and fetch points
            float[] allPoints = new float[totalPoints * 3];
            ILLIXRBridge.illixr_unity_get_query_response_points(
                queryId, allPoints, totalPoints);

            string textQuery = System.Text.Encoding.UTF8
                .GetString(_textQueryBuf).TrimEnd('\0');

            Debug.Log($"[ILLIXRResponsePoller] Response: id={queryId} " +
                      $"clouds={numClouds} totalPoints={totalPoints} " +
                      $"text='{textQuery}'");

            OnResponse?.Invoke(new QueryResponseData
            {
                QueryId       = queryId,
                NumClouds     = numClouds,
                Centroids     = _centroids,
                Colors        = _colors,
                PointsPerCloud = _pointsPerCloud,
                AllPoints     = allPoints,
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
        public int[]   PointsPerCloud;
        public float[] AllPoints;
        public float   ServerLatency;
        public string  TextQuery;
    }
}