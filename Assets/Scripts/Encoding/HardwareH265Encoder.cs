using System;
using System.Collections.Concurrent;
using System.Threading;
using SemanticXR.Streaming;
using UnityEngine;

namespace SemanticXR.Encoding
{
    public class HardwareH265Encoder : IDisposable
    {
        readonly int _width, _height, _bitrate, _frameRate;
        readonly ConcurrentQueue<FrameData> _input = new(), _output = new();
        Thread _thread;
        volatile bool _running;
        int _inCount;
        const int MaxQueue = 5;

#if UNITY_ANDROID && !UNITY_EDITOR
        AndroidJavaObject _codec, _bufferInfo;
#endif

        public ConcurrentQueue<FrameData> OutputQueue => _output;

        public HardwareH265Encoder(int w, int h, int bitrate = 8_000_000, int fps = 30)
        {
            _width = w; _height = h; _bitrate = bitrate; _frameRate = fps;
        }

        public void Start()
        {
            _running = true;
            _thread = new Thread(Loop) { Name = "H265Enc", IsBackground = true };
            _thread.Start();
        }

        public void Enqueue(FrameData f)
        {
            while (_inCount >= MaxQueue && _input.TryDequeue(out _))
                Interlocked.Decrement(ref _inCount);
            _input.Enqueue(f);
            Interlocked.Increment(ref _inCount);
        }

        void Loop()
        {
#if UNITY_ANDROID && !UNITY_EDITOR
            try
            {
                AndroidJNI.AttachCurrentThread();
                InitCodec();
                while (_running)
                {
                    if (_input.TryDequeue(out var f))
                    {
                        Interlocked.Decrement(ref _inCount);
                        Encode(f);
                    }
                    else Thread.Sleep(1);
                }
                StopCodec();
            }
            catch (Exception ex) { Debug.LogError($"[H265] Encoder error: {ex}"); }
            finally { AndroidJNI.DetachCurrentThread(); }
#else
            while (_running)
            {
                if (_input.TryDequeue(out var f))
                {
                    Interlocked.Decrement(ref _inCount);
                    f.H265Bytes = f.H265Bytes ?? Array.Empty<byte>(); // passthrough in editor
                    _output.Enqueue(f);
                }
                else Thread.Sleep(1);
            }
#endif
        }

#if UNITY_ANDROID && !UNITY_EDITOR
        void InitCodec()
        {
            using (var fmtClass = new AndroidJavaClass("android.media.MediaFormat"))
            {
                var fmt = fmtClass.CallStatic<AndroidJavaObject>("createVideoFormat", "video/hevc", _width, _height);
                fmt.Call("setInteger", "bitrate", _bitrate);
                fmt.Call("setInteger", "frame-rate", _frameRate);
                fmt.Call("setInteger", "color-format", 21); // NV12
                fmt.Call("setInteger", "i-frame-interval", 1);

                using (var codecClass = new AndroidJavaClass("android.media.MediaCodec"))
                    _codec = codecClass.CallStatic<AndroidJavaObject>("createEncoderByType", "video/hevc");

                _codec.Call("configure", fmt, (AndroidJavaObject)null, (AndroidJavaObject)null, 1);
                _codec.Call("start");
                fmt.Dispose();
            }
            _bufferInfo = new AndroidJavaObject("android.media.MediaCodec$BufferInfo");
            Debug.LogWarning($"[H265] Encoder ready: {_width}x{_height} @ {_bitrate}bps");
        }

        void Encode(FrameData frame)
        {
            const int TIMEOUT = 10000;

            int inIdx = _codec.Call<int>("dequeueInputBuffer", (long)TIMEOUT);
            if (inIdx < 0) return;

            var inBufs = _codec.Call<AndroidJavaObject[]>("getInputBuffers");
            inBufs[inIdx].Call<AndroidJavaObject>("clear");
            inBufs[inIdx].Call<AndroidJavaObject>("put", (sbyte[])(Array)frame.H265Bytes); // NV12 raw data in H265Bytes temporarily
            _codec.Call("queueInputBuffer", inIdx, 0, frame.H265Bytes.Length, frame.TimestampNs / 1000, 0);
            foreach (var b in inBufs) b?.Dispose();

            // Drain output
            while (true)
            {
                int outIdx = _codec.Call<int>("dequeueOutputBuffer", _bufferInfo, (long)TIMEOUT);
                if (outIdx < 0) break;

                int offset = _bufferInfo.Get<int>("offset");
                int size = _bufferInfo.Get<int>("size");

                if (size > 0)
                {
                    var outBuf = _codec.Call<AndroidJavaObject>("getOutputBuffer", outIdx);
                    outBuf.Call<AndroidJavaObject>("position", offset);
                    outBuf.Call<AndroidJavaObject>("limit", offset + size);

                    byte[] encoded = new byte[size];

                    // Read byte-by-byte from the ByteBuffer (reliable across all JNI)
                    for (int i = 0; i < size; i++)
                        encoded[i] = (byte)outBuf.Call<sbyte>("get");

                    outBuf.Dispose();
                    frame.H265Bytes = encoded;
                    _output.Enqueue(frame);
                }

                _codec.Call("releaseOutputBuffer", outIdx, false);
                break;
            }
        }

        void StopCodec()
        {
            _codec?.Call("stop"); _codec?.Call("release"); _codec?.Dispose();
            _bufferInfo?.Dispose();
            Debug.LogWarning("[H265] Encoder stopped");
        }
#endif

        public void Stop() { _running = false; _thread?.Join(2000); }
        public void Dispose() => Stop();
    }
}
