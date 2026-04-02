using System;
using System.Reflection;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using SemanticXR.Encoding;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace SemanticXR.Streaming
{
    public class StreamingOrchestrator : MonoBehaviour
    {
        PassthroughCameraAccess _cam;
        HardwareH265Encoder _encoder;
        TcpProtoClient _tcp;

        int _frameNumber;
        float _captureInterval;
        float _lastCaptureTime;
        bool _encoderReady;
        int _capturedCount, _encoderOutCount;
        float _lastLogTime;

        // Depth capture
        EnvironmentDepthManager _depthManager;
        bool _depthReadbackPending;

        // Depth camera intrinsics & pose (from EnvironmentDepthManager via reflection)
        FieldInfo _frameDescField;

        // --- Snapshot: captured atomically at readback REQUEST time ---
        // This ensures depth bytes, RGB, head pose, depth pose, depth intrinsics,
        // and zbuffer params all correspond to the SAME moment in time.
        struct DepthSnapshot
        {
            public byte[] DepthBytes;
            public int DepthWidth, DepthHeight;
            public float NearZ, FarZ;               // ZBufferParams
            public Matrix4x4 HeadPose;               // Camera.main at request time
            public float DepthFx, DepthFy, DepthCx, DepthCy;
            public Matrix4x4 DepthCameraPose;
            public bool HasDepthIntrinsics;
            // RGB captured at the same instant as depth
            public byte[] RgbNv12;
            public int RgbWidth, RgbHeight;
            public float RgbFx, RgbFy, RgbCx, RgbCy;
            public bool Valid;
        }
        DepthSnapshot _latestSnapshot;

        public bool IsConnected => _tcp?.IsConnected == true;
        public int FrameCount => _tcp?.SentFrames ?? 0;
        public int QueuedFrames => _tcp?.QueuedFrames ?? 0;
        public string LastError => _tcp?.LastError;
        public string ServerTarget { get; private set; } = "Not connected";

        public event Action OnConnected;
        public event Action OnDisconnected;
        public event Action<string> OnError;

        public void Connect(string address, int port, int fps)
        {
            if (_tcp != null) return;

            _captureInterval = 1f / Mathf.Clamp(fps, 1, 30);
            _frameNumber = 0;
            _encoderReady = false;
            _capturedCount = 0;
            _encoderOutCount = 0;
            ServerTarget = $"{address}:{port}";

            _tcp = new TcpProtoClient(address, port, fps);
            _tcp.Start();

            OnConnected?.Invoke();
        }

        public void Disconnect()
        {
            _encoder?.Stop(); _encoder?.Dispose(); _encoder = null;
            _tcp?.Stop(); _tcp?.Dispose(); _tcp = null;
            _encoderReady = false;
            ServerTarget = "Not connected";
            OnDisconnected?.Invoke();
        }

        void Start()
        {
            _cam = FindAnyObjectByType<PassthroughCameraAccess>();
            if (_cam == null)
                Debug.LogError("[Orchestrator] No PassthroughCameraAccess found!");

            // Setup depth
            SetupDepth();

            // Disable the CameraToWorld demo UI elements
            DisableDemoUI();
        }

        void SetupDepth()
        {
            _depthManager = FindAnyObjectByType<EnvironmentDepthManager>();
            if (_depthManager == null)
            {
                var go = new GameObject("EnvironmentDepthManager");
                _depthManager = go.AddComponent<EnvironmentDepthManager>();
                Debug.LogWarning("[Orchestrator] Created EnvironmentDepthManager");
            }
            // Cache reflection access to internal frameDescriptors field
            _frameDescField = typeof(EnvironmentDepthManager).GetField(
                "frameDescriptors",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            if (_frameDescField != null)
                Debug.LogWarning("[Orchestrator] Found frameDescriptors field via reflection");
            else
                Debug.LogWarning("[Orchestrator] Could not find frameDescriptors — depth intrinsics unavailable");

            Debug.LogWarning("[Orchestrator] Depth capture setup complete");
        }

        /// <summary>
        /// Extract depth camera intrinsics and pose from EnvironmentDepthManager's
        /// internal frameDescriptors via reflection. Returns values for left eye (index 0).
        /// </summary>
        bool TryGetDepthCameraParams(out float dfx, out float dfy, out float dcx, out float dcy,
                                      out Matrix4x4 depthPose, int depthW, int depthH)
        {
            dfx = dfy = dcx = dcy = 0;
            depthPose = Matrix4x4.identity;

            if (_depthManager == null || _frameDescField == null) return false;

            try
            {
                var descs = _frameDescField.GetValue(_depthManager) as Array;
                if (descs == null || descs.Length == 0) return false;

                var desc = descs.GetValue(0);
                var descType = desc.GetType();

                float fovLeft = (float)descType.GetField("fovLeftAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                float fovRight = (float)descType.GetField("fovRightAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                float fovTop = (float)descType.GetField("fovTopAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                float fovDown = (float)descType.GetField("fovDownAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);

                Vector3 poseLoc = (Vector3)descType.GetField("createPoseLocation",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                Quaternion poseRot = (Quaternion)descType.GetField("createPoseRotation",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);

                if (fovLeft <= 0 || fovRight <= 0) return false;

                float w = depthW > 0 ? depthW : 320;
                float h = depthH > 0 ? depthH : 320;

                dfx = w / (fovRight + fovLeft);
                dfy = h / (fovTop + fovDown);
                dcx = fovLeft * dfx;
                dcy = fovTop * dfy;  // cy from top after server's vertical flip

                depthPose = Matrix4x4.TRS(poseLoc, poseRot, Vector3.one);

                if (_capturedCount <= 3)
                    Debug.LogWarning($"[Depth] FOV tangents: L={fovLeft:F3} R={fovRight:F3} T={fovTop:F3} D={fovDown:F3} " +
                                     $"=> fx={dfx:F1} fy={dfy:F1} cx={dcx:F1} cy={dcy:F1} " +
                                     $"pose=({poseLoc.x:F3},{poseLoc.y:F3},{poseLoc.z:F3})");

                return true;
            }
            catch (Exception ex)
            {
                if (_capturedCount <= 3)
                    Debug.LogWarning($"[Depth] Reflection failed: {ex.Message}");
                return false;
            }
        }

        void TryReadbackDepth()
        {
            if (_depthReadbackPending) return;

            var depthTex = Shader.GetGlobalTexture("_PreprocessedEnvironmentDepthTexture") as RenderTexture;
            if (depthTex == null || !depthTex.IsCreated()) return;

            // ---- SNAPSHOT everything at REQUEST time ----
            // The GPU readback captures the depth texture content at this moment.
            // We ALSO capture RGB, pose, intrinsics NOW so they all match.

            // Capture RGB at this instant (before async readback)
            byte[] rgbNv12 = null;
            int rgbW = 0, rgbH = 0;
            float rgbFx = 0, rgbFy = 0, rgbCx = 0, rgbCy = 0;

            if (_cam != null && _cam.IsPlaying)
            {
                var res = _cam.CurrentResolution;
                if (res.x > 0 && res.y > 0)
                {
                    try
                    {
                        var colors = _cam.GetColors();
                        if (colors.IsCreated && colors.Length > 0)
                        {
                            rgbW = res.x;
                            rgbH = res.y;
                            rgbNv12 = RgbaToNv12(colors, rgbW, rgbH);

                            var intr = _cam.Intrinsics;
                            if (intr.FocalLength.x > 0)
                            {
                                rgbFx = intr.FocalLength.x;
                                rgbFy = intr.FocalLength.y;
                                rgbCx = intr.PrincipalPoint.x;
                                rgbCy = intr.PrincipalPoint.y;
                            }
                        }
                    }
                    catch { /* passthrough not ready yet */ }
                }
            }

            _depthReadbackPending = true;

            var zbuf = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");
            float nearZ = zbuf.x;
            float farZ = zbuf.y;

            // Head pose at this instant
            var headPose = Camera.main != null ? Camera.main.transform.localToWorldMatrix : Matrix4x4.identity;

            // Depth camera intrinsics & pose at this instant
            int texW = depthTex.width, texH = depthTex.height;
            bool hasDepthIntr = TryGetDepthCameraParams(
                out float dfx, out float dfy, out float dcx, out float dcy,
                out Matrix4x4 depthCamPose, texW, texH);

            if (_capturedCount <= 3)
                Debug.LogWarning($"[Depth] ZBufferParams=({zbuf.x:G}, {zbuf.y:G}, {zbuf.z:G}, {zbuf.w:G})");

            AsyncGPUReadback.Request(depthTex, 0, 0, depthTex.width, 0, depthTex.height, 0, 1,
                request =>
                {
                    _depthReadbackPending = false;

                    if (request.hasError) return;

                    var data = request.GetData<byte>();

                    // Store the complete snapshot: depth + RGB + pose + intrinsics from request time
                    _latestSnapshot = new DepthSnapshot
                    {
                        DepthBytes = data.ToArray(),
                        DepthWidth = request.width,
                        DepthHeight = request.height,
                        NearZ = nearZ,
                        FarZ = farZ,
                        HeadPose = headPose,
                        DepthFx = dfx,
                        DepthFy = dfy,
                        DepthCx = dcx,
                        DepthCy = dcy,
                        DepthCameraPose = depthCamPose,
                        HasDepthIntrinsics = hasDepthIntr,
                        RgbNv12 = rgbNv12,
                        RgbWidth = rgbW,
                        RgbHeight = rgbH,
                        RgbFx = rgbFx,
                        RgbFy = rgbFy,
                        RgbCx = rgbCx,
                        RgbCy = rgbCy,
                        Valid = rgbNv12 != null,
                    };

                    if (_capturedCount <= 3 || _capturedCount % 30 == 0)
                        Debug.LogWarning($"[Depth] Readback: {request.width}x{request.height}, " +
                                         $"{_latestSnapshot.DepthBytes.Length} bytes, near={nearZ:F3} far={farZ:F3}" +
                                         $", rgb={rgbW}x{rgbH}");
                });
        }

        void DisableDemoUI()
        {
            foreach (var mb in FindObjectsByType<MonoBehaviour>(FindObjectsSortMode.None))
            {
                var name = mb.GetType().Name;
                if (name == "CameraToWorldManager" || name == "CameraToWorldCameraCanvas" ||
                    name == "CameraToWorldRayRenderer")
                {
                    mb.enabled = false;
                }
            }

            var allCanvases = FindObjectsByType<Canvas>(FindObjectsSortMode.None);
            foreach (var c in allCanvases)
            {
                if (c.gameObject.name != "StreamCanvas")
                    c.enabled = false;
            }

            foreach (var img in FindObjectsByType<UnityEngine.UI.RawImage>(FindObjectsSortMode.None))
                img.enabled = false;

            foreach (var lr in FindObjectsByType<LineRenderer>(FindObjectsSortMode.None))
                lr.enabled = false;

            foreach (var mr in FindObjectsByType<MeshRenderer>(FindObjectsSortMode.None))
            {
                var goName = mr.gameObject.name.ToLower();
                if (goName.Contains("controller") || goName.Contains("hand") || goName.Contains("touch"))
                    continue;
                mr.enabled = false;
            }
        }

        void Update()
        {
            if (_tcp == null || _cam == null || !_cam.IsPlaying) return;

            var err = _tcp.LastError;
            if (!string.IsNullOrEmpty(err))
            {
                Disconnect();
                OnError?.Invoke(err);
                return;
            }

            if (Time.time - _lastCaptureTime < _captureInterval) return;
            _lastCaptureTime = Time.time;

            TryReadbackDepth();
            CaptureFrame();
            ForwardEncoded();

            if (Time.time - _lastLogTime > 5f)
            {
                _lastLogTime = Time.time;
                Debug.LogWarning($"[Pipeline] captured={_capturedCount} encoderOut={_encoderOutCount} " +
                                 $"sent={_tcp?.SentFrames} queue={_tcp?.QueuedFrames} " +
                                 $"depth={_latestSnapshot.DepthWidth}x{_latestSnapshot.DepthHeight} connected={_tcp?.IsConnected}");
            }
        }

        void CaptureFrame()
        {
            // Everything comes from the snapshot — RGB, depth, pose, intrinsics
            // are all captured at the same instant (depth readback request time).
            if (!_latestSnapshot.Valid) return;

            int w = _latestSnapshot.RgbWidth;
            int h = _latestSnapshot.RgbHeight;
            if (w <= 0 || h <= 0) return;

            if (!_encoderReady)
            {
                _encoder = new HardwareH265Encoder(w, h, 8_000_000, 30);
                _encoder.Start();
                _encoderReady = true;
                Debug.LogWarning($"[Orchestrator] Encoder init: {w}x{h}");
            }

            var frame = new FrameData
            {
                H265Bytes = _latestSnapshot.RgbNv12,
                ImageWidth = w,
                ImageHeight = h,
                Fx = _latestSnapshot.RgbFx,
                Fy = _latestSnapshot.RgbFy,
                Cx = _latestSnapshot.RgbCx,
                Cy = _latestSnapshot.RgbCy,
                FrameNumber = _frameNumber++,
                TimestampNs = (long)(Time.realtimeSinceStartupAsDouble * 1_000_000_000),
                Pose = _latestSnapshot.HeadPose,
                DepthBytes = _latestSnapshot.DepthBytes,
                DepthWidth = _latestSnapshot.DepthWidth,
                DepthHeight = _latestSnapshot.DepthHeight,
                DepthNearZ = _latestSnapshot.NearZ,
                DepthFarZ = _latestSnapshot.FarZ,
            };

            if (_latestSnapshot.HasDepthIntrinsics)
            {
                frame.HasDepthIntrinsics = true;
                frame.DepthFx = _latestSnapshot.DepthFx;
                frame.DepthFy = _latestSnapshot.DepthFy;
                frame.DepthCx = _latestSnapshot.DepthCx;
                frame.DepthCy = _latestSnapshot.DepthCy;
                frame.DepthPose = _latestSnapshot.DepthCameraPose;
            }

            _encoder.Enqueue(frame);
            _capturedCount++;
        }

        void ForwardEncoded()
        {
            if (_encoder == null || _tcp == null) return;
            while (_encoder.OutputQueue.TryDequeue(out var f))
            {
                _encoderOutCount++;
                _tcp.Enqueue(f);
            }
        }

        static byte[] RgbaToNv12(NativeArray<Color32> rgba, int w, int h)
        {
            int ySize = w * h;
            int uvSize = (w / 2) * (h / 2) * 2;
            byte[] nv12 = new byte[ySize + uvSize];

            for (int row = 0; row < h; row++)
            {
                int srcRow = h - 1 - row;
                for (int col = 0; col < w; col++)
                {
                    var c = rgba[srcRow * w + col];
                    nv12[row * w + col] = (byte)Mathf.Clamp(((66 * c.r + 129 * c.g + 25 * c.b + 128) >> 8) + 16, 0, 255);
                }
            }

            int uvIdx = ySize;
            for (int row = 0; row < h; row += 2)
            {
                int srcRow = h - 1 - row;
                for (int col = 0; col < w; col += 2)
                {
                    var c = rgba[srcRow * w + col];
                    nv12[uvIdx++] = (byte)Mathf.Clamp(((-38 * c.r - 74 * c.g + 112 * c.b + 128) >> 8) + 128, 0, 255);
                    nv12[uvIdx++] = (byte)Mathf.Clamp(((112 * c.r - 94 * c.g - 18 * c.b + 128) >> 8) + 128, 0, 255);
                }
            }

            return nv12;
        }

        void OnDestroy() => Disconnect();
    }
}
