using System;
using System.Collections.Concurrent;
using System.Threading;
using Cysharp.Net.Http;
using Google.Protobuf;
using Grpc.Core;
using Grpc.Net.Client;
using UnityEngine;
using Xr;

namespace SemanticXR.Streaming
{
    /// <summary>
    /// gRPC alternative to <see cref="TcpProtoClient"/>. Same public API
    /// (see <see cref="IFramesClient"/>), same FrameData → protobuf mapping
    /// — only the transport is different.
    ///
    /// Uses <c>Grpc.Net.Client</c> with <c>YetAnotherHttpHandler</c> (Rust/hyper
    /// native HTTP/2) because Unity's Mono HTTP stack doesn't support HTTP/2
    /// over cleartext reliably.
    ///
    /// Exists so Unity can talk to the production gRPC <c>XrService</c>
    /// server without touching the raw-TCP path, which stays as a proven
    /// fallback.
    /// </summary>
    public class GrpcFramesClient : IFramesClient
    {
        readonly string _address;
        readonly int _port;
        readonly int _fps;

        readonly ConcurrentQueue<FrameData> _sendQueue = new();
        Thread _sendThread;
        volatile bool _running;
        int _sendQueueCount;
        const int MaxQueueSize = 5;

        // Hoisted out of SendLoop so Stop() can dispose them from any thread
        // to abort pending gRPC operations fast instead of waiting for graceful
        // close. Without this, Disconnect feels hung for several seconds.
        YetAnotherHttpHandler _yaha;
        GrpcChannel _channel;
        AsyncClientStreamingCall<UpstreamSyncMessage_quest, VideoStatus> _call;

        public int  QueuedFrames => _sendQueueCount;
        public bool IsConnected  { get; private set; }
        public int  SentFrames   { get; private set; }

        long _totalBytesSent;
        public long TotalBytesSent => Interlocked.Read(ref _totalBytesSent);

        volatile string _lastError;
        public string LastError
        {
            get { var e = _lastError; _lastError = null; return e; }
        }

        public GrpcFramesClient(string address, int port, int fps)
        {
            _address = address;
            _port    = port;
            _fps     = fps;
        }

        public void Start()
        {
            _running = true;
            _sendThread = new Thread(SendLoop) { Name = "GrpcFramesSend", IsBackground = true };
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
                Debug.LogWarning($"[gRPC] Connecting to {_address}:{_port}...");

                _yaha = new YetAnotherHttpHandler { Http2Only = true };
                _channel = GrpcChannel.ForAddress($"http://{_address}:{_port}",
                    new GrpcChannelOptions
                    {
                        HttpHandler        = _yaha,
                        MaxSendMessageSize = 100 * 1024 * 1024,
                        DisposeHttpClient  = false,
                    });

                var client = new XrService.XrServiceClient(_channel);
                _call = client.UploadSyncMessage_quest();
                var call = _call;      // local alias to keep the inner loop terse
                IsConnected = true;
                Debug.LogWarning($"[gRPC] Connected to {_address}:{_port}");

                while (_running)
                {
                    if (_sendQueue.TryDequeue(out var frame))
                    {
                        Interlocked.Decrement(ref _sendQueueCount);
                        try
                        {
                            var msg = BuildMessage(frame);
                            Interlocked.Add(ref _totalBytesSent, msg.CalculateSize());
                            call.RequestStream.WriteAsync(msg).Wait();
                            SentFrames++;
                            _logCounter++;
                            if (_logCounter <= 3 || _logCounter % 30 == 0)
                                Debug.LogWarning($"[gRPC] Sent frame #{frame.FrameNumber}, total sent={SentFrames}");
                        }
                        catch (Exception ex)
                        {
                            IsConnected = false;
                            _lastError = $"Send error: {ex.Message}";
                            Debug.LogError($"[gRPC] {_lastError}");
                            return;  // exit loop — orchestrator will disconnect on LastError
                        }
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }

                // Best-effort close — short timeout because Stop() may already
                // have disposed the call to force-abort.
                try { call.RequestStream.CompleteAsync().Wait(300); } catch { }
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _lastError = $"Connection failed: {ex.Message}";
                Debug.LogError($"[gRPC] {_lastError}");
            }
            finally
            {
                try { _call?.Dispose();    } catch { }
                try { _channel?.Dispose(); } catch { }
                try { _yaha?.Dispose();    } catch { }
                _call = null; _channel = null; _yaha = null;
                IsConnected = false;
            }
        }

        int _logCounter;

        // Mirrors TcpProtoClient.SendFrame's message-building logic so the
        // two transports produce byte-identical protobufs. Update both
        // together if the protocol changes.
        UpstreamSyncMessage_quest BuildMessage(FrameData f)
        {
            var msg = new UpstreamSyncMessage_quest
            {
                Image = new VideoFrame
                {
                    DataH265    = ByteString.CopyFrom(f.H265Bytes ?? Array.Empty<byte>()),
                    FrameNumber = f.FrameNumber,
                    TimestampUs = f.TimestampNs / 1000,
                },
                Depth            = ByteString.CopyFrom(f.DepthBytes ?? Array.Empty<byte>()),
                ImageWidth       = f.ImageWidth,
                ImageHeight      = f.ImageHeight,
                DepthWidth       = f.DepthWidth,
                DepthHeight      = f.DepthHeight,
                Fps              = _fps,
                TimestampNs      = f.TimestampNs,
                DepthNearZ       = f.DepthNearZ,
                DepthFarZ        = f.DepthFarZ,
                RgbTimestampNs   = f.RgbTimestampNs,
                DepthTimestampNs = f.DepthTimestampNs,
            };

            static float[] LhToRh(Matrix4x4 m) => new[]
            {
                 m.m00,  m.m01, -m.m02,  m.m03,
                 m.m10,  m.m11, -m.m12,  m.m13,
                -m.m20, -m.m21,  m.m22, -m.m23,
                 m.m30,  m.m31, -m.m32,  m.m33,
            };

            msg.Pose.AddRange(LhToRh(f.HeadPose));
            msg.HeadPose.AddRange(LhToRh(f.HeadPose));

            if (f.HasRgbCameraPose)
                msg.RgbCameraPose.AddRange(LhToRh(f.RgbCameraPose));

            if (f.Fx > 0)
            {
                msg.Intrinsics = new CameraIntrinsics
                {
                    Fx = f.Fx, Fy = f.Fy, Cx = f.Cx, Cy = f.Cy,
                };
            }

            if (f.HasDepthIntrinsics)
            {
                msg.DepthIntrinsics = new CameraIntrinsics
                {
                    Fx = f.DepthFx, Fy = f.DepthFy, Cx = f.DepthCx, Cy = f.DepthCy,
                };
                msg.DepthFovTangents.AddRange(new[]
                {
                    f.DepthFovTanLeft, f.DepthFovTanRight,
                    f.DepthFovTanTop,  f.DepthFovTanDown,
                });
                msg.DepthPose.AddRange(LhToRh(f.DepthCameraPose));
            }

            return msg;
        }

        public void Stop()
        {
            _running = false;
            // Force-abort any in-flight gRPC send/receive — much faster than
            // waiting for a graceful CompleteAsync round-trip.
            try { _call?.Dispose();    } catch { }
            try { _channel?.Dispose(); } catch { }
            _sendThread?.Join(500);
        }

        public void Dispose() => Stop();
    }
}
