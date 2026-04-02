using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Google.Protobuf;
using UnityEngine;
using Xr;

namespace SemanticXR.Streaming
{
    /// <summary>
    /// Simple TCP client that sends length-prefixed protobuf messages.
    /// Bypasses gRPC HTTP/2 which doesn't work with Grpc.Net.Client on Android IL2CPP.
    /// Protocol: [4 bytes big-endian length][protobuf bytes] per message.
    /// </summary>
    public class TcpProtoClient : IDisposable
    {
        readonly string _address;
        readonly int _port;
        readonly int _fps;

        TcpClient _tcp;
        NetworkStream _stream;

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

        public TcpProtoClient(string address, int port, int fps)
        {
            _address = address;
            _port = port;
            _fps = fps;
        }

        public void Start()
        {
            _running = true;
            _sendThread = new Thread(SendLoop) { Name = "TcpSend", IsBackground = true };
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
                Debug.LogWarning($"[TCP] Connecting to {_address}:{_port}...");

                _tcp = new TcpClient();
                var connectResult = _tcp.BeginConnect(_address, _port, null, null);
                bool connected = connectResult.AsyncWaitHandle.WaitOne(8000);

                if (!connected || !_tcp.Connected)
                {
                    _lastError = $"Cannot reach {_address}:{_port}. Check IP/port.";
                    Debug.LogError($"[TCP] {_lastError}");
                    return;
                }
                _tcp.EndConnect(connectResult);

                _tcp.NoDelay = true;
                _tcp.SendBufferSize = 4 * 1024 * 1024;
                _stream = _tcp.GetStream();
                Debug.LogWarning($"[TCP] Socket connected. Sending header...");

                var header = System.Text.Encoding.UTF8.GetBytes("QUEST_STREAM\n");
                _stream.Write(header, 0, header.Length);
                _stream.Flush();
                Debug.LogWarning($"[TCP] Header sent. Waiting for frames...");

                IsConnected = true;
                Debug.LogWarning($"[TCP] Connected to {_address}:{_port}");

                while (_running)
                {
                    if (_sendQueue.TryDequeue(out var frame))
                    {
                        Interlocked.Decrement(ref _sendQueueCount);
                        SendFrame(frame);
                        SentFrames++;
                    }
                    else
                    {
                        Thread.Sleep(1);
                    }
                }
            }
            catch (SocketException ex)
            {
                IsConnected = false;
                if (ex.Message.Contains("refused"))
                    _lastError = $"Connection refused at {_address}:{_port}.";
                else
                    _lastError = $"Network error: {ex.Message}";
                Debug.LogError($"[TCP] {_lastError}");
            }
            catch (Exception ex)
            {
                IsConnected = false;
                _lastError = $"Connection failed: {ex.Message}";
                Debug.LogError($"[TCP] {_lastError}");
            }
            finally
            {
                try { _stream?.Close(); } catch { }
                try { _tcp?.Close(); } catch { }
                IsConnected = false;
            }
        }

        int _logCounter;

        void SendFrame(FrameData f)
        {
            var msg = new UpstreamSyncMessage_quest
            {
                Image = new VideoFrame
                {
                    DataH265 = ByteString.CopyFrom(f.H265Bytes ?? Array.Empty<byte>()),
                    FrameNumber = f.FrameNumber,
                    TimestampUs = f.TimestampNs / 1000,
                },
                Depth = ByteString.CopyFrom(f.DepthBytes ?? Array.Empty<byte>()),
                ImageWidth = f.ImageWidth,
                ImageHeight = f.ImageHeight,
                DepthWidth = f.DepthWidth,
                DepthHeight = f.DepthHeight,
                Fps = _fps,
                TimestampNs = f.TimestampNs,
                DepthNearZ = f.DepthNearZ,
                DepthFarZ = f.DepthFarZ,
            };

            // Pose: Unity left-handed -> OpenXR right-handed
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

            // Depth camera intrinsics (from FOV tangents)
            if (f.HasDepthIntrinsics)
            {
                msg.DepthIntrinsics = new CameraIntrinsics
                {
                    Fx = f.DepthFx, Fy = f.DepthFy, Cx = f.DepthCx, Cy = f.DepthCy,
                };

                // Depth camera pose: Unity left-handed -> right-handed (same conversion as head pose)
                var dm = f.DepthPose;
                msg.DepthPose.AddRange(new[]
                {
                     dm.m00,  dm.m01, -dm.m02,  dm.m03,
                     dm.m10,  dm.m11, -dm.m12,  dm.m13,
                    -dm.m20, -dm.m21,  dm.m22, -dm.m23,
                     dm.m30,  dm.m31, -dm.m32,  dm.m33,
                });
            }

            // Serialize to bytes
            byte[] data = msg.ToByteArray();

            // Send length-prefixed: [4 bytes big-endian length][data]
            byte[] lenBytes = new byte[4];
            lenBytes[0] = (byte)(data.Length >> 24);
            lenBytes[1] = (byte)(data.Length >> 16);
            lenBytes[2] = (byte)(data.Length >> 8);
            lenBytes[3] = (byte)(data.Length);

            _stream.Write(lenBytes, 0, 4);
            _stream.Write(data, 0, data.Length);
            _stream.Flush();

            _logCounter++;
            if (_logCounter <= 3 || _logCounter % 30 == 0)
                Debug.LogWarning($"[TCP] Sent frame #{f.FrameNumber}, {data.Length} bytes, total sent={SentFrames + 1}");
        }

        public void Stop()
        {
            _running = false;
            _sendThread?.Join(3000);
        }

        public void Dispose() => Stop();
    }
}
