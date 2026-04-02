using System;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Threading;
using Grpc.Core;
using Grpc.Net.Client;
using UnityEngine;
using Xr;

namespace SemanticXR.Streaming
{
    public class GrpcStreamingClient : IDisposable
    {
        readonly string _address;
        readonly int _port;
        readonly int _fps;

        GrpcChannel _channel;
        XrService.XrServiceClient _client;
        AsyncClientStreamingCall<UpstreamSyncMessage_quest, VideoStatus> _questStreamCall;

        readonly ConcurrentQueue<FrameData> _sendQueue = new();
        Thread _sendThread;
        volatile bool _running;
        int _sendQueueCount;
        const int MaxQueueSize = 5;

        public int QueuedFrames => _sendQueueCount;
        public bool IsConnected { get; private set; }
        public int SentFrames { get; private set; }

        volatile string _lastError;
        public string LastError
        {
            get { var e = _lastError; _lastError = null; return e; }
        }

        public GrpcStreamingClient(string address, int port, int fps)
        {
            _address = address;
            _port = port;
            _fps = fps;
        }

        public void Start()
        {
            _running = true;
            _sendThread = new Thread(SendLoop) { Name = "GrpcSend", IsBackground = true };
            _sendThread.Start();
        }

        public void Enqueue(FrameData frame)
        {
            while (_sendQueueCount >= MaxQueueSize && _sendQueue.TryDequeue(out _))
                Interlocked.Decrement(ref _sendQueueCount);
            _sendQueue.Enqueue(frame);
            Interlocked.Increment(ref _sendQueueCount);
        }

        void SendLoop()
        {
            try
            {
                // TCP health check
                Debug.LogWarning($"[gRPC] Health check: {_address}:{_port}...");
                if (!TcpCheck(_address, _port, 8000))
                {
                    _lastError = $"Cannot reach {_address}:{_port}. Check IP/port.";
                    Debug.LogError($"[gRPC] {_lastError}");
                    return;
                }

                // Open channel
                _channel = GrpcChannel.ForAddress($"http://{_address}:{_port}", new GrpcChannelOptions
                {
                    MaxSendMessageSize = 100 * 1024 * 1024,
                });
                _client = new XrService.XrServiceClient(_channel);
                _questStreamCall = _client.UploadSyncMessage_quest();
                Debug.LogWarning($"[gRPC] Channel open, waiting for frames...");

                while (_running)
                {
                    if (_sendQueue.TryDequeue(out var frame))
                    {
                        Interlocked.Decrement(ref _sendQueueCount);

                        if (!IsConnected)
                        {
                            // First send with timeout
                            var task = _questStreamCall.RequestStream.WriteAsync(BuildMessage(frame));
                            var delay = System.Threading.Tasks.Task.Delay(10000);
                            var done = System.Threading.Tasks.Task.WhenAny(task, delay).Result;
                            if (done == delay || !task.IsCompleted)
                            {
                                _lastError = $"Connection timed out (10s). Server at {_address}:{_port} not responding.";
                                Debug.LogError($"[gRPC] {_lastError}");
                                return;
                            }
                            if (task.IsFaulted) throw task.Exception.InnerException ?? task.Exception;
                            IsConnected = true;
                            Debug.LogWarning($"[gRPC] Connected to {_address}:{_port}");
                        }
                        else
                        {
                            _questStreamCall.RequestStream.WriteAsync(BuildMessage(frame)).Wait();
                        }
                        SentFrames++;
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            catch (RpcException ex)
            {
                IsConnected = false;
                _lastError = $"gRPC error: {ex.Status.StatusCode} — {ex.Status.Detail ?? ex.Message}";
                Debug.LogError($"[gRPC] {_lastError}");
            }
            catch (Exception ex)
            {
                IsConnected = false;
                var inner = ex; while (inner.InnerException != null) inner = inner.InnerException;
                if (inner.Message.Contains("refused")) _lastError = $"Connection refused at {_address}:{_port}.";
                else if (inner.Message.Contains("timed out")) _lastError = $"Connection timed out at {_address}:{_port}.";
                else _lastError = $"Connection failed: {inner.Message}";
                Debug.LogError($"[gRPC] {_lastError}");
            }
            finally
            {
                try { _questStreamCall?.Dispose(); } catch { }
                try { _channel?.Dispose(); } catch { }
                IsConnected = false;
            }
        }

        UpstreamSyncMessage_quest BuildMessage(FrameData f)
        {
            var msg = new UpstreamSyncMessage_quest
            {
                Image = new VideoFrame
                {
                    DataH265 = Google.Protobuf.ByteString.CopyFrom(f.H265Bytes ?? Array.Empty<byte>()),
                    FrameNumber = f.FrameNumber,
                    TimestampUs = f.TimestampNs / 1000,
                },
                Depth = Google.Protobuf.ByteString.CopyFrom(f.DepthBytes ?? Array.Empty<byte>()),
                ImageWidth = f.ImageWidth,
                ImageHeight = f.ImageHeight,
                DepthWidth = f.DepthWidth,
                DepthHeight = f.DepthHeight,
                Fps = _fps,
                TimestampNs = f.TimestampNs,
            };

            // Pose: Unity left-handed -> OpenXR right-handed (negate Z)
            var m = f.Pose;
            msg.Pose.AddRange(new[]
            {
                 m.m00,  m.m01, -m.m02,  m.m03,
                 m.m10,  m.m11, -m.m12,  m.m13,
                -m.m20, -m.m21,  m.m22, -m.m23,
                 m.m30,  m.m31, -m.m32,  m.m33,
            });

            if (f.Fx > 0)
            {
                msg.Intrinsics = new CameraIntrinsics
                {
                    Fx = f.Fx, Fy = f.Fy, Cx = f.Cx, Cy = f.Cy,
                };
            }
            return msg;
        }

        static bool TcpCheck(string host, int port, int timeoutMs)
        {
            try
            {
                using var tcp = new TcpClient();
                var r = tcp.BeginConnect(host, port, null, null);
                bool ok = r.AsyncWaitHandle.WaitOne(timeoutMs);
                if (ok && tcp.Connected) { tcp.EndConnect(r); return true; }
                return false;
            }
            catch { return false; }
        }

        public void Stop()
        {
            _running = false;
            _sendThread?.Join(3000);
        }

        public void Dispose() => Stop();
    }

    /// <summary>One frame of data ready to send.</summary>
    public class FrameData
    {
        public byte[] H265Bytes;
        public byte[] DepthBytes;
        public int ImageWidth, ImageHeight;
        public int DepthWidth, DepthHeight;
        public Matrix4x4 Pose;
        public float Fx, Fy, Cx, Cy;
        public int FrameNumber;
        public long TimestampNs;
        public float DepthNearZ, DepthFarZ;
        // Depth camera intrinsics (computed from FOV tangents)
        public float DepthFx, DepthFy, DepthCx, DepthCy;
        // Depth camera pose (camera-to-world, Unity convention)
        public Matrix4x4 DepthPose;
        public bool HasDepthIntrinsics;
    }
}
