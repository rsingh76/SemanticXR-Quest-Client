using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.UI;
using TMPro;
using SemanticXR.Streaming;

namespace SemanticXR.UI
{
    public class StreamingUI : MonoBehaviour
    {
        [Header("UI Position")]
        [SerializeField] float distanceFromCamera = 1.2f;
        [SerializeField] float verticalOffset = -0.1f;

        StreamingOrchestrator _orchestrator;
        Canvas _canvas;
        RectTransform _canvasRect;
        GameObject _connectPanel, _streamingPanel;

        // EDIT THE FIRST STRING IN EACH PAIR to change the friendly name shown
        // on the dropdown. The second string is the actual IP. Keep names short
        // (≤ ~14 chars) so they fit comfortably on the 260-wide display button.
        static readonly (string name, string ip)[] IpPresets =
        {
            ("Demo (Arches)",    "128.174.3.132"),
            ("Debug Laptop (Home)",   "192.168.1.2"),
            ("Everglades", "10.195.21.84"),
            ("Debug Laptop (Lab)", "10.193.43.94"),
        };
        int _ipIndex;
        string _ipAddress;
        Mk.DropdownHandle _ipDropdown;

        // Mirror production: the real server talks gRPC on 50051, so gRPC
        // debug/prod lives at 50051. TCP debug server moves to 50055.
        const string DefaultTcpPort  = "50055";
        const string DefaultGrpcPort = "50051";
        string _portString = DefaultGrpcPort;   // initial value updated in Awake based on orchestrator's transport
        TextMeshProUGUI _portLabel;

        string _audioPortString = "50054";
        TextMeshProUGUI _audioPortLabel;

        static readonly int[] FpsOptions = { 2, 3, 5, 6, 7, 10, 15, 20, 25 };
        int _fpsIndex;
        int _selectedFps = 2;
        Mk.DropdownHandle _fpsDropdown;

        Button _transportBtn;
        TextMeshProUGUI _transportLabel;

        Button _connectBtn;
        TextMeshProUGUI _errorText, _statusText, _statsText;

        AudioStreamController _audio;
        PointCloudVisualizer  _visualizer;

        // Upstream bandwidth: per-second cumulative byte snapshots over a
        // 10-second window. Rate = (now - oldest) * 8 / 10s. Updated once
        // per second from Update().
        const int BwWindowSec = 10;
        readonly long[] _bwSnapshots = new long[BwWindowSec];
        int   _bwIdx;
        float _bwNextSampleTime;
        float _bwRateMbps;
        Button _micBtn;
        Button _clearBtn;
        Image _micBtnBg;
        GameObject _micIconGroup, _stopIconGroup;
        TextMeshProUGUI _dictationText;
        static readonly Color MicIdleColor = new Color(0.15f, 0.45f, 0.75f);
        static readonly Color MicActiveColor = new Color(0.85f, 0.2f, 0.2f);

        TouchScreenKeyboard _keyboard;
        string _editField;
        bool _positioned;

        [Header("Mic Orb (Body-Locked)")]
        [SerializeField] float micOrbDistance = 0.55f;
        [SerializeField] float micOrbVerticalOffset = -0.35f;
        [SerializeField] float micOrbPosSmoothTime = 0.3f;
        // Larger = slower yaw catch-up. Head twists don't drag the orb; body turns do.
        [SerializeField] float micOrbYawSmoothTime = 1.2f;

        GameObject _micOrb;
        Vector3 _orbBodyForward = Vector3.forward;
        Vector3 _orbVelocity;

        void Awake()
        {
            _ipAddress = IpPresets[0].ip;

            // Setup XR interaction (ray interactors on controllers)
            gameObject.AddComponent<XRInteractionSetup>();

            _orchestrator = gameObject.AddComponent<StreamingOrchestrator>();
            _orchestrator.OnConnected += ShowStreaming;
            _orchestrator.OnDisconnected += ShowConnect;
            _orchestrator.OnError += ShowError;

            _audio = gameObject.AddComponent<AudioStreamController>();
            _audio.OnStatus            += OnAudioStatus;
            _audio.OnListeningChanged  += OnDictationListeningChanged;
            _audio.OnError             += OnDictationErrorReceived;
            _audio.OnPointClouds       += OnPointCloudsReceived;

            // Point-cloud visualizer lives on a separate child GameObject so
            // spheres are parented there, not on the StreamCanvas.
            var pcHost = new GameObject("PointClouds");
            pcHost.transform.SetParent(transform, worldPositionStays: false);
            _visualizer = pcHost.AddComponent<PointCloudVisualizer>();

            // Initial frames port follows whichever transport the orchestrator
            // defaults to. Kept in sync on toggle via ToggleTransport().
            _portString = DefaultPortFor(_orchestrator.Transport);

            // ProjectionSanityTest is intentionally NOT auto-spawned. It's a
            // one-off diagnostic that runs 10 validation frames and writes a
            // bunch of LogWarnings; useful when bringing up depth↔RGB sync but
            // pure noise in production. Add it manually in the editor (or
            // uncomment the line below) when debugging projection math.
            // gameObject.AddComponent<SemanticXR.Diagnostics.ProjectionSanityTest>();

            BuildUI();
        }

        void Start() { SubscribeRecenter(); }
        void OnDestroy() { UnsubscribeRecenter(); }

        void SubscribeRecenter()
        {
            var subs = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            foreach (var s in subs) s.trackingOriginUpdated += _ => Position();
        }
        void UnsubscribeRecenter()
        {
            var subs = new List<XRInputSubsystem>();
            SubsystemManager.GetSubsystems(subs);
            foreach (var s in subs) s.trackingOriginUpdated -= _ => Position();
        }

        void Position()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var fwd = cam.transform.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) fwd = cam.transform.forward;
            fwd.Normalize();
            var pos = cam.transform.position + fwd * distanceFromCamera;
            pos.y = cam.transform.position.y + verticalOffset;
            _canvas.transform.position = pos;
            _canvas.transform.rotation = Quaternion.LookRotation(pos - cam.transform.position);
        }

        void BuildUI()
        {
            var go = new GameObject("StreamCanvas");
            go.transform.SetParent(transform);
            _canvas = go.AddComponent<Canvas>();
            _canvas.renderMode = RenderMode.WorldSpace;
            go.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;

            // This is what made buttons work in the old SemanticXR project
            go.AddComponent<TrackedDeviceGraphicRaycaster>();

            _canvasRect = _canvas.GetComponent<RectTransform>();
            _canvasRect.sizeDelta = new Vector2(650, 420);
            _canvasRect.localScale = Vector3.one * 0.001f;

            var bg = Mk.Panel(go.transform, "Bg", new Color(0.05f, 0.05f, 0.1f, 0.88f));
            Mk.Stretch(bg);

            _connectPanel = Mk.Panel(bg.transform, "Connect", Color.clear);
            Mk.Stretch(_connectPanel, 30);

            Mk.Label(_connectPanel.transform, "SemanticXR", new Vector2(0, 160), 32, Color.white);

            Mk.Label(_connectPanel.transform, "Server IP", new Vector2(0, 115), 16, new Color(0.6f, 0.6f, 0.65f));
            var ipNames = new string[IpPresets.Length];
            var ipAddrs = new string[IpPresets.Length];
            for (int i = 0; i < IpPresets.Length; i++) { ipNames[i] = IpPresets[i].name; ipAddrs[i] = IpPresets[i].ip; }
            _ipDropdown = Mk.Dropdown(_connectPanel.transform, new Vector2(-40, 85), new Vector2(260, 36),
                ipNames, _ipIndex,
                i => { _ipIndex = i; _ipAddress = ipAddrs[i]; },
                itemHeight: 34f, secondaries: ipAddrs);
            Mk.Btn(_connectPanel.transform, "Custom", new Vector2(150, 85), new Vector2(80, 30), new Color(0.3f, 0.3f, 0.4f), 14, () => OpenKB("ip"));

            // Two ports side-by-side: frames (raw TCP or gRPC) and audio (gRPC VisualizerServer).
            Mk.Label(_connectPanel.transform, "Frames Port", new Vector2(-115, 50), 16, new Color(0.6f, 0.6f, 0.65f));
            _portLabel = Mk.Label(_connectPanel.transform, _portString, new Vector2(-115, 22), 20, Color.white);
            Mk.Btn(_connectPanel.transform, "", new Vector2(-115, 22), new Vector2(200, 30), new Color(0.2f, 0.2f, 0.25f, 0.5f), 0, () => OpenKB("port"));

            Mk.Label(_connectPanel.transform, "Audio Port", new Vector2(115, 50), 16, new Color(0.6f, 0.6f, 0.65f));
            _audioPortLabel = Mk.Label(_connectPanel.transform, _audioPortString, new Vector2(115, 22), 20, Color.white);
            Mk.Btn(_connectPanel.transform, "", new Vector2(115, 22), new Vector2(200, 30), new Color(0.2f, 0.2f, 0.25f, 0.5f), 0, () => OpenKB("audioPort"));

            Mk.Label(_connectPanel.transform, "FPS", new Vector2(-220, -15), 16, new Color(0.6f, 0.6f, 0.65f), TextAlignmentOptions.MidlineRight, 160);
            var fpsStrings = new string[FpsOptions.Length];
            for (int i = 0; i < FpsOptions.Length; i++) fpsStrings[i] = FpsOptions[i].ToString();
            _fpsDropdown = Mk.Dropdown(_connectPanel.transform, new Vector2(-40, -15), new Vector2(140, 30),
                fpsStrings, _fpsIndex,
                i => { _fpsIndex = i; _selectedFps = FpsOptions[i]; },
                openUpward: true, itemHeight: 22f, itemFontSize: 15f, arrowRightMargin: 18f);

            Mk.Label(_connectPanel.transform, "Transport", new Vector2(115, -15), 14, new Color(0.6f, 0.6f, 0.65f), TextAlignmentOptions.MidlineRight, 100);
            _transportBtn = Mk.Btn(_connectPanel.transform, _orchestrator.Transport.ToString(),
                new Vector2(220, -15), new Vector2(100, 30),
                new Color(0.3f, 0.3f, 0.4f), 16, ToggleTransport);
            _transportLabel = _transportBtn.GetComponentInChildren<TextMeshProUGUI>();

            _connectBtn = Mk.Btn(_connectPanel.transform, "Connect", new Vector2(0, -65), new Vector2(220, 50), new Color(0.15f, 0.55f, 0.25f), 24, OnConnect);
            _errorText = Mk.Label(_connectPanel.transform, "", new Vector2(0, -110), 15, new Color(1f, 0.4f, 0.4f));

            _streamingPanel = Mk.Panel(bg.transform, "Streaming", Color.clear);
            Mk.Stretch(_streamingPanel, 20);
            Mk.Label(_streamingPanel.transform, "Streaming", new Vector2(0, 155), 26, new Color(0.3f, 0.9f, 0.4f));
            _statusText = Mk.Label(_streamingPanel.transform, "", new Vector2(0, 120), 16, Color.white);

            // Compact stats line in the top-right corner. Center is intentionally
            // left empty for new feature UI (e.g. add-voice-query).
            // Format: "Sent N · Q M · Drops K · X FPS · Upstream Y Mbps". Color goes orange on drops.
            // Rect is right-anchored: width grows leftward from x=150 (the old right edge).
            _statsText = Mk.Label(_streamingPanel.transform, "",
                new Vector2(-100, 175), 13, new Color(0.55f, 0.55f, 0.6f),
                TextAlignmentOptions.TopRight, 500);
            _statsText.GetComponent<RectTransform>().sizeDelta = new Vector2(500, 22);

            // Voice dictation: text box above a mic/stop toggle button.
            Mk.Label(_streamingPanel.transform, "Speak to SemanticXR", new Vector2(0, 85), 14,
                new Color(0.6f, 0.6f, 0.65f));

            var textBoxBg = Mk.Panel(_streamingPanel.transform, "DictationBox",
                new Color(0.12f, 0.12f, 0.18f, 0.9f));
            var tbR = textBoxBg.GetComponent<RectTransform>();
            tbR.anchoredPosition = new Vector2(0, 25);
            tbR.sizeDelta = new Vector2(520, 100);
            textBoxBg.GetComponent<Image>().raycastTarget = false;

            _dictationText = Mk.Label(textBoxBg.transform, "",
                Vector2.zero, 16, new Color(0.92f, 0.92f, 0.95f),
                TextAlignmentOptions.TopLeft, 500);
            var dtR = _dictationText.GetComponent<RectTransform>();
            dtR.sizeDelta = new Vector2(500, 90);
            _dictationText.textWrappingMode = TextWrappingModes.Normal;

            // Mic + Clear + Disconnect all live on a body-locked orb (see BuildMicOrb).
            _streamingPanel.SetActive(false);

            BuildMicOrb();
        }

        void BuildMicOrb()
        {
            _micOrb = new GameObject("MicOrb");
            _micOrb.transform.SetParent(transform, worldPositionStays: true);
            var canvas = _micOrb.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            _micOrb.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;
            _micOrb.AddComponent<TrackedDeviceGraphicRaycaster>();

            var r = canvas.GetComponent<RectTransform>();
            r.sizeDelta = new Vector2(340, 100);
            r.localScale = Vector3.one * 0.001f;

            var clearColor      = new Color(0.25f, 0.35f, 0.5f);
            var disconnectColor = new Color(0.6f, 0.15f, 0.15f);

            _clearBtn = MakeOrbButton(_micOrb.transform, new Vector2(-115, 0),
                clearColor, IconFactory.Trash, ClearPoints);

            _micBtn = MakeOrbButton(_micOrb.transform, Vector2.zero,
                MicIdleColor, IconFactory.Mic, OnMicClicked);
            _micBtnBg = _micBtn.GetComponent<Image>();
            // Stop icon overlays the mic icon on the same button, toggled by listening state.
            _micIconGroup  = _micBtn.transform.Find("Icon").gameObject;
            _stopIconGroup = IconFactory.MakeIconChild(_micBtn.transform, "StopIcon", IconFactory.Stop);
            _stopIconGroup.SetActive(false);

            var trig = _micBtn.gameObject.AddComponent<EventTrigger>();
            AddTrigger(trig, EventTriggerType.PointerEnter, () => Debug.Log("[StreamingUI] pointer ENTER mic orb"));
            AddTrigger(trig, EventTriggerType.PointerDown,  () => Debug.Log("[StreamingUI] pointer DOWN mic orb"));
            AddTrigger(trig, EventTriggerType.PointerUp,    () => Debug.Log("[StreamingUI] pointer UP mic orb"));
            AddTrigger(trig, EventTriggerType.PointerClick, () => Debug.Log("[StreamingUI] pointer CLICK mic orb"));

            MakeOrbButton(_micOrb.transform, new Vector2(115, 0),
                disconnectColor, IconFactory.Power, () => _orchestrator.Disconnect());

            _micOrb.SetActive(false);
        }

        static Button MakeOrbButton(Transform parent, Vector2 pos, Color bg, Sprite icon, UnityEngine.Events.UnityAction onClick)
        {
            var btn = Mk.Btn(parent, "", pos, new Vector2(85, 85), bg, 0, onClick);
            var img = btn.GetComponent<Image>();
            img.sprite = IconFactory.Circle;
            img.type   = Image.Type.Simple;
            IconFactory.MakeIconChild(btn.transform, "Icon", icon);
            return btn;
        }

        void InitMicOrbPose()
        {
            var cam = Camera.main;
            if (cam == null || _micOrb == null) return;
            var fwd = cam.transform.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            _orbBodyForward = fwd.normalized;
            _orbVelocity = Vector3.zero;
            var p = cam.transform.position + _orbBodyForward * micOrbDistance;
            p.y = cam.transform.position.y + micOrbVerticalOffset;
            _micOrb.transform.position = p;
            _micOrb.transform.rotation = Quaternion.LookRotation(p - cam.transform.position);
        }

        void UpdateMicOrbPose()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var headFwd = cam.transform.forward; headFwd.y = 0;
            if (headFwd.sqrMagnitude < 0.001f) return;
            headFwd.Normalize();

            float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, micOrbYawSmoothTime));
            _orbBodyForward = Vector3.Slerp(_orbBodyForward, headFwd, t).normalized;

            var target = cam.transform.position + _orbBodyForward * micOrbDistance;
            target.y = cam.transform.position.y + micOrbVerticalOffset;
            _micOrb.transform.position = Vector3.SmoothDamp(
                _micOrb.transform.position, target, ref _orbVelocity, micOrbPosSmoothTime);
            _micOrb.transform.rotation = Quaternion.LookRotation(
                _micOrb.transform.position - cam.transform.position);
        }

        void ToggleTransport()
        {
            _orchestrator.Transport = _orchestrator.Transport == FramesTransport.Tcp
                ? FramesTransport.Grpc : FramesTransport.Tcp;
            if (_transportLabel != null) _transportLabel.text = _orchestrator.Transport.ToString();
            // Auto-snap the frames port to the new transport's default.
            _portString = DefaultPortFor(_orchestrator.Transport);
            if (_portLabel != null) _portLabel.text = _portString;
            Debug.Log($"[StreamingUI] Transport → {_orchestrator.Transport}, port → {_portString}");
        }

        static string DefaultPortFor(FramesTransport t) =>
            t == FramesTransport.Grpc ? DefaultGrpcPort : DefaultTcpPort;
        void OpenKB(string field)
        {
            _editField = field;
            string seed = field switch
            {
                "ip"        => _ipAddress,
                "port"      => _portString,
                "audioPort" => _audioPortString,
                _           => ""
            };
            _keyboard = TouchScreenKeyboard.Open(
                seed, TouchScreenKeyboardType.DecimalPad, false, false, false, false, "", 21);
        }
        void OnConnect()
        {
            _errorText.text = "";
            if (string.IsNullOrEmpty(_ipAddress)) { ShowError("Enter IP"); return; }
            if (!int.TryParse(_portString, out int port) || port < 1 || port > 65535) { ShowError("Invalid port"); return; }
            _connectBtn.interactable = false;
            _errorText.text = "Connecting...";
            _orchestrator.Connect(_ipAddress, port, _selectedFps);
        }
        void ShowConnect()
        {
            ResetDictation();
            _canvasRect.sizeDelta = new Vector2(650, 420);
            _connectBtn.interactable = true;
            _errorText.text = "";
            _connectPanel.SetActive(true);
            _streamingPanel.SetActive(false);
            if (_micOrb != null) _micOrb.SetActive(false);
            Position();
        }

        void ResetDictation()
        {
            if (_audio != null) _audio.Cancel();
            if (_visualizer != null) _visualizer.Clear();
            if (_dictationText   != null) _dictationText.text = "";
            OnDictationListeningChanged(false);
        }
        void ShowStreaming()
        {
            _connectPanel.SetActive(false);
            _streamingPanel.SetActive(true);
            _canvasRect.sizeDelta = new Vector2(650, 420);
            System.Array.Clear(_bwSnapshots, 0, _bwSnapshots.Length);
            _bwIdx = 0;
            _bwNextSampleTime = Time.unscaledTime + 1f;
            _bwRateMbps = 0f;
            // Tell the audio client which server to send to — same IP as the
            // frames stream, separate user-configurable port for the
            // vis_proto VisualizerServer.
            if (_audio != null && int.TryParse(_audioPortString, out int audioPort))
                _audio.Configure(_ipAddress, audioPort);
            Position();
            if (_micOrb != null) { _micOrb.SetActive(true); InitMicOrbPose(); }
        }
        void ShowError(string msg) { _errorText.text = msg; _connectBtn.interactable = true; }

        void OnMicClicked()
        {
            Debug.Log($"[StreamingUI] mic clicked, listening={_audio.IsListening}");
            StartCoroutine(FlashMicButton());
            _audio.Toggle();
        }

        System.Collections.IEnumerator FlashMicButton()
        {
            if (_micBtnBg == null) yield break;
            var original = _micBtnBg.color;
            _micBtnBg.color = new Color(1f, 0.95f, 0.2f);  // bright yellow
            yield return new WaitForSeconds(0.15f);
            // If a listening-state change already re-colored it, don't stomp.
            if (_micBtnBg.color.r > 0.9f && _micBtnBg.color.g > 0.9f)
                _micBtnBg.color = original;
        }

        static void AddTrigger(EventTrigger trig, EventTriggerType type, System.Action cb)
        {
            var entry = new EventTrigger.Entry { eventID = type };
            entry.callback.AddListener(_ => cb());
            trig.triggers.Add(entry);
        }

        void OnAudioStatus(string s)
        {
            // Verbose per-query status ("Sending X KB...", "Sent. Server
            // returned N point clouds.") goes only to the text box. The short
            // button-state strings ("Listening...", "Tap the mic to speak")
            // are written separately by OnDictationListeningChanged.
            if (_dictationText != null) _dictationText.text = s;
        }

        void OnPointCloudsReceived(XrVis.allPointClouds response)
        {
            if (_visualizer == null || response == null) return;
            var (objects, points) = _visualizer.AddResponse(response);
            string query = string.IsNullOrEmpty(response.TextQuery) ? "(none)" : response.TextQuery;
            string msg = $"Query: {query}\nReceived: {objects} objects, {points} points ({response.ServerQueryProcessing:F0} ms)";
            Debug.Log($"[StreamingUI] {msg}");
            if (_dictationText != null) _dictationText.text = msg;
        }

        void ClearPoints()
        {
            _visualizer?.Clear();
            if (_dictationText != null) _dictationText.text = "Points cleared.";
            Debug.Log("[StreamingUI] Cleared all point clouds");
        }

        void OnDictationListeningChanged(bool listening)
        {
            if (_micBtnBg != null)
            {
                _micBtnBg.color = listening ? MicActiveColor : MicIdleColor;
                var c = _micBtn.colors;
                c.highlightedColor = _micBtnBg.color * 1.3f;
                c.pressedColor = _micBtnBg.color * 0.7f;
                _micBtn.colors = c;
            }
            if (_micIconGroup  != null) _micIconGroup.SetActive(!listening);
            if (_stopIconGroup != null) _stopIconGroup.SetActive(listening);
        }

        void OnDictationErrorReceived(string msg)
        {
            Debug.LogWarning($"[StreamingUI] dictation error: {msg}");
            if (_dictationText != null && !string.IsNullOrEmpty(msg))
                _dictationText.text = $"<color=#ff6b6b>Error: {msg}</color>";
            // Button state is user-intent driven — do not flip it on error.
        }

        void Update()
        {
            if (!_positioned && Camera.main != null) { Position(); _positioned = true; }

            if (_micOrb != null && _micOrb.activeSelf) UpdateMicOrbPose();

            if (_keyboard != null)
            {
                if (_keyboard.status == TouchScreenKeyboard.Status.Visible ||
                    _keyboard.status == TouchScreenKeyboard.Status.Done)
                {
                    switch (_editField)
                    {
                        case "ip":        _ipAddress       = _keyboard.text; _ipDropdown.label.text = "Custom"; if (_ipDropdown.subLabel != null) _ipDropdown.subLabel.text = _ipAddress; break;
                        case "audioPort": _audioPortString = _keyboard.text; _audioPortLabel.text = _audioPortString; break;
                        default:          _portString      = _keyboard.text; _portLabel.text      = _portString;      break;
                    }
                }
                if (_keyboard.status != TouchScreenKeyboard.Status.Visible)
                    _keyboard = null;
            }

            if (_streamingPanel.activeSelf)
            {
                var err = _orchestrator.LastError;
                if (!string.IsNullOrEmpty(err)) { _orchestrator.Disconnect(); ShowConnect(); ShowError(err); return; }
                _statusText.text = _orchestrator.IsConnected
                    ? $"Connected to {_orchestrator.ServerTarget}"
                    : $"Connecting to {_orchestrator.ServerTarget}...";

                // Compact stats line: Sent · Q · Drops · FPS · Upstream  (color-coded on drops).
                int totalDropped = _orchestrator.TotalDropped;
                _statsText.color = totalDropped > 0
                    ? new Color(1f, 0.7f, 0.3f)
                    : new Color(0.55f, 0.55f, 0.6f);
                string fpsStr = _orchestrator.CaptureFps > 0 ? $"{_orchestrator.CaptureFps:F1}" : "—";

                if (Time.unscaledTime >= _bwNextSampleTime)
                {
                    long cur = _orchestrator.BytesSent;
                    long old = _bwSnapshots[_bwIdx];
                    _bwSnapshots[_bwIdx] = cur;
                    _bwIdx = (_bwIdx + 1) % BwWindowSec;
                    // Clamp to 0 to absorb the reset at reconnect (cur < old).
                    long delta = cur - old;
                    _bwRateMbps = delta > 0 ? delta * 8f / BwWindowSec / 1_000_000f : 0f;
                    _bwNextSampleTime = Time.unscaledTime + 1f;
                }

                _statsText.text = $"Sent {_orchestrator.FrameCount} · Q {_orchestrator.QueuedFrames} · Drops {totalDropped} · {fpsStr} FPS · Upstream {_bwRateMbps:F2} Mbps";
            }
        }
    }

    static class Mk
    {
        public static GameObject Panel(Transform p, string n, Color c)
        {
            var go = new GameObject(n); go.transform.SetParent(p, false);
            go.AddComponent<RectTransform>();
            if (c.a > 0) go.AddComponent<Image>().color = c;
            return go;
        }
        public static void Stretch(GameObject go, float inset = 0)
        {
            var r = go.GetComponent<RectTransform>();
            r.anchorMin = Vector2.zero; r.anchorMax = Vector2.one;
            r.offsetMin = Vector2.one * inset; r.offsetMax = -Vector2.one * inset;
        }
        public static TextMeshProUGUI Label(Transform p, string text, Vector2 pos, float size, Color color,
            TextAlignmentOptions align = TextAlignmentOptions.Center, float width = 500)
        {
            var go = new GameObject("L"); go.transform.SetParent(p, false);
            var r = go.AddComponent<RectTransform>(); r.anchoredPosition = pos; r.sizeDelta = new Vector2(width, 36);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = size; t.alignment = align; t.color = color; t.raycastTarget = false;
            return t;
        }

        // Half-height text child. topHalf=true: anchored to upper half; false: lower half.
        // Used for two-line dropdown items (name on top, IP as subscript below).
        public static TextMeshProUGUI MakeStackedText(Transform parent, string name, string text,
            float fontSize, Color color, bool topHalf)
        {
            var go = new GameObject(name); go.transform.SetParent(parent, false);
            var r = go.AddComponent<RectTransform>();
            r.anchorMin = topHalf ? new Vector2(0, 0.5f) : new Vector2(0, 0);
            r.anchorMax = topHalf ? new Vector2(1, 1)    : new Vector2(1, 0.5f);
            r.offsetMin = Vector2.zero; r.offsetMax = Vector2.zero;
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text; t.fontSize = fontSize; t.color = color;
            t.alignment = TextAlignmentOptions.Center; t.raycastTarget = false;
            return t;
        }

        public static Button Btn(Transform p, string label, Vector2 pos, Vector2 size, Color bg, float fontSize, UnityEngine.Events.UnityAction click)
        {
            var go = new GameObject("B_" + label); go.transform.SetParent(p, false);
            var r = go.AddComponent<RectTransform>(); r.anchoredPosition = pos; r.sizeDelta = size;
            var img = go.AddComponent<Image>(); img.color = bg;
            var btn = go.AddComponent<Button>(); btn.targetGraphic = img;
            var c = btn.colors; c.highlightedColor = bg * 1.3f; c.pressedColor = bg * 0.7f; btn.colors = c;
            if (fontSize > 0)
            {
                var lgo = new GameObject("T"); lgo.transform.SetParent(go.transform, false);
                var lr = lgo.AddComponent<RectTransform>();
                lr.anchorMin = Vector2.zero; lr.anchorMax = Vector2.one;
                lr.offsetMin = Vector2.zero; lr.offsetMax = Vector2.zero;
                var t = lgo.AddComponent<TextMeshProUGUI>();
                t.text = label; t.fontSize = fontSize; t.alignment = TextAlignmentOptions.Center; t.color = Color.white;
                t.raycastTarget = false;
            }
            if (click != null) btn.onClick.AddListener(click);
            return btn;
        }

        // Tracks the currently-open dropdown so opening another one auto-closes it.
        static DropdownHandle _openDropdown;

        public class DropdownHandle
        {
            public Button display;
            public GameObject panel;
            public GameObject scrim;
            public TextMeshProUGUI label;    // primary / main text
            public TextMeshProUGUI subLabel; // secondary (subscript) text; null for single-line dropdowns
            public int selectedIndex;

            public void Close()
            {
                if (panel != null) panel.SetActive(false);
                if (scrim != null) scrim.SetActive(false);
                if (_openDropdown == this) _openDropdown = null;
            }

            public void Open()
            {
                if (_openDropdown != null && _openDropdown != this) _openDropdown.Close();
                _openDropdown = this;
                scrim.SetActive(true);
                scrim.transform.SetAsLastSibling();
                panel.SetActive(true);
                panel.transform.SetAsLastSibling();
            }
        }

        // Custom WorldSpace dropdown. TMP_Dropdown's built-in template is painful
        // to construct programmatically, and XR-friendly behavior is simpler when
        // options live as regular Buttons on a child panel.
        public static DropdownHandle Dropdown(Transform parent, Vector2 pos, Vector2 size,
            string[] options, int initialIdx, System.Action<int> onChange,
            bool openUpward = false, float itemHeight = 28f, float itemFontSize = 16f,
            float arrowRightMargin = 10f,
            string[] secondaries = null)
        {
            var h = new DropdownHandle { selectedIndex = initialIdx };
            var bg = new Color(0.3f, 0.3f, 0.4f);
            var subColor = new Color(0.65f, 0.65f, 0.72f);

            if (secondaries != null)
            {
                // Two-line display: name primary (top), IP subscript (bottom).
                h.display = Btn(parent, "", pos, size, bg, 0, null);
                h.label    = MakeStackedText(h.display.transform, "Primary",   options[initialIdx],     14f, Color.white, topHalf: true);
                h.subLabel = MakeStackedText(h.display.transform, "Secondary", secondaries[initialIdx], 10f, subColor,    topHalf: false);
            }
            else
            {
                h.display = Btn(parent, options[initialIdx], pos, size, bg, 18, null);
                h.label = h.display.GetComponentInChildren<TextMeshProUGUI>();
            }

            // Triangle arrow anchored to the right edge of the display button.
            // Rotated 180° for upward-opening dropdowns so it points the open direction.
            var arrowGo = new GameObject("Arrow");
            arrowGo.transform.SetParent(h.display.transform, false);
            var ar = arrowGo.AddComponent<RectTransform>();
            ar.anchorMin = ar.anchorMax = new Vector2(1f, 0.5f);
            ar.pivot = new Vector2(1f, 0.5f);
            ar.anchoredPosition = new Vector2(-arrowRightMargin, 0f);
            ar.sizeDelta = new Vector2(18f, 13f);
            var arrowImg = arrowGo.AddComponent<Image>();
            arrowImg.sprite = IconFactory.TriDown;
            arrowImg.color = Color.white;
            arrowImg.raycastTarget = false;
            if (openUpward) arrowGo.transform.localRotation = Quaternion.Euler(0, 0, 180f);

            // Invisible full-stretch click-catcher. Any tap outside the options panel
            // (including on other dropdowns' displays) hits this first and closes us.
            h.scrim = new GameObject("DD_Scrim");
            h.scrim.transform.SetParent(parent, false);
            var sr = h.scrim.AddComponent<RectTransform>();
            sr.anchorMin = Vector2.zero; sr.anchorMax = Vector2.one;
            sr.offsetMin = Vector2.zero; sr.offsetMax = Vector2.zero;
            var sImg = h.scrim.AddComponent<Image>();
            sImg.color = new Color(1, 1, 1, 0f);
            var sBtn = h.scrim.AddComponent<Button>();
            sBtn.transition = Selectable.Transition.None;
            sBtn.targetGraphic = sImg;
            sBtn.onClick.AddListener(h.Close);
            h.scrim.SetActive(false);

            float panelHeight = options.Length * itemHeight;
            h.panel = Panel(parent, "DD_Options", new Color(0.1f, 0.1f, 0.15f, 0.98f));
            var pR = h.panel.GetComponent<RectTransform>();
            float dir = openUpward ? 1f : -1f;
            pR.anchoredPosition = new Vector2(pos.x, pos.y + dir * (size.y / 2f + panelHeight / 2f));
            pR.sizeDelta = new Vector2(size.x, panelHeight);

            for (int i = 0; i < options.Length; i++)
            {
                int idx = i;
                float yOff = (options.Length - 1) * itemHeight / 2f - i * itemHeight;
                UnityEngine.Events.UnityAction onPick = () =>
                {
                    h.selectedIndex = idx;
                    h.label.text = options[idx];
                    if (h.subLabel != null && secondaries != null) h.subLabel.text = secondaries[idx];
                    onChange?.Invoke(idx);
                    h.Close();
                };
                var itemSize = new Vector2(size.x - 4, itemHeight - 2);
                var itemBg = new Color(0.2f, 0.2f, 0.3f);
                if (secondaries != null)
                {
                    var ib = Btn(h.panel.transform, "", new Vector2(0, yOff), itemSize, itemBg, 0, onPick);
                    MakeStackedText(ib.transform, "Primary",   options[i],     itemFontSize,          Color.white, topHalf: true);
                    MakeStackedText(ib.transform, "Secondary", secondaries[i], itemFontSize * 0.72f,  subColor,    topHalf: false);
                }
                else
                {
                    Btn(h.panel.transform, options[i], new Vector2(0, yOff), itemSize, itemBg, itemFontSize, onPick);
                }
            }
            h.panel.SetActive(false);

            h.display.onClick.AddListener(() =>
            {
                if (h.panel.activeSelf) h.Close();
                else h.Open();
            });
            return h;
        }
    }

    // Procedurally generates mic / stop / circle sprites so we never rely on
    // Unity's built-in resources (which may not load on every Android build).
    // Textures are 128x128 RGBA, cached as static singletons.
    static class IconFactory
    {
        static Sprite _mic, _stop, _circle, _trash, _power, _triDown;

        public static Sprite Mic     => _mic     ??= BuildMic();
        public static Sprite Stop    => _stop    ??= BuildStop();
        public static Sprite Circle  => _circle  ??= BuildCircle();
        public static Sprite Trash   => _trash   ??= BuildTrash();
        public static Sprite Power   => _power   ??= BuildPower();
        public static Sprite TriDown => _triDown ??= BuildTriDown();

        const int Size = 128;

        public static GameObject MakeIconChild(Transform parent, string name, Sprite sprite)
        {
            var go = new GameObject(name);
            go.transform.SetParent(parent, false);
            var r = go.AddComponent<RectTransform>();
            r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
            r.anchoredPosition = Vector2.zero; r.sizeDelta = new Vector2(50, 50);
            var img = go.AddComponent<Image>();
            img.sprite = sprite; img.color = Color.white; img.raycastTarget = false;
            return go;
        }

        static Sprite BuildMic()
        {
            var px = ClearBuffer();
            // Capsule head (rounded rect with fully-rounded ends).
            FillRoundedRect(px, 46, 48, 36, 60, 18, Color.white);
            // Stem down from head.
            FillRoundedRect(px, 62, 30, 4, 20, 2, Color.white);
            // Horizontal base line.
            FillRoundedRect(px, 46, 22, 36, 5, 2.5f, Color.white);
            return MakeSprite(px);
        }

        static Sprite BuildStop()
        {
            var px = ClearBuffer();
            FillRoundedRect(px, 16, 16, 96, 96, 14, Color.white);
            return MakeSprite(px);
        }

        static Sprite BuildCircle()
        {
            var px = ClearBuffer();
            // A rounded rect with r = size/2 is a perfect disc.
            FillRoundedRect(px, 0, 0, Size, Size, Size / 2f, Color.white);
            return MakeSprite(px);
        }

        static Sprite BuildTrash()
        {
            var px = ClearBuffer();
            // Body (bucket).
            FillRoundedRect(px, 40, 26, 48, 58, 5, Color.white);
            // Lid (horizontal bar above body, with a small gap).
            FillRoundedRect(px, 34, 88, 60, 7, 3, Color.white);
            // Handle on top of lid.
            FillRoundedRect(px, 54, 95, 20, 6, 2, Color.white);
            return MakeSprite(px);
        }

        static Sprite BuildPower()
        {
            var px = ClearBuffer();
            // Broken ring (opening at top) + vertical bar through the opening.
            FillRingWithTopGap(px, 64, 58, 38, 30, 22f, Color.white);
            FillRoundedRect(px, 62, 56, 4, 42, 2, Color.white);
            return MakeSprite(px);
        }

        static Sprite BuildTriDown()
        {
            var px = ClearBuffer();
            // Apex at (64, 38) pointing down, base from (34, 84) to (94, 84).
            // Image rect displays texture with y=0 at bottom, so apex appears at bottom.
            FillTriDown(px, 64, 38, 84, 30, Color.white);
            return MakeSprite(px);
        }

        static Color32[] ClearBuffer()
        {
            var px = new Color32[Size * Size];
            // Initialized to (0,0,0,0) by default.
            return px;
        }

        // Anti-aliased rounded rectangle drawn into the buffer.
        // (x0, y0) = bottom-left corner, (w, h) = size, r = corner radius.
        static void FillRoundedRect(Color32[] px, float x0, float y0, float w, float h, float r, Color color)
        {
            int xmin = Mathf.Max(0, Mathf.FloorToInt(x0 - 1));
            int xmax = Mathf.Min(Size - 1, Mathf.CeilToInt(x0 + w + 1));
            int ymin = Mathf.Max(0, Mathf.FloorToInt(y0 - 1));
            int ymax = Mathf.Min(Size - 1, Mathf.CeilToInt(y0 + h + 1));

            for (int y = ymin; y <= ymax; y++)
            {
                for (int x = xmin; x <= xmax; x++)
                {
                    float pxF = x + 0.5f;
                    float pyF = y + 0.5f;
                    float cx = Mathf.Clamp(pxF, x0 + r, x0 + w - r);
                    float cy = Mathf.Clamp(pyF, y0 + r, y0 + h - r);
                    float dx = pxF - cx, dy = pyF - cy;
                    float d  = Mathf.Sqrt(dx * dx + dy * dy) - r;
                    float a  = Mathf.Clamp01(0.5f - d);
                    if (a <= 0) continue;
                    int idx = y * Size + x;
                    byte newA = (byte)Mathf.RoundToInt(color.a * a * 255f);
                    if (newA > px[idx].a)
                        px[idx] = new Color32(
                            (byte)Mathf.RoundToInt(color.r * 255f),
                            (byte)Mathf.RoundToInt(color.g * 255f),
                            (byte)Mathf.RoundToInt(color.b * 255f),
                            newA);
                }
            }
        }

        // Down-pointing isoceles triangle with apex at (cx, yBot) and base running
        // from (cx - halfWidth, yTop) to (cx + halfWidth, yTop). yBot < yTop; on a
        // bottom-left-origin texture the apex ends up at the bottom of the image.
        static void FillTriDown(Color32[] px, float cx, float yBot, float yTop, float halfWidth, Color color)
        {
            int xmin = Mathf.Max(0, Mathf.FloorToInt(cx - halfWidth - 1));
            int xmax = Mathf.Min(Size - 1, Mathf.CeilToInt(cx + halfWidth + 1));
            int ymin = Mathf.Max(0, Mathf.FloorToInt(yBot - 1));
            int ymax = Mathf.Min(Size - 1, Mathf.CeilToInt(yTop + 1));
            float h = yTop - yBot;
            if (h <= 0) return;

            for (int y = ymin; y <= ymax; y++)
            {
                float pyF = y + 0.5f;
                float t = Mathf.Clamp01((pyF - yBot) / h);
                float halfW = halfWidth * t;
                float xL = cx - halfW, xR = cx + halfW;
                float ayBot = Mathf.Clamp01(0.5f + pyF - yBot);
                float ayTop = Mathf.Clamp01(0.5f + yTop - pyF);
                for (int x = xmin; x <= xmax; x++)
                {
                    float pxF = x + 0.5f;
                    float axL = Mathf.Clamp01(0.5f + pxF - xL);
                    float axR = Mathf.Clamp01(0.5f + xR - pxF);
                    float a = ayBot * ayTop * axL * axR;
                    if (a <= 0) continue;
                    int idx = y * Size + x;
                    byte newA = (byte)Mathf.RoundToInt(color.a * a * 255f);
                    if (newA > px[idx].a)
                        px[idx] = new Color32(
                            (byte)Mathf.RoundToInt(color.r * 255f),
                            (byte)Mathf.RoundToInt(color.g * 255f),
                            (byte)Mathf.RoundToInt(color.b * 255f),
                            newA);
                }
            }
        }

        // Annulus (ring) with a wedge removed at the top. halfGapDeg = half-angle
        // of the gap measured from +y axis. Used for the power-button icon.
        static void FillRingWithTopGap(Color32[] px, float cx, float cy, float rOuter, float rInner, float halfGapDeg, Color color)
        {
            int xmin = Mathf.Max(0, Mathf.FloorToInt(cx - rOuter - 1));
            int xmax = Mathf.Min(Size - 1, Mathf.CeilToInt(cx + rOuter + 1));
            int ymin = Mathf.Max(0, Mathf.FloorToInt(cy - rOuter - 1));
            int ymax = Mathf.Min(Size - 1, Mathf.CeilToInt(cy + rOuter + 1));
            float gapRad = halfGapDeg * Mathf.Deg2Rad;

            for (int y = ymin; y <= ymax; y++)
            {
                for (int x = xmin; x <= xmax; x++)
                {
                    float pxF = x + 0.5f, pyF = y + 0.5f;
                    float dx = pxF - cx, dy = pyF - cy;
                    float d = Mathf.Sqrt(dx * dx + dy * dy);
                    // Distance-from-annulus (negative inside the ring, 0 on either edge).
                    float dRing = Mathf.Max(d - rOuter, rInner - d);
                    float a = Mathf.Clamp01(0.5f - dRing);
                    if (a <= 0) continue;
                    // Skip the wedge at the top.
                    if (dy > 0 && Mathf.Atan2(Mathf.Abs(dx), dy) < gapRad) continue;
                    int idx = y * Size + x;
                    byte newA = (byte)Mathf.RoundToInt(color.a * a * 255f);
                    if (newA > px[idx].a)
                        px[idx] = new Color32(
                            (byte)Mathf.RoundToInt(color.r * 255f),
                            (byte)Mathf.RoundToInt(color.g * 255f),
                            (byte)Mathf.RoundToInt(color.b * 255f),
                            newA);
                }
            }
        }

        static Sprite MakeSprite(Color32[] px)
        {
            var tex = new Texture2D(Size, Size, TextureFormat.RGBA32, false);
            tex.filterMode = FilterMode.Bilinear;
            tex.wrapMode   = TextureWrapMode.Clamp;
            tex.hideFlags  = HideFlags.HideAndDontSave;
            tex.SetPixels32(px);
            tex.Apply();
            return Sprite.Create(tex, new Rect(0, 0, Size, Size), new Vector2(0.5f, 0.5f), 100f);
        }
    }
}
