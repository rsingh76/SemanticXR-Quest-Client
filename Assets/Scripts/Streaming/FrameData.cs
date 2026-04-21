using UnityEngine;

namespace SemanticXR.Streaming
{
    /// <summary>
    /// One frame of co-captured data ready to send. All RGB / depth / pose fields
    /// are captured in the same Unity Update() tick (best-effort sync, not atomic).
    /// </summary>
    public class FrameData
    {
        // ----- RGB -----
        public byte[] H265Bytes;                    // NV12 input to hardware encoder (will be H.265 after encoding)
        public int ImageWidth, ImageHeight;
        public float Fx, Fy, Cx, Cy;                // RGB camera intrinsics

        // ----- Depth -----
        // Raw bytes from _PreprocessedEnvironmentDepthTexture, format R16G16B16A16_SFloat
        // (8 bytes/pixel = 4 half-floats). To recover linear depth in meters:
        //   depth_m = nearZ / R_channel
        // Channels: R = 1.0 - minAvg, G = 1.0 - maxAvg, B = avg - minAvg, A = maxAvg - minAvg.
        public byte[] DepthBytes;
        public int DepthWidth, DepthHeight;
        public float DepthNearZ, DepthFarZ;
        public float DepthFx, DepthFy, DepthCx, DepthCy;   // pinhole intrinsics derived from FOV tangents
        public float DepthFovTanLeft, DepthFovTanRight, DepthFovTanTop, DepthFovTanDown; // raw source values
        public bool HasDepthIntrinsics;

        // ----- Poses (all 4x4 Unity world-space, will be LH->RH converted on the wire) -----
        public Matrix4x4 HeadPose;          // Camera.main.localToWorldMatrix at capture
        public Matrix4x4 RgbCameraPose;     // PassthroughCameraAccess.GetCameraPose() (RGB sensor + lens offset)
        public bool HasRgbCameraPose;
        public Matrix4x4 DepthCameraPose;   // EnvironmentDepthFrameDesc createPoseLocation/Rotation (tracking space)

        // ----- Timestamps -----
        // NOTE: these are in DIFFERENT clock domains and not directly comparable:
        //   RgbTimestampNs   = Android Camera2 SENSOR_TIMESTAMP, CLOCK_BOOTTIME nanoseconds
        //   DepthTimestampNs = OpenXR XrTime, CLOCK_MONOTONIC nanoseconds
        //   TimestampNs      = Unity render-loop time at capture (NOT a sensor clock)
        public long TimestampNs;
        public long RgbTimestampNs;
        public long DepthTimestampNs;

        public int FrameNumber;
    }
}
