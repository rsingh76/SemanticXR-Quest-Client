using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using SemanticXR.Encoding;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Rendering;

namespace SemanticXR.Streaming
{
    public enum FramesTransport { Tcp, Grpc }

    public class StreamingOrchestrator : MonoBehaviour
    {
        [Header("Transport")]
        [SerializeField] FramesTransport transport = FramesTransport.Grpc;
        public FramesTransport Transport { get => transport; set => transport = value; }

        PassthroughCameraAccess _cam;
        HardwareH265Encoder _encoder;
        IFramesClient _tcp;     // name kept for minimal churn; actually holds any IFramesClient

        int _frameNumber;
        float _captureInterval;
        float _lastCaptureTime;
        bool _encoderReady;
        int _capturedCount, _encoderOutCount;
        float _lastLogTime;

        // --- Drop counters & validation ---
        int _droppedNoPose;
        int _droppedNoTimestamp;
        int _droppedBadIntrinsics;
        int _droppedNoRgb;
        int _droppedNoDepth;
        bool _startupValidated;
        string _validationError;        // non-null = hard fail, will not stream

        // Depth capture
        EnvironmentDepthManager _depthManager;
        bool _depthReadbackPending;

        // Reflection handles for fields not exposed by Meta's public API
        FieldInfo _frameDescField;          // EnvironmentDepthManager.frameDescriptors (internal array)
        FieldInfo _frameDescCreateTimeField; // DepthFrameDesc.createTime (may not exist on older SDKs)
        FieldInfo _camTimestampNsField;      // PassthroughCameraAccess._timestampNsMonotonic (private)

        // NOTE: We considered using MRUKNativeFuncs.GetHeadsetPoseAtTime(long ns) which takes
        // nanoseconds directly with zero precision loss. However, Meta's own GetCameraPose()
        // never calls it — they only use it as a "native library loaded?" guard, then call
        // OVRPlugin.GetNodePoseStateAtTime instead. GetHeadsetPoseAtTime is undocumented and
        // may return in OpenXR RH tracking space (not Unity LH world space). We use
        // ovrp_GetNodePoseStateAtTime(double) exclusively — same native function as Meta's
        // GetCameraPose() but with double precision instead of float.

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

        // Drop counters (read by UI)
        public int DroppedNoPose => _droppedNoPose;
        public int DroppedNoTimestamp => _droppedNoTimestamp;
        public int DroppedBadIntrinsics => _droppedBadIntrinsics;
        public int DroppedNoRgb => _droppedNoRgb;
        public int DroppedNoDepth => _droppedNoDepth;
        public int TotalDropped => _droppedNoPose + _droppedNoTimestamp + _droppedBadIntrinsics + _droppedNoRgb + _droppedNoDepth;
        public string ValidationError => _validationError;
        public string PoseMethod { get; private set; } = "unknown";

        // --- Client-side timing stats (rolling average over StatsWindow frames) ---
        const int StatsWindow = 30;
        int _statsFrames;
        double _tRgb, _tNv12, _tEnc, _tTcp;   // summed ms over the window
        double _tDepthCb;                     // summed ms (async callback latency)
        int _depthCbN;                        // samples collected (async, may differ from _statsFrames)
        double _loopIntervalSum;              // summed ms between consecutive captures
        double _lastLoopTime = -1.0;

        public string TimingLine { get; private set; } = "warmup...";
        // Latest rolling-average client capture rate (frames/sec). Updated alongside
        // TimingLine. 0 until the first stats window completes.
        public float CaptureFps { get; private set; }

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

            _tcp = transport == FramesTransport.Grpc
                ? (IFramesClient)new GrpcFramesClient(address, port, fps)
                : new TcpProtoClient(address, port, fps);
            Debug.LogWarning($"[Orchestrator] Frames transport = {transport}");
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

                // createPoseLocation/createPoseRotation are in OpenXR TRACKING SPACE,
                // NOT Unity world space. The RGB camera pose (from ovrp_GetNodePoseStateAtTime)
                // IS in Unity world space. To make them consistent, we must transform the
                // depth pose from tracking space to world space using the OVRCameraRig's
                // trackingSpace transform.
                var trackingToWorld = Matrix4x4.identity;
                var rig = FindAnyObjectByType<OVRCameraRig>();
                if (rig != null && rig.trackingSpace != null)
                    trackingToWorld = rig.trackingSpace.localToWorldMatrix;

                var depthPoseLocal = Matrix4x4.TRS(poseLoc, poseRot, Vector3.one);
                depthPose = trackingToWorld * depthPoseLocal;

                if (_capturedCount <= 3)
                    Debug.LogWarning($"[Depth] trackingToWorld: pos=({trackingToWorld.GetPosition().x:F3},{trackingToWorld.GetPosition().y:F3},{trackingToWorld.GetPosition().z:F3})");

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

            // Timing: mark the start of the synchronous RGB phase
            double rgbStart = Time.realtimeSinceStartupAsDouble;
            if (_cam != null && _cam.IsPlaying)
            {
                var res = _cam.CurrentResolution;
                if (res.x > 0 && res.y > 0)
                {
                    try
                    {
                        // --- Read timestamp BEFORE GetColors() ---
                        // PassthroughCameraAccess.Update() sets both _texture and
                        // _timestampNsMonotonic in the SAME call to CameraGetLatestImage().
                        // Unity runs all Update() sequentially on one thread, so no other
                        // Update() can interleave between our reads here. Reading the
                        // timestamp before AND after GetColors() lets us verify no PCA
                        // Update() snuck in between (which can't happen, but we check).
                        long tsBeforeReadback = 0;
                        if (_camTimestampNsField != null)
                        {
                            try { tsBeforeReadback = (long)_camTimestampNsField.GetValue(_cam); }
                            catch { }
                        }

                        var colors = _cam.GetColors();
                        if (colors.IsCreated && colors.Length > 0)
                        {
                            rgbW = res.x;
                            rgbH = res.y;
                            double nv12Start = Time.realtimeSinceStartupAsDouble;
                            rgbNv12 = RgbaToNv12(colors, rgbW, rgbH);
                            _tNv12 += (Time.realtimeSinceStartupAsDouble - nv12Start) * 1000.0;
                            hasRgb = true;

                            var intr = _cam.Intrinsics;
                            if (intr.FocalLength.x > 0)
                            {
                                // Convert intrinsics from SENSOR space to IMAGE space.
                                // PrincipalPoint and FocalLength are in full-sensor pixel coords,
                                // but GetColors() returns a cropped/scaled region (CurrentResolution).
                                // Must apply the same CalcSensorCropRegion transform that
                                // ViewportPointToRay uses internally (see PassthroughCameraAccess.cs:546).
                                var sensorRes = (Vector2)intr.SensorResolution;
                                var currentRes = (Vector2)res;
                                var scaleFactor = currentRes / sensorRes;
                                scaleFactor /= Mathf.Max(scaleFactor.x, scaleFactor.y);
                                var cropX = sensorRes.x * (1f - scaleFactor.x) * 0.5f;
                                var cropY = sensorRes.y * (1f - scaleFactor.y) * 0.5f;
                                var cropW = sensorRes.x * scaleFactor.x;
                                var cropH = sensorRes.y * scaleFactor.y;

                                // Map sensor-space intrinsics to image-space
                                rgbFx = intr.FocalLength.x * currentRes.x / cropW;
                                rgbFy = intr.FocalLength.y * currentRes.y / cropH;
                                rgbCx = (intr.PrincipalPoint.x - cropX) / cropW * currentRes.x;
                                rgbCy = (intr.PrincipalPoint.y - cropY) / cropH * currentRes.y;

                                if (_capturedCount <= 3)
                                    Debug.LogWarning($"[Intrinsics] sensor={intr.SensorResolution} current={res} " +
                                        $"crop=({cropX:F1},{cropY:F1},{cropW:F1},{cropH:F1}) " +
                                        $"raw: fx={intr.FocalLength.x:F1} cx={intr.PrincipalPoint.x:F1} cy={intr.PrincipalPoint.y:F1} " +
                                        $"adjusted: fx={rgbFx:F1} cx={rgbCx:F1} cy={rgbCy:F1}");
                            }

                            // RGB capture timestamp — read again and verify it matches
                            // the pre-readback value (proves no PCA.Update() ran in between)
                            if (_camTimestampNsField != null)
                            {
                                try
                                {
                                    rgbTsNs = (long)_camTimestampNsField.GetValue(_cam);
                                    if (tsBeforeReadback != 0 && rgbTsNs != tsBeforeReadback)
                                    {
                                        // This should never happen (single-threaded Update),
                                        // but if it does, the pixels and timestamp don't match
                                        Debug.LogError($"[SYNC] Timestamp changed during GetColors()! " +
                                            $"before={tsBeforeReadback} after={rgbTsNs} — frame dropped");
                                        hasRgb = false;
                                    }
                                }
                                catch { }
                            }

                            // Physical RGB sensor pose with lens offset (Unity world space).
                            //
                            // The image and _timestampNsMonotonic come from a SINGLE native call
                            // (CameraGetLatestImage in PassthroughCameraAccess.Update).
                            // We query the pose at that exact timestamp — this is the ONLY way
                            // Meta's API provides pose+image sync (there is no atomic API
                            // that returns both). The timestamp IS the sync mechanism.
                            //
                            // We use the native GetHeadsetPoseAtTime(long ns) which takes
                            // nanoseconds directly (zero precision loss), NOT Meta's
                            // GetCameraPose() which converts to float32 first (lossy).
                            if (rgbTsNs > 0 && TryGetPoseAtImageTimestamp(rgbTsNs, out var pose))
                            {
                                rgbCamPose = Matrix4x4.TRS(pose.position, pose.rotation, Vector3.one);
                                hasRgbCamPose = true;
                            }
                        }
                    }
                    catch { /* passthrough not ready yet */ }
                }
            }
            _tRgb += (Time.realtimeSinceStartupAsDouble - rgbStart) * 1000.0;

            _depthReadbackPending = true;
            double depthReqStart = Time.realtimeSinceStartupAsDouble;

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
                    _tDepthCb += (Time.realtimeSinceStartupAsDouble - depthReqStart) * 1000.0;
                    _depthCbN++;

                    if (request.hasError) return;

                    var data = request.GetData<byte>();

                    // Strip to R channel + flip vertically in one pass.
                    //
                    // Source: R16G16B16A16_SFloat (8 bytes/pixel), bottom-up (OpenGL order).
                    //   R = left-eye inverted NDC depth   <- the only channel we use
                    //   G = right-eye                     <- unused (we're a left-camera capture)
                    //   B, A = edge softness for soft occlusion  <- unused
                    //
                    // Dropping G/B/A cuts depth bandwidth by 4x (800KB -> 200KB per frame
                    // at 320x320). We also flip to top-down here so server/consumers can
                    // use cy = tanT * fy directly.
                    //
                    // Output: R16_SFloat, 2 bytes/pixel, top-down.
                    int w = request.width, h = request.height;
                    byte[] depthR = new byte[w * h * 2];
                    for (int r = 0; r < h; r++)
                    {
                        int srcBase = (h - 1 - r) * w * 8; // bottom-up source row
                        int dstBase = r * w * 2;           // top-down target row
                        for (int c = 0; c < w; c++)
                        {
                            depthR[dstBase + c * 2]     = data[srcBase + c * 8];
                            depthR[dstBase + c * 2 + 1] = data[srcBase + c * 8 + 1];
                        }
                    }

                    // Co-captured frame: depth + RGB + poses + timestamps from request time.
                    // Gating: we accept the frame if depth is present. RGB is optional —
                    // a frame may be depth-only if RGB readback failed transiently.
                    _latestCapture = new CoCapturedFrame
                    {
                        DepthBytes = depthR,
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

        // ---- Pose lookup that matches the image timestamp exactly ----
        //
        // Architecture: PassthroughCameraAccess.Update() calls a single native function
        // (CameraGetLatestImage) that returns BOTH the GPU texture AND the nanosecond
        // timestamp (_timestampNsMonotonic) atomically. GetColors() just reads that
        // already-captured texture back to CPU. So the image and timestamp ARE paired.
        //
        // The pose is then queried at that exact timestamp from the tracking system.
        // This is the ONLY way Meta's API works — there is no single call that returns
        // image + pose together. The timestamp IS the synchronization mechanism.
        //
        // We call ovrp_GetNodePoseStateAtTime(double) — the same native function that
        // Meta's GetCameraPose() uses internally, but with DOUBLE precision instead of
        // FLOAT. This gives sub-microsecond timestamp precision vs Meta's ~6ms float loss.
        //
        // Convention: returns head pose in Unity LEFT-HANDED WORLD SPACE (same as
        // Camera.main.transform). We then apply the camera-specific LensOffset to get
        // the physical RGB camera pose.

        [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
        static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(
            double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);

        /// <summary>
        /// Get the RGB camera pose at the exact image capture time.
        /// Uses ovrp_GetNodePoseStateAtTime(double) — same function as Meta's GetCameraPose()
        /// but with double precision. Returns pose in Unity LH world space.
        /// Applies the physical LensOffset for the active camera (left or right).
        /// </summary>
        bool TryGetPoseAtImageTimestamp(long timestampNsMonotonic, out Pose cameraPose)
        {
            cameraPose = default;
            if (timestampNsMonotonic <= 0) return false;

            // Convert ns → seconds with DOUBLE precision (not float!)
            // Meta's GetCameraPose() does _timestampNsMonotonic * 1e-9f (float) → ~6ms loss
            // We do timestampNsMonotonic * 1e-9 (double) → sub-microsecond precision
            double timeSec = timestampNsMonotonic * 1e-9;

            if (!ovrp_GetNodePoseStateAtTime(timeSec, OVRPlugin.Node.Head,
                    out OVRPlugin.PoseStatef poseState).IsSuccess())
                return false;

            // OVRPlugin returns in Unity LH world space (SDK handles RH→LH internally)
            var headPose = poseState.Pose.ToOVRPose();

            // Apply physical lens offset for THIS camera (left or right)
            // Same logic as Meta's PassthroughCameraAccess.GetCameraPose() line 571-573
            if (_cam != null)
            {
                var lensOffset = _cam.Intrinsics.LensOffset;
                cameraPose = new Pose(
                    headPose.position + headPose.orientation * lensOffset.position,
                    headPose.orientation * lensOffset.rotation);
            }
            else
            {
                cameraPose = new Pose(headPose.position, headPose.orientation);
            }

            PoseMethod = "OVRPlugin-double";

            if (_capturedCount <= 3)
                Debug.LogWarning($"[Pose] tsNs={timestampNsMonotonic} timeSec={timeSec:F9} " +
                    $"head=({headPose.position.x:F4},{headPose.position.y:F4},{headPose.position.z:F4}) " +
                    $"cam=({cameraPose.position.x:F4},{cameraPose.position.y:F4},{cameraPose.position.z:F4})");

            return true;
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

        /// <summary>
        /// One-time startup validation after camera warmup (~1 second).
        /// Checks that all required subsystems are functional. Sets _validationError
        /// on hard failure (streaming will refuse to send frames).
        /// </summary>
        void TryStartupValidation()
        {
            if (_startupValidated) return;
            if (_cam == null || !_cam.IsPlaying) return;

            // Wait a bit for the camera to warm up (at least 30 frames or 1 second)
            if (Time.frameCount < 30 && Time.realtimeSinceStartup < 2f) return;
            _startupValidated = true;

            var errors = new System.Collections.Generic.List<string>();

            // 1. Camera resolution
            var res = _cam.CurrentResolution;
            if (res.x <= 0 || res.y <= 0)
                errors.Add($"Camera resolution invalid: {res.x}x{res.y}");

            // 2. RGB intrinsics
            var intr = _cam.Intrinsics;
            if (intr.FocalLength.x <= 0 || intr.FocalLength.y <= 0)
                errors.Add($"Intrinsics focal length invalid: fx={intr.FocalLength.x:F1} fy={intr.FocalLength.y:F1}");
            else if (res.x > 0 && res.y > 0)
            {
                // Principal point must be inside image
                if (intr.PrincipalPoint.x < 0 || intr.PrincipalPoint.x >= res.x ||
                    intr.PrincipalPoint.y < 0 || intr.PrincipalPoint.y >= res.y)
                    errors.Add($"Intrinsics principal point outside image: cx={intr.PrincipalPoint.x:F1} cy={intr.PrincipalPoint.y:F1} in {res.x}x{res.y}");
            }

            // 3. Timestamp reflection
            if (_camTimestampNsField == null)
                errors.Add("_timestampNsMonotonic reflection failed -- cannot sync pose to image");

            // 4. Pose lookup availability (OVRPlugin P/Invoke with double precision)
            try
            {
                ovrp_GetNodePoseStateAtTime(0.0, OVRPlugin.Node.Head, out _);
                PoseMethod = "OVRPlugin-double";
            }
            catch
            {
                errors.Add("ovrp_GetNodePoseStateAtTime P/Invoke failed -- pose lookup unavailable");
            }

            // 5. Try a real pose lookup with the current timestamp
            if (_camTimestampNsField != null && errors.Count == 0)
            {
                try
                {
                    long tsNs = (long)_camTimestampNsField.GetValue(_cam);
                    if (tsNs > 0)
                    {
                        if (!TryGetPoseAtImageTimestamp(tsNs, out _))
                            errors.Add($"Pose lookup failed for timestamp {tsNs}ns");
                    }
                    else
                    {
                        errors.Add($"Timestamp is zero after warmup -- camera not delivering timestamps");
                    }
                }
                catch (Exception ex)
                {
                    errors.Add($"Timestamp read failed: {ex.Message}");
                }
            }

            if (errors.Count > 0)
            {
                _validationError = string.Join("\n", errors);
                Debug.LogError($"[Validation] HARD FAIL -- will not stream:\n{_validationError}");
            }
            else
            {
                Debug.LogWarning($"[Validation] PASSED: res={res.x}x{res.y} " +
                    $"fx={intr.FocalLength.x:F1} fy={intr.FocalLength.y:F1} " +
                    $"cx={intr.PrincipalPoint.x:F1} cy={intr.PrincipalPoint.y:F1} " +
                    $"poseMethod={PoseMethod}");
            }
        }

        void Update()
        {
            if (_tcp == null || _cam == null || !_cam.IsPlaying) return;

            // Run one-time validation after camera warmup
            TryStartupValidation();

            var err = _tcp.LastError;
            if (!string.IsNullOrEmpty(err))
            {
                Disconnect();
                OnError?.Invoke(err);
                return;
            }

            // Hard validation failure — still run Update for UI/disconnect but don't capture
            if (_validationError != null) return;

            if (Time.time - _lastCaptureTime < _captureInterval) return;
            _lastCaptureTime = Time.time;

            // Track wall-clock between consecutive captures (true client FPS).
            double nowReal = Time.realtimeSinceStartupAsDouble;
            if (_lastLoopTime > 0) _loopIntervalSum += (nowReal - _lastLoopTime) * 1000.0;
            _lastLoopTime = nowReal;

            TryReadbackDepth();
            CaptureFrame();
            ForwardEncoded();

            _statsFrames++;
            if (_statsFrames >= StatsWindow)
            {
                int n = _statsFrames;
                double loopAvg = n > 1 ? _loopIntervalSum / (n - 1) : 0;
                double effFps = loopAvg > 0 ? 1000.0 / loopAvg : 0;
                double depthAvg = _depthCbN > 0 ? _tDepthCb / _depthCbN : 0;
                TimingLine = $"rgb={_tRgb/n:F0} nv12={_tNv12/n:F0} depth_cb={depthAvg:F0} " +
                             $"enc={_tEnc/n:F1} tcp={_tTcp/n:F1} | loop={loopAvg:F0}ms eff={effFps:F1}fps";
                CaptureFps = (float)effFps;
                Debug.LogWarning($"[Timing avg/{n}] {TimingLine}");
                _tRgb = _tNv12 = _tEnc = _tTcp = 0;
                _tDepthCb = 0; _depthCbN = 0;
                _loopIntervalSum = 0;
                _statsFrames = 0;
            }

            if (Time.time - _lastLogTime > 5f)
            {
                _lastLogTime = Time.time;
                int td = _droppedNoPose + _droppedNoTimestamp + _droppedBadIntrinsics + _droppedNoRgb + _droppedNoDepth;
                Debug.LogWarning($"[Pipeline] captured={_capturedCount} encoderOut={_encoderOutCount} " +
                                 $"sent={_tcp?.SentFrames} queue={_tcp?.QueuedFrames} " +
                                 $"depth={_latestCapture.DepthWidth}x{_latestCapture.DepthHeight} connected={_tcp?.IsConnected}" +
                                 (td > 0 ? $" DROPPED={td}(pose={_droppedNoPose} ts={_droppedNoTimestamp} intr={_droppedBadIntrinsics} rgb={_droppedNoRgb} depth={_droppedNoDepth})" : ""));
            }
        }

        void CaptureFrame()
        {
            // Hard validation failure — don't attempt anything
            if (_validationError != null) return;

            var c = _latestCapture;

            // --- Strict gating: every field needed for RGB-D reconstruction must be present ---

            if (!c.HasDepth)  { _droppedNoDepth++; return; }
            if (!c.HasRgb)    { _droppedNoRgb++; return; }

            // Pose MUST come from the image timestamp path (not Camera.main)
            if (!c.HasRgbCameraPose)  { _droppedNoPose++; return; }
            if (c.RgbTimestampNs <= 0) { _droppedNoTimestamp++; return; }

            // Intrinsics sanity: fx/fy positive, principal point inside image
            int w = c.RgbWidth, h = c.RgbHeight;
            if (w <= 0 || h <= 0) { _droppedBadIntrinsics++; return; }
            if (c.RgbFx <= 0 || c.RgbFy <= 0 ||
                c.RgbCx < 0 || c.RgbCx >= w ||
                c.RgbCy < 0 || c.RgbCy >= h)
            {
                _droppedBadIntrinsics++;
                return;
            }

            if (!_encoderReady)
            {
                _encoder = new HardwareH265Encoder(w, h, 5_000_000, 30);
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

            double encStart = Time.realtimeSinceStartupAsDouble;
            _encoder.Enqueue(frame);
            _tEnc += (Time.realtimeSinceStartupAsDouble - encStart) * 1000.0;
            _capturedCount++;
        }

        void ForwardEncoded()
        {
            if (_encoder == null || _tcp == null) return;
            while (_encoder.OutputQueue.TryDequeue(out var f))
            {
                _encoderOutCount++;
                double tcpStart = Time.realtimeSinceStartupAsDouble;
                _tcp.Enqueue(f);
                _tTcp += (Time.realtimeSinceStartupAsDouble - tcpStart) * 1000.0;
            }
        }

        static byte[] RgbaToNv12(NativeArray<Color32> rgba, int w, int h)
        {
            int ySize = w * h;
            int uvSize = (w / 2) * (h / 2) * 2;
            byte[] nv12 = new byte[ySize + uvSize];

            // Vertical flip: GetColors() returns bottom-up on this stack
            // (decoded JPEGs otherwise come out upside-down). Read source row
            // (h-1-row) when writing destination row.
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
                int srcRow = h - 2 - row;
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
