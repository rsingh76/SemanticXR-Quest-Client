using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
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

        // In-flight map: presentationTimeUs -> FrameData of the input that produced this pts.
        // Used to pair MediaCodec outputs with the ORIGINAL input's metadata (pose, depth,
        // timestamps, intrinsics). Accessed only from the encoder Loop thread so no lock
        // is needed in the hot path. Cleared on shutdown.
        readonly Dictionary<long, FrameData> _inFlight = new();
        // Hard safety cap — if encoder never emits for some reason, stop the map from
        // growing unboundedly. Steady state size <= MediaCodec internal queue depth (~5).
        const int InFlightCap = 64;

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
                // Inline VPS/SPS/PPS into every IDR so the server's H.265 decoder can
                // decode without needing to see a separate CODEC_CONFIG packet. Without
                // this, skip-CODEC_CONFIG (below) leaves the server with no parameter
                // sets and every frame fails to decode.
                fmt.Call("setInteger", "prepend-sps-pps-to-idr-frames", 1);

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

            // Presentation timestamp used to match the emerging encoded output back
            // to THIS exact FrameData (pose/depth/intrinsics/timestamps). MediaCodec
            // has internal latency — an output buffer may belong to an earlier input,
            // NOT the one we just queued. Shipping the current frame's metadata with
            // another frame's pixels breaks RGB↔pose synchronization.
            long pts = frame.TimestampNs / 1000;
            _inFlight[pts] = frame;
            if (_inFlight.Count > InFlightCap)
            {
                // Encoder is stalled or losing pts. Drop oldest to stay bounded.
                long oldest = long.MaxValue;
                foreach (var k in _inFlight.Keys) if (k < oldest) oldest = k;
                _inFlight.Remove(oldest);
                Debug.LogWarning($"[H265] in-flight map exceeded {InFlightCap}; dropped pts={oldest}");
            }

            var inBufs = _codec.Call<AndroidJavaObject[]>("getInputBuffers");
            inBufs[inIdx].Call<AndroidJavaObject>("clear");
            inBufs[inIdx].Call<AndroidJavaObject>("put", (sbyte[])(Array)frame.H265Bytes); // NV12 raw data in H265Bytes temporarily
            _codec.Call("queueInputBuffer", inIdx, 0, frame.H265Bytes.Length, pts, 0);
            foreach (var b in inBufs) b?.Dispose();

            // Drain output. MediaCodec may have multiple buffers ready after a
            // single queueInputBuffer (e.g. a CODEC_CONFIG buffer preceding the
            // first IDR). Keep dequeuing until TRY_AGAIN_LATER (-1) so nothing
            // piles up in the output queue to be mis-attached to a later frame's
            // metadata. Skip codec-config buffers (SPS/PPS/VPS) — those are not
            // picture data and the PyAV decoder correctly produces no frame for
            // them, so shipping them wastes bandwidth and disk.
            const int BUFFER_FLAG_CODEC_CONFIG = 2;
            while (true)
            {
                int outIdx = _codec.Call<int>("dequeueOutputBuffer", _bufferInfo, (long)TIMEOUT);
                if (outIdx < 0) break;

                int offset = _bufferInfo.Get<int>("offset");
                int size = _bufferInfo.Get<int>("size");
                int flags = _bufferInfo.Get<int>("flags");
                long outPts = _bufferInfo.Get<long>("presentationTimeUs");
                bool isCodecConfig = (flags & BUFFER_FLAG_CODEC_CONFIG) != 0;

                if (size > 0 && !isCodecConfig)
                {
                    // Look up the ORIGINAL input that produced this output. MediaCodec
                    // stamps each output with the pts we passed at queueInputBuffer,
                    // so this always identifies the correct source FrameData.
                    if (!_inFlight.TryGetValue(outPts, out FrameData srcFrame))
                    {
                        // pts without a recorded input — should not happen in a healthy
                        // pipeline. Release the buffer and move on; don't ship mismatched
                        // metadata (the whole reason for this bookkeeping).
                        Debug.LogWarning($"[H265] output pts={outPts} has no matching input in _inFlight (count={_inFlight.Count}); dropping frame");
                        _codec.Call("releaseOutputBuffer", outIdx, false);
                        continue;
                    }
                    _inFlight.Remove(outPts);

                    var outBuf = _codec.Call<AndroidJavaObject>("getOutputBuffer", outIdx);
                    outBuf.Call<AndroidJavaObject>("position", offset);
                    outBuf.Call<AndroidJavaObject>("limit", offset + size);

                    byte[] encoded = new byte[size];

                    // Bulk-copy the encoded payload from the Java ByteBuffer into the C#
                    // array in a single JNI round-trip. AndroidJavaObject.Call marshals
                    // C#->Java args but does NOT copy back mutations to reference-type
                    // args, so passing our C# byte[] to ByteBuffer.get(byte[]) "succeeds"
                    // while leaving the array all zeros. Drop to raw JNI: allocate a
                    // Java-side byte[], call get() to fill it, then pull it back via
                    // FromSByteArray. sbyte[] and byte[] share binary layout -> BlockCopy.
                    IntPtr jArr = AndroidJNI.NewByteArray(size);
                    try
                    {
                        IntPtr outCls = AndroidJNI.GetObjectClass(outBuf.GetRawObject());
                        IntPtr getMid = AndroidJNI.GetMethodID(outCls, "get", "([B)Ljava/nio/ByteBuffer;");
                        AndroidJNI.DeleteLocalRef(outCls);

                        jvalue[] getArgs = new jvalue[1];
                        getArgs[0].l = jArr;
                        IntPtr ret = AndroidJNI.CallObjectMethod(outBuf.GetRawObject(), getMid, getArgs);
                        if (ret != IntPtr.Zero) AndroidJNI.DeleteLocalRef(ret);

                        sbyte[] sarr = AndroidJNI.FromSByteArray(jArr);
                        Buffer.BlockCopy(sarr, 0, encoded, 0, size);
                    }
                    finally
                    {
                        AndroidJNI.DeleteLocalRef(jArr);
                    }

                    outBuf.Dispose();
                    // Attach encoded bytes to the ORIGINAL input's FrameData so the
                    // outgoing message bundles matched RGB bytes + pose + depth + etc.
                    srcFrame.H265Bytes = encoded;
                    _output.Enqueue(srcFrame);
                }
                // Note: for CODEC_CONFIG (SPS/PPS/VPS) buffers we do NOT remove the
                // in-flight entry. MediaCodec emits CODEC_CONFIG immediately followed
                // by the first IDR, both carrying the SAME pts. Removing the entry on
                // the config buffer would orphan the IDR lookup. The real picture
                // buffer's branch above removes the entry; any stragglers are evicted
                // by the InFlightCap guard on the next queue operation.

                _codec.Call("releaseOutputBuffer", outIdx, false);
            }
        }

        void StopCodec()
        {
            _codec?.Call("stop"); _codec?.Call("release"); _codec?.Dispose();
            _bufferInfo?.Dispose();
            Debug.LogWarning("[H265] Encoder stopped");
        }
#endif

        public void Stop()
        {
            _running = false;
            _thread?.Join(2000);
            _inFlight.Clear();
        }
        public void Dispose() => Stop();
    }
}
