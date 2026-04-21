using System;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace SemanticXR.UI
{
    // Captures mic audio with UnityEngine.Microphone, encodes it as 12 kHz /
    // mono / 16-bit PCM, and POSTs the raw bytes to the debug server at
    // http://{ip}:{port}/audio when the user taps stop.
    //
    // We moved off gRPC (Grpc.Net.Client) because Unity's Mono runtime lacks
    // SocketsHttpHandler and can't negotiate HTTP/2 over cleartext reliably.
    // UnityWebRequest is platform-uniform and has no HTTP/2 requirement.
    // When the real vis_proto gRPC server is ready, either add a native
    // Grpc.Core plugin on Unity side or expose an HTTP-gateway route.
    public class AudioStreamController : MonoBehaviour
    {
        public event Action<string> OnStatus;
        public event Action<bool>   OnListeningChanged;
        public event Action<string> OnError;

        const int SampleRate    = 12000;
        const int MaxRecordSecs = 30;
        const string PathPart   = "/audio";

        string _serverAddress;
        int    _serverPort;

        string    _deviceName;
        AudioClip _clip;
        int       _startSamplePosition;
        bool      _listening;

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
            _deviceName = Microphone.devices[0];

            _clip = Microphone.Start(_deviceName, loop: false, lengthSec: MaxRecordSecs, frequency: SampleRate);
            if (_clip == null)
            {
                OnError?.Invoke("Microphone.Start returned null");
                return;
            }
            _startSamplePosition = 0;

            _listening = true;
            OnListeningChanged?.Invoke(true);
            OnStatus?.Invoke("Listening...");
            Debug.Log($"[Audio] Recording started ({SampleRate} Hz mono, device='{_deviceName}')");
        }

        public void StopListening()
        {
            if (!_listening) return;

            int endPos = Microphone.GetPosition(_deviceName);
            Microphone.End(_deviceName);
            _listening = false;
            OnListeningChanged?.Invoke(false);

            var pcm = ExtractPcm16(_clip, _startSamplePosition, endPos);
            _clip = null;

            if (pcm == null || pcm.Length == 0)
            {
                OnStatus?.Invoke("No audio captured");
                return;
            }

            float seconds = pcm.Length / (float)(SampleRate * 2);
            OnStatus?.Invoke($"Sending {pcm.Length / 1024} KB ({seconds:F1}s)...");
            Debug.Log($"[Audio] Captured {pcm.Length} bytes ({seconds:F2}s), posting to {_serverAddress}:{_serverPort}{PathPart}");

            _ = SendAudio(pcm);
        }

        public void Cancel()
        {
            if (_listening)
            {
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

        static byte[] ExtractPcm16(AudioClip clip, int startPos, int endPos)
        {
            if (clip == null) return null;
            int total = clip.samples;
            int count = endPos >= startPos ? endPos - startPos : total - startPos + endPos;
            if (count <= 0) return new byte[0];

            var floats = new float[count * clip.channels];
            clip.GetData(floats, startPos);

            var pcm = new byte[count * 2];
            for (int i = 0; i < count; i++)
            {
                float s = Mathf.Clamp(floats[i * clip.channels], -1f, 1f);
                short v = (short)(s * short.MaxValue);
                pcm[2 * i]     = (byte)(v & 0xff);
                pcm[2 * i + 1] = (byte)((v >> 8) & 0xff);
            }
            return pcm;
        }

        async Task SendAudio(byte[] pcm)
        {
            string url = $"http://{_serverAddress}:{_serverPort}{PathPart}";
            using var req = new UnityWebRequest(url, UnityWebRequest.kHttpVerbPOST)
            {
                uploadHandler   = new UploadHandlerRaw(pcm) { contentType = "application/octet-stream" },
                downloadHandler = new DownloadHandlerBuffer(),
                timeout         = 15,
            };
            req.SetRequestHeader("X-SampleRate",  SampleRate.ToString());
            req.SetRequestHeader("X-Channels",    "1");
            req.SetRequestHeader("X-BitsPerSample", "16");

            var op = req.SendWebRequest();
            while (!op.isDone) await Task.Yield();

            if (req.result == UnityWebRequest.Result.Success)
            {
                string resp = req.downloadHandler?.text ?? "";
                string statusMsg = $"Sent {pcm.Length} bytes. Server: {resp}";
                Debug.Log($"[Audio] {statusMsg}");
                OnStatus?.Invoke(statusMsg);
            }
            else
            {
                Debug.LogWarning($"[Audio] HTTP POST failed: {req.result} — {req.error}");
                OnError?.Invoke($"Send failed: {req.error}");
            }
        }
    }
}
