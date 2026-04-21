using System;
using System.Threading.Tasks;
using Cysharp.Net.Http;
using Grpc.Core;
using Grpc.Net.Client;
using Google.Protobuf;
using UnityEngine;
using XrVis;

namespace SemanticXR.UI
{
    // Captures mic audio with UnityEngine.Microphone, encodes it as 12 kHz /
    // mono / 16-bit PCM (matching visualization_service.py), and sends it in a
    // single AudioFile message to VisualizerServer.clientTextQuery when the
    // user taps stop.
    //
    // gRPC transport: Grpc.Net.Client + Cysharp.Net.Http.YetAnotherHttpHandler
    // (YAHA). Unity's Mono runtime doesn't have a working HTTP/2 stack, so
    // YAHA's native (Rust/hyper) HttpHandler replaces Mono's HTTP client.
    public class AudioStreamController : MonoBehaviour
    {
        public event Action<string> OnStatus;
        public event Action<bool>   OnListeningChanged;
        public event Action<string> OnError;

        const int ServerSampleRate = 12000;   // what visualization_service.py expects
        const int MaxRecordSecs    = 30;

        string _serverAddress;
        int    _serverPort;

        string       _deviceName;
        AudioClip    _clip;
        AudioSource  _keepAliveSource;     // consumes the mic clip so Android doesn't stop writing samples
        int          _startSamplePosition;
        int          _actualSampleRate;    // what the mic is actually giving us
        bool         _listening;

        public bool IsListening => _listening;

        public void Configure(string address, int port)
        {
            _serverAddress = address;
            _serverPort    = port;
        }

        public void StartListening()
        {
            if (_listening) return;
            if (string.IsNullOrEmpty(_serverAddress))
            {
                OnError?.Invoke("Server address not set — connect first");
                return;
            }

#if UNITY_ANDROID && !UNITY_EDITOR
            if (!UnityEngine.Android.Permission.HasUserAuthorizedPermission(
                    UnityEngine.Android.Permission.Microphone))
            {
                UnityEngine.Android.Permission.RequestUserPermission(
                    UnityEngine.Android.Permission.Microphone);
                OnError?.Invoke("Microphone permission needed — grant it and tap again");
                return;
            }
#endif

            if (Microphone.devices == null || Microphone.devices.Length == 0)
            {
                OnError?.Invoke("No microphone available");
                return;
            }

            // List every mic device the system reports, with caps.
            for (int i = 0; i < Microphone.devices.Length; i++)
            {
                Microphone.GetDeviceCaps(Microphone.devices[i], out int a, out int b);
                Debug.Log($"[Audio] Device[{i}] = '{Microphone.devices[i]}' caps=[{a}..{b}]");
            }

            _deviceName = Microphone.devices[0];
            Microphone.GetDeviceCaps(_deviceName, out int minFreq, out int maxFreq);
            _actualSampleRate = (minFreq == 0 && maxFreq == 0) ? 48000
                             : Mathf.Clamp(48000, minFreq, maxFreq);
            Debug.Log($"[Audio] Selected device='{_deviceName}' recording @ {_actualSampleRate} Hz");

            _clip = Microphone.Start(_deviceName, loop: false, lengthSec: MaxRecordSecs, frequency: _actualSampleRate);
            if (_clip == null)
            {
                OnError?.Invoke("Microphone.Start returned null");
                return;
            }
            _startSamplePosition = 0;

            // Wait for the mic to actually start producing data. Without this
            // Microphone.GetPosition returns 0 and Android never starts writing.
            // This is the documented Unity pattern.
            int waitGuard = 0;
            while (Microphone.GetPosition(_deviceName) <= 0 && waitGuard++ < 1000) { /* spin briefly */ }

            // Attach an AudioSource that "consumes" the clip so the platform
            // audio engine keeps writing samples. muted so the user doesn't
            // hear their own voice played back.
            if (_keepAliveSource == null) _keepAliveSource = gameObject.AddComponent<AudioSource>();
            _keepAliveSource.clip   = _clip;
            _keepAliveSource.loop   = true;
            _keepAliveSource.mute   = true;
            _keepAliveSource.volume = 0f;
            _keepAliveSource.Play();

            _listening = true;
            OnListeningChanged?.Invoke(true);
            OnStatus?.Invoke("Listening...");
            Debug.Log($"[Audio] Recording started ({_actualSampleRate} Hz mono, device='{_deviceName}', keep-alive AudioSource active)");
        }

        public void StopListening()
        {
            if (!_listening) return;

            int endPos = Microphone.GetPosition(_deviceName);
            if (_keepAliveSource != null) _keepAliveSource.Stop();
            Microphone.End(_deviceName);
            _listening = false;
            OnListeningChanged?.Invoke(false);

            var floats = ExtractFloats(_clip, _startSamplePosition, endPos);
            _clip = null;

            if (floats == null || floats.Length == 0)
            {
                OnStatus?.Invoke("No audio captured");
                return;
            }

            // Peak + RMS across the buffer, and peak per-decile so we can see
            // if audio was hot at *any* point during the recording.
            float peak = 0f, sumSq = 0f;
            for (int i = 0; i < floats.Length; i++) { float a = Mathf.Abs(floats[i]); if (a > peak) peak = a; sumSq += floats[i] * floats[i]; }
            float rms = Mathf.Sqrt(sumSq / Mathf.Max(1, floats.Length));
            Debug.Log($"[Audio] Captured {floats.Length} samples @ {_actualSampleRate} Hz, peak={peak:F4}, rms={rms:F4}");
            int decile = floats.Length / 10;
            if (decile > 0)
            {
                var parts = new System.Text.StringBuilder("[Audio] peak-per-decile ");
                for (int d = 0; d < 10; d++)
                {
                    float p = 0f;
                    int start = d * decile;
                    int end   = d == 9 ? floats.Length : start + decile;
                    for (int i = start; i < end; i++) { float a = Mathf.Abs(floats[i]); if (a > p) p = a; }
                    parts.Append(p.ToString("F3")).Append(' ');
                }
                Debug.Log(parts.ToString());
            }

            // Quest's mic captures at very low amplitude (~0.001-0.2 peak even
            // with the AudioSource keep-alive). RMS-based normalization with
            // soft-clipping gives perceptually loud output without audible
            // distortion — this is what Whisper (and human listeners) want.
            //
            // Target RMS 0.15 ≈ normal speech loudness after compression.
            // Gain is capped so pure-silence recordings don't blow noise up.
            const float TargetRms = 0.15f;
            const float MinRms    = 0.001f;
            const float MaxGain   = 50f;
            if (rms >= MinRms)
            {
                float gain = Mathf.Min(TargetRms / rms, MaxGain);
                Debug.Log($"[Audio] Normalizing: gain={gain:F1}x (peak {peak:F3}→{Mathf.Min(peak*gain,1f):F2}, rms {rms:F4}→{Mathf.Min(rms*gain,TargetRms):F3})");
                for (int i = 0; i < floats.Length; i++)
                {
                    // tanh soft clipper: linear near 0, smoothly approaches ±1
                    // for large |v|. Keeps output strictly in (-1, 1).
                    floats[i] = (float)System.Math.Tanh(floats[i] * gain);
                }
            }
            else
            {
                Debug.LogWarning($"[Audio] RMS {rms:F4} below noise floor — skipping normalization");
            }

            // Downsample (linear) from _actualSampleRate to ServerSampleRate.
            var resampled = Resample(floats, _actualSampleRate, ServerSampleRate);
            var pcm       = FloatsToPcm16(resampled);

            float seconds = pcm.Length / (float)(ServerSampleRate * 2);
            OnStatus?.Invoke($"Sending {pcm.Length / 1024} KB ({seconds:F1}s)...");
            Debug.Log($"[Audio] Resampled to {resampled.Length} samples @ {ServerSampleRate} Hz ({pcm.Length} bytes), sending to {_serverAddress}:{_serverPort}");

            _ = SendAudio(pcm);
        }

        public void Cancel()
        {
            if (_listening)
            {
                if (_keepAliveSource != null) _keepAliveSource.Stop();
                Microphone.End(_deviceName);
                _listening = false;
                OnListeningChanged?.Invoke(false);
            }
            _clip = null;
        }

        public void Toggle()
        {
            if (_listening) StopListening(); else StartListening();
        }

        // ---- helpers ----

        static float[] ExtractFloats(AudioClip clip, int startPos, int endPos)
        {
            if (clip == null) return null;
            int total = clip.samples;
            int count = endPos >= startPos ? endPos - startPos : total - startPos + endPos;
            if (count <= 0) return new float[0];

            var interleaved = new float[count * clip.channels];
            clip.GetData(interleaved, startPos);

            // Collapse to mono by averaging channels.
            if (clip.channels == 1) return interleaved;
            var mono = new float[count];
            for (int i = 0; i < count; i++)
            {
                float sum = 0f;
                for (int c = 0; c < clip.channels; c++) sum += interleaved[i * clip.channels + c];
                mono[i] = sum / clip.channels;
            }
            return mono;
        }

        // Linear-interpolation resampling. Quality is fine for voice at these
        // rates; no need for a proper polyphase filter.
        static float[] Resample(float[] src, int srcRate, int dstRate)
        {
            if (srcRate == dstRate) return src;
            int dstLen = (int)((long)src.Length * dstRate / srcRate);
            var dst = new float[dstLen];
            double ratio = (double)srcRate / dstRate;
            for (int i = 0; i < dstLen; i++)
            {
                double srcIndex = i * ratio;
                int    i0 = (int)srcIndex;
                int    i1 = Mathf.Min(i0 + 1, src.Length - 1);
                float  frac = (float)(srcIndex - i0);
                dst[i] = src[i0] * (1f - frac) + src[i1] * frac;
            }
            return dst;
        }

        static byte[] FloatsToPcm16(float[] floats)
        {
            var pcm = new byte[floats.Length * 2];
            for (int i = 0; i < floats.Length; i++)
            {
                float s = Mathf.Clamp(floats[i], -1f, 1f);
                short v = (short)(s * short.MaxValue);
                pcm[2 * i]     = (byte)(v & 0xff);
                pcm[2 * i + 1] = (byte)((v >> 8) & 0xff);
            }
            return pcm;
        }

        async Task SendAudio(byte[] pcm)
        {
            YetAnotherHttpHandler yaha = null;
            GrpcChannel channel = null;
            try
            {
                yaha = new YetAnotherHttpHandler
                {
                    // Mandatory for gRPC over cleartext — forces HTTP/2 prior
                    // knowledge (h2c), skipping the upgrade dance.
                    Http2Only = true,
                };

                channel = GrpcChannel.ForAddress($"http://{_serverAddress}:{_serverPort}",
                    new GrpcChannelOptions
                    {
                        HttpHandler        = yaha,
                        MaxSendMessageSize = 100 * 1024 * 1024,
                        DisposeHttpClient  = false,
                    });
                var client = new VisualizerServer.VisualizerServerClient(channel);

                var call = client.clientTextQuery();
                try
                {
                    var msg = new AudioFile { ChunkData = ByteString.CopyFrom(pcm) };
                    await call.RequestStream.WriteAsync(msg);
                    await call.RequestStream.CompleteAsync();
                    var response = await call.ResponseAsync;

                    string statusMsg = $"Sent. Server returned {response.NumPointClouds} point clouds.";
                    Debug.Log($"[Audio] {statusMsg}");
                    OnStatus?.Invoke(statusMsg);
                }
                finally
                {
                    call.Dispose();
                }
            }
            catch (Exception e)
            {
                Debug.LogWarning($"[Audio] gRPC send failed: {e.Message}");
                OnError?.Invoke($"Send failed: {e.Message}");
            }
            finally
            {
                channel?.Dispose();
                yaha?.Dispose();
            }
        }
    }
}
