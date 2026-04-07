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

        // Reflection handles for fields not exposed by Meta's public API
        FieldInfo _frameDescField;          // EnvironmentDepthManager.frameDescriptors (internal array)
        FieldInfo _frameDescCreateTimeField; // DepthFrameDesc.createTime (may not exist on older SDKs)
        FieldInfo _camTimestampNsField;      // PassthroughCameraAccess._timestampNsMonotonic (private)

        // ---------------------------------------------------------------------
        // CoCapturedFrame: best-effort synchronized capture of RGB + depth + poses.
        //
        // This is NOT atomic. RGB (from PassthroughCameraAccess) and depth (from
        // EnvironmentDepthManager) come from independent Meta subsystems with no
        // unified callback. We grab whatever each API has "right now" inside the
        // same Unity Update() tick. The two sensors run at ~25 FPS each, so worst
        // case the data is ~40ms apart. We do NOT currently reject mismatched
        // pairs by timestamp diff — that requires offset-correcting the BOOTTIME
        // (RGB) vs MONOTONIC (depth) clocks, which is a TODO.
        //
        // The fields below are populated when the depth GPU readback callback
        // fires (1-2 frames after the request). RGB is captured at REQUEST time
        // so RGB and depth bytes are from the same Update() — see TryCoCapture().
        // ---------------------------------------------------------------------
        struct CoCapturedFrame
        {
            // Depth (from _PreprocessedEnvironmentDepthTexture, R16G16B16A16_SFloat)
            public byte[] DepthBytes;
            public int DepthWidth, DepthHeight;
            public float NearZ, FarZ;
            public float DepthFx, DepthFy, DepthCx, DepthCy;
            public float DepthFovTanLeft, DepthFovTanRight, DepthFovTanTop, DepthFovTanDown;
            public Matrix4x4 DepthCameraPose;       // From EnvironmentDepthFrameDesc (tracking space, NOT Unity world)
            public long DepthTimestampNs;           // createTime in ns (0 if OVRCameraRig absent — Meta SDK quirk)
            public bool HasDepthIntrinsics;

            // RGB (from PassthroughCameraAccess)
            public byte[] RgbNv12;
            public int RgbWidth, RgbHeight;
            public float RgbFx, RgbFy, RgbCx, RgbCy;
            public Matrix4x4 RgbCameraPose;         // GetCameraPose() — physical sensor with lens offset (Unity world)
            public bool HasRgbCameraPose;
            public long RgbTimestampNs;             // Camera2 SENSOR_TIMESTAMP (CLOCK_BOOTTIME ns)

            // Common
            public Matrix4x4 HeadPose;              // Camera.main eye center (Unity world)

            public bool HasDepth;
            public bool HasRgb;
        }
        CoCapturedFrame _latestCapture;

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

            // Try to find createTime on the descriptor element type — used to read the depth
            // sensor capture timestamp (XrTime). NOTE: this returns 0 unless OVRCameraRig is in
            // the scene (known Meta SDK bug), see README.
            try
            {
                var elemType = _frameDescField?.FieldType.GetElementType();
                if (elemType != null)
                {
                    _frameDescCreateTimeField = elemType.GetField("createTime",
                        BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
                    Debug.LogWarning(_frameDescCreateTimeField != null
                        ? "[Orchestrator] Found DepthFrameDesc.createTime field"
                        : "[Orchestrator] DepthFrameDesc.createTime not found — depth timestamps unavailable");
                }
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[Orchestrator] createTime reflection lookup failed: {ex.Message}");
            }

            // Cache reflection access to PassthroughCameraAccess._timestampNsMonotonic
            // (private long, gives nanosecond-precision RGB capture time in CLOCK_BOOTTIME).
            if (_cam != null)
            {
                _camTimestampNsField = _cam.GetType().GetField("_timestampNsMonotonic",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Debug.LogWarning(_camTimestampNsField != null
                    ? "[Orchestrator] Found PassthroughCameraAccess._timestampNsMonotonic field"
                    : "[Orchestrator] _timestampNsMonotonic not found — RGB nanosecond timestamps unavailable");
            }

            // OVRCameraRig must exist in the scene or DepthFrameDesc.createTime returns 0
            // (known Meta SDK quirk — the rig is what pumps the XR frame timing into the
            // depth subsystem). If the project's scene didn't add one, spawn a hidden,
            // disabled rig so the depth manager populates createTime without affecting
            // tracking or rendering (we use OpenXR / Camera.main for those).
            if (FindAnyObjectByType<OVRCameraRig>() == null)
            {
                var rigGo = new GameObject("OVRCameraRig (auto-spawned for depth timestamps)");
                rigGo.SetActive(false);
                rigGo.AddComponent<OVRCameraRig>();
                rigGo.SetActive(true);
                Debug.LogWarning("[Orchestrator] Auto-spawned OVRCameraRig so DepthFrameDesc.createTime is populated");
            }

            Debug.LogWarning("[Orchestrator] Depth capture setup complete");
        }

        /// <summary>
        /// Read depth camera FOV tangents, derived pinhole intrinsics, depth-camera pose,
        /// and createTime from EnvironmentDepthManager's internal frameDescriptors[0] via reflection.
        ///
        /// FOV tangents are the SOURCE of truth from Meta's SDK. The pinhole intrinsics
        /// (fx, fy, cx, cy) are derived via the standard pinhole model:
        ///   fx = width  / (tanRight + tanLeft)
        ///   fy = height / (tanTop   + tanDown)
        ///   cx = tanLeft * fx
        ///   cy = tanTop  * fy   (cy from top because the server flips the image vertically)
        /// Both are stored on the wire so the server can verify or use a non-pinhole model.
        ///
        /// Pose: createPoseLocation/createPoseRotation are in OpenXR tracking space (NOT Unity world).
        /// createTime is XrTime (CLOCK_MONOTONIC ns); returns 0 unless OVRCameraRig is in the scene.
        /// </summary>
        bool TryReadDepthFrameDesc(int depthW, int depthH,
                                   out float tanL, out float tanR, out float tanT, out float tanD,
                                   out float dfx, out float dfy, out float dcx, out float dcy,
                                   out Matrix4x4 depthPose, out long createTimeNs)
        {
            tanL = tanR = tanT = tanD = 0;
            dfx = dfy = dcx = dcy = 0;
            depthPose = Matrix4x4.identity;
            createTimeNs = 0;

            if (_depthManager == null || _frameDescField == null) return false;

            try
            {
                var descs = _frameDescField.GetValue(_depthManager) as Array;
                if (descs == null || descs.Length == 0) return false;

                var desc = descs.GetValue(0);
                var descType = desc.GetType();

                tanL = (float)descType.GetField("fovLeftAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                tanR = (float)descType.GetField("fovRightAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                tanT = (float)descType.GetField("fovTopAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                tanD = (float)descType.GetField("fovDownAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);

                Vector3 poseLoc = (Vector3)descType.GetField("createPoseLocation",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                Quaternion poseRot = (Quaternion)descType.GetField("createPoseRotation",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);

                if (tanL <= 0 || tanR <= 0) return false;

                float w = depthW > 0 ? depthW : 320;
                float h = depthH > 0 ? depthH : 320;

                dfx = w / (tanR + tanL);
                dfy = h / (tanT + tanD);
                dcx = tanL * dfx;
                dcy = tanT * dfy;

                // NOTE: this pose is in OpenXR tracking space, NOT Unity world space.
                // To use it next to RGB camera pose (which IS Unity world), it must be
                // transformed by the XROrigin/OVRCameraRig.trackingSpace transform.
                // We pass it through as-is and rely on the server to handle the offset
                // (in scenes without an XR origin offset, tracking space ≈ world space).
                depthPose = Matrix4x4.TRS(poseLoc, poseRot, Vector3.one);

                // createTime: depth sensor capture timestamp in seconds (XrTime / 1e9)
                if (_frameDescCreateTimeField != null)
                {
                    try
                    {
                        double createTimeSec = Convert.ToDouble(_frameDescCreateTimeField.GetValue(desc));
                        createTimeNs = (long)(createTimeSec * 1e9);
                    }
                    catch { /* field type changed */ }
                }

                if (_capturedCount <= 3)
                    Debug.LogWarning($"[Depth] tangents L={tanL:F3} R={tanR:F3} T={tanT:F3} D={tanD:F3} " +
                                     $"=> fx={dfx:F1} fy={dfy:F1} cx={dcx:F1} cy={dcy:F1} " +
                                     $"createTimeNs={createTimeNs}");

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
            Matrix4x4 rgbCamPose = Matrix4x4.identity;
            bool hasRgbCamPose = false;
            long rgbTsNs = 0;
            bool hasRgb = false;

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
                            hasRgb = true;

                            var intr = _cam.Intrinsics;
                            if (intr.FocalLength.x > 0)
                            {
                                rgbFx = intr.FocalLength.x;
                                rgbFy = intr.FocalLength.y;
                                rgbCx = intr.PrincipalPoint.x;
                                rgbCy = intr.PrincipalPoint.y;
                            }

                            // Physical RGB sensor pose with lens offset (Unity world space)
                            try
                            {
                                var pose = _cam.GetCameraPose();
                                rgbCamPose = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
                                hasRgbCamPose = true;
                            }
                            catch { /* pose not ready */ }

                            // RGB capture timestamp (CLOCK_BOOTTIME ns) via reflection
                            if (_camTimestampNsField != null)
                            {
                                try { rgbTsNs = (long)_camTimestampNsField.GetValue(_cam); }
                                catch { }
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

            // Head pose at this instant (Camera.main eye center, Unity world)
            var headPose = Camera.main != null ? Camera.main.transform.localToWorldMatrix : Matrix4x4.identity;

            // Depth FOV tangents, derived intrinsics, pose, createTime
            int texW = depthTex.width, texH = depthTex.height;
            bool hasDepthIntr = TryReadDepthFrameDesc(texW, texH,
                out float tanL, out float tanR, out float tanT, out float tanD,
                out float dfx, out float dfy, out float dcx, out float dcy,
                out Matrix4x4 depthCamPose, out long depthTsNs);

            if (_capturedCount <= 3)
                Debug.LogWarning($"[Depth] ZBufferParams=({zbuf.x:G}, {zbuf.y:G}, {zbuf.z:G}, {zbuf.w:G})");

            AsyncGPUReadback.Request(depthTex, 0, 0, depthTex.width, 0, depthTex.height, 0, 1,
                request =>
                {
                    _depthReadbackPending = false;

                    if (request.hasError) return;

                    var data = request.GetData<byte>();

                    // Co-captured frame: depth + RGB + poses + timestamps from request time.
                    // Gating: we accept the frame if depth is present. RGB is optional —
                    // a frame may be depth-only if RGB readback failed transiently.
                    _latestCapture = new CoCapturedFrame
                    {
                        DepthBytes = data.ToArray(),
                        DepthWidth = request.width,
                        DepthHeight = request.height,
                        NearZ = nearZ,
                        FarZ = farZ,
                        DepthFx = dfx, DepthFy = dfy, DepthCx = dcx, DepthCy = dcy,
                        DepthFovTanLeft = tanL, DepthFovTanRight = tanR,
                        DepthFovTanTop = tanT, DepthFovTanDown = tanD,
                        DepthCameraPose = depthCamPose,
                        DepthTimestampNs = depthTsNs,
                        HasDepthIntrinsics = hasDepthIntr,

                        RgbNv12 = rgbNv12,
                        RgbWidth = rgbW, RgbHeight = rgbH,
                        RgbFx = rgbFx, RgbFy = rgbFy, RgbCx = rgbCx, RgbCy = rgbCy,
                        RgbCameraPose = rgbCamPose,
                        HasRgbCameraPose = hasRgbCamPose,
                        RgbTimestampNs = rgbTsNs,

                        HeadPose = headPose,
                        HasDepth = true,
                        HasRgb = hasRgb,
                    };

                    if (_capturedCount <= 3 || _capturedCount % 30 == 0)
                        Debug.LogWarning($"[Depth] Readback: {request.width}x{request.height}, " +
                                         $"{_latestCapture.DepthBytes.Length} bytes, near={nearZ:F3} far={farZ:F3}" +
                                         $", rgb={rgbW}x{rgbH}, rgbTsNs={rgbTsNs}, depthTsNs={depthTsNs}");
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
                                 $"depth={_latestCapture.DepthWidth}x{_latestCapture.DepthHeight} connected={_tcp?.IsConnected}");
            }
        }

        void CaptureFrame()
        {
            // We need at least depth + RGB to send a useful frame. RGB-only or
            // depth-only would force the server to handle missing fields.
            var c = _latestCapture;
            if (!c.HasDepth || !c.HasRgb) return;

            int w = c.RgbWidth, h = c.RgbHeight;
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
                H265Bytes = c.RgbNv12,
                ImageWidth = w, ImageHeight = h,
                Fx = c.RgbFx, Fy = c.RgbFy, Cx = c.RgbCx, Cy = c.RgbCy,
                FrameNumber = _frameNumber++,
                TimestampNs = (long)(Time.realtimeSinceStartupAsDouble * 1_000_000_000),
                RgbTimestampNs = c.RgbTimestampNs,
                DepthTimestampNs = c.DepthTimestampNs,

                DepthBytes = c.DepthBytes,
                DepthWidth = c.DepthWidth, DepthHeight = c.DepthHeight,
                DepthNearZ = c.NearZ, DepthFarZ = c.FarZ,

                HeadPose = c.HeadPose,
                RgbCameraPose = c.RgbCameraPose,
                HasRgbCameraPose = c.HasRgbCameraPose,
                DepthCameraPose = c.DepthCameraPose,
            };

            if (c.HasDepthIntrinsics)
            {
                frame.HasDepthIntrinsics = true;
                frame.DepthFx = c.DepthFx; frame.DepthFy = c.DepthFy;
                frame.DepthCx = c.DepthCx; frame.DepthCy = c.DepthCy;
                frame.DepthFovTanLeft = c.DepthFovTanLeft;
                frame.DepthFovTanRight = c.DepthFovTanRight;
                frame.DepthFovTanTop = c.DepthFovTanTop;
                frame.DepthFovTanDown = c.DepthFovTanDown;
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
