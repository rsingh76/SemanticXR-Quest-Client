using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace SemanticXR.Streaming
{
    public class ILLIXRFramesClient : IFramesClient
    {
        public int    QueuedFrames   => 0;       // synchronous — no queue
        public bool   IsConnected    { get; private set; }
        public int    SentFrames     { get; private set; }
        public long   TotalBytesSent { get; private set; }
        public string LastError      { get; private set; }

        public void Start()
        {
            int result = ILLIXRBridge.illixr_unity_init();
            if (result != 0)
            {
                LastError = $"illixr_unity_init failed with code {result}";
                Debug.LogError($"[ILLIXR] {LastError}");
                return;
            }
            IsConnected = true;
            Debug.LogWarning("[ILLIXR] Runtime initialized");
        }

        public void Enqueue(FrameData f)
        {
            if (!IsConnected) return;

            // Unity left-handed -> right-handed (negate Z column & row),
            // same convention as TcpProtoClient.SendFrame
            float[] rgbPose   = LhToRh(f.RgbCameraPose);
            float[] depthPose = LhToRh(f.DepthCameraPose);

            float[] intrinsics = new float[]
            {
                f.Fx, f.Fy, f.Cx, f.Cy
            };

            float[] depthIntrinsics = f.HasDepthIntrinsics
                ? new float[] { f.DepthFx, f.DepthFy, f.DepthCx, f.DepthCy }
                : new float[4];

            byte[] image = f.H265Bytes ?? Array.Empty<byte>();
            byte[] depth = f.DepthBytes ?? Array.Empty<byte>();

            ILLIXRBridge.illixr_unity_send_semantic_frame(
                f.FrameNumber,
                f.ImageWidth,    f.ImageHeight,
                image,           image.Length,
                f.DepthWidth,    f.DepthHeight,
                depth,           depth.Length,
                f.DepthNearZ,
                intrinsics,
                depthIntrinsics,
                rgbPose,
                depthPose,
                f.DepthFarZ      // used as max_depth
            );

            TotalBytesSent += image.Length + depth.Length;
            SentFrames++;
        }

        public void Stop()
        {
            ILLIXRBridge.illixr_unity_shutdown();
            IsConnected = false;
            Debug.LogWarning("[ILLIXR] Runtime stopped");
        }

        public void Dispose() => Stop();

        static float[] LhToRh(Matrix4x4 m) => new[]
        {
             m.m00,  m.m01, -m.m02,  m.m03,
             m.m10,  m.m11, -m.m12,  m.m13,
            -m.m20, -m.m21,  m.m22, -m.m23,
             m.m30,  m.m31, -m.m32,  m.m33,
        };
    }
}