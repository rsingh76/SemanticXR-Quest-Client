using System;
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
        byte[] _latestDepthBytes;
        int _depthWidth, _depthHeight;
        float _depthNearZ, _depthFarZ;
        bool _depthReady;
        bool _depthReadbackPending;

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
            Debug.LogWarning("[Orchestrator] Depth capture setup complete");
        }

        void TryReadbackDepth()
        {
            if (_depthReadbackPending) return;

            // Use preprocessed depth — it has R16G16B16A16_SFloat format that AsyncGPUReadback supports
            // (the raw _EnvironmentDepthTexture is a native OpenXR texture with "None" format)
            var depthTex = Shader.GetGlobalTexture("_PreprocessedEnvironmentDepthTexture") as RenderTexture;
            if (depthTex == null || !depthTex.IsCreated()) return;

            _depthReadbackPending = true;

            // Meta's _EnvironmentDepthZBufferParams from EnvironmentDepthUtils.ComputeNdcToLinearDepthParameters:
            //   x = invDepthFactor = -2*near (infinite far) or -2*far*near/(far-near) (finite far)
            //   y = depthOffset    = -1       (infinite far) or -(far+near)/(far-near) (finite far)
            //   z, w = always 0
            // Conversion: ndc = zbuf_value * 2 - 1; linear_depth = x / (ndc + y)
            var zbuf = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");
            _depthNearZ = zbuf.x;  // send raw x (invDepthFactor) for server-side conversion
            _depthFarZ = zbuf.y;   // send raw y (depthOffset)

            if (_capturedCount <= 3)
                Debug.LogWarning($"[Depth] ZBufferParams=({zbuf.x:G}, {zbuf.y:G}, {zbuf.z:G}, {zbuf.w:G})");

            // Always readback — the texture object doesn't change but its contents do each frame
            AsyncGPUReadback.Request(depthTex, 0, 0, depthTex.width, 0, depthTex.height, 0, 1,
                request =>
                {
                    _depthReadbackPending = false;

                    if (request.hasError)
                    {
                        return;
                    }

                    var data = request.GetData<byte>();
                    _depthWidth = request.width;
                    _depthHeight = request.height;
                    _latestDepthBytes = data.ToArray();
                    _depthReady = true;

                    if (_capturedCount <= 3 || _capturedCount % 30 == 0)
                        Debug.LogWarning($"[Depth] Readback: {_depthWidth}x{_depthHeight}, {_latestDepthBytes.Length} bytes, near={_depthNearZ:F3} far={_depthFarZ:F3}");
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
                Debug.LogWarning($"[Pipeline] captured={_capturedCount} encoderOut={_encoderOutCount} sent={_tcp?.SentFrames} queue={_tcp?.QueuedFrames} depth={_depthWidth}x{_depthHeight} connected={_tcp?.IsConnected}");
            }
        }

        void CaptureFrame()
        {
            var res = _cam.CurrentResolution;
            if (res.x <= 0 || res.y <= 0) return;

            if (!_encoderReady)
            {
                _encoder = new HardwareH265Encoder(res.x, res.y, 8_000_000, 30);
                _encoder.Start();
                _encoderReady = true;
                Debug.LogWarning($"[Orchestrator] Encoder init: {res.x}x{res.y}");
            }

            NativeArray<Color32> colors;
            try { colors = _cam.GetColors(); }
            catch { return; }
            if (!colors.IsCreated || colors.Length == 0) return;

            int w = res.x, h = res.y;
            byte[] nv12 = RgbaToNv12(colors, w, h);

            var pose = Camera.main != null ? Camera.main.transform.localToWorldMatrix : Matrix4x4.identity;

            float fx = 0, fy = 0, cx = 0, cy = 0;
            var intr = _cam.Intrinsics;
            if (intr.FocalLength.x > 0)
            {
                fx = intr.FocalLength.x;
                fy = intr.FocalLength.y;
                cx = intr.PrincipalPoint.x;
                cy = intr.PrincipalPoint.y;
            }

            var frame = new FrameData
            {
                H265Bytes = nv12,
                ImageWidth = w,
                ImageHeight = h,
                Pose = pose,
                Fx = fx, Fy = fy, Cx = cx, Cy = cy,
                FrameNumber = _frameNumber++,
                TimestampNs = (long)(Time.realtimeSinceStartupAsDouble * 1_000_000_000),
            };

            // Attach latest depth if available
            if (_depthReady && _latestDepthBytes != null)
            {
                frame.DepthBytes = _latestDepthBytes;
                frame.DepthWidth = _depthWidth;
                frame.DepthHeight = _depthHeight;
                frame.DepthNearZ = _depthNearZ;
                frame.DepthFarZ = _depthFarZ;
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
