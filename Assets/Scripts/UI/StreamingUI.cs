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

        static readonly string[] IpPresets = { "192.168.1.2", "192.168.1.5", "10.195.21.84", "10.193.43.94", "192.168.1.100" };
        int _ipIndex;
        string _ipAddress;
        TextMeshProUGUI _ipLabel;

        // Mirror production: the real server talks gRPC on 50051, so gRPC
        // debug/prod lives at 50051. TCP debug server moves to 50055.
        const string DefaultTcpPort  = "50055";
        const string DefaultGrpcPort = "50051";
        string _portString = DefaultGrpcPort;   // initial value updated in Awake based on orchestrator's transport
        TextMeshProUGUI _portLabel;

        string _audioPortString = "50054";
        TextMeshProUGUI _audioPortLabel;

        static readonly int[] FpsOptions = { 2, 3, 5, 6, 7, 10, 15, 20, 25, 30 };
        int _fpsIndex;
        int _selectedFps = 2;
        TextMeshProUGUI _fpsLabel;

        Button _transportBtn;
        TextMeshProUGUI _transportLabel;

        Button _connectBtn;
        TextMeshProUGUI _errorText, _statusText, _statsText;

        AudioStreamController _audio;
        Button _micBtn;
        Image _micBtnBg;
        GameObject _micIconGroup, _stopIconGroup;
        TextMeshProUGUI _dictationText, _dictationStatus;
        static readonly Color MicIdleColor = new Color(0.15f, 0.45f, 0.75f);
        static readonly Color MicActiveColor = new Color(0.85f, 0.2f, 0.2f);

        TouchScreenKeyboard _keyboard;
        string _editField;
        bool _positioned;

        void Awake()
        {
            _ipAddress = IpPresets[0];

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
            Mk.Btn(_connectPanel.transform, "<", new Vector2(-150, 85), new Vector2(36, 30), new Color(0.3f, 0.3f, 0.4f), 20, () => ChangeIp(-1));
            _ipLabel = Mk.Label(_connectPanel.transform, _ipAddress, new Vector2(0, 85), 20, Color.white);
            Mk.Btn(_connectPanel.transform, ">", new Vector2(150, 85), new Vector2(36, 30), new Color(0.3f, 0.3f, 0.4f), 20, () => ChangeIp(1));
            Mk.Btn(_connectPanel.transform, "Custom", new Vector2(230, 85), new Vector2(80, 30), new Color(0.3f, 0.3f, 0.4f), 14, () => OpenKB("ip"));

            // Two ports side-by-side: frames (raw TCP or gRPC) and audio (gRPC VisualizerServer).
            Mk.Label(_connectPanel.transform, "Frames Port", new Vector2(-115, 50), 16, new Color(0.6f, 0.6f, 0.65f));
            _portLabel = Mk.Label(_connectPanel.transform, _portString, new Vector2(-115, 22), 20, Color.white);
            Mk.Btn(_connectPanel.transform, "", new Vector2(-115, 22), new Vector2(200, 30), new Color(0.2f, 0.2f, 0.25f, 0.5f), 0, () => OpenKB("port"));

            Mk.Label(_connectPanel.transform, "Audio Port", new Vector2(115, 50), 16, new Color(0.6f, 0.6f, 0.65f));
            _audioPortLabel = Mk.Label(_connectPanel.transform, _audioPortString, new Vector2(115, 22), 20, Color.white);
            Mk.Btn(_connectPanel.transform, "", new Vector2(115, 22), new Vector2(200, 30), new Color(0.2f, 0.2f, 0.25f, 0.5f), 0, () => OpenKB("audioPort"));

            Mk.Label(_connectPanel.transform, "FPS", new Vector2(-220, -15), 16, new Color(0.6f, 0.6f, 0.65f), TextAlignmentOptions.MidlineRight, 160);
            Mk.Btn(_connectPanel.transform, "<", new Vector2(-110, -15), new Vector2(36, 30), new Color(0.3f, 0.3f, 0.4f), 20, () => ChangeFps(-1));
            _fpsLabel = Mk.Label(_connectPanel.transform, _selectedFps.ToString(), new Vector2(-60, -15), 20, Color.white);
            Mk.Btn(_connectPanel.transform, ">", new Vector2(-10, -15), new Vector2(36, 30), new Color(0.3f, 0.3f, 0.4f), 20, () => ChangeFps(1));

            Mk.Label(_connectPanel.transform, "Transport", new Vector2(60, -15), 14, new Color(0.6f, 0.6f, 0.65f), TextAlignmentOptions.MidlineLeft, 100);
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
            // Format: "Sent N · Q M · Drops K · X FPS". Color goes orange on drops.
            _statsText = Mk.Label(_streamingPanel.transform, "",
                new Vector2(-10, 175), 13, new Color(0.55f, 0.55f, 0.6f),
                TextAlignmentOptions.TopRight, 320);
            _statsText.GetComponent<RectTransform>().sizeDelta = new Vector2(320, 22);

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

            _micBtn = Mk.Btn(_streamingPanel.transform, "", new Vector2(0, -75),
                new Vector2(90, 90), MicIdleColor, 0, OnMicClicked);
            _micBtnBg = _micBtn.GetComponent<Image>();
            // Procedurally generated circular disc — no dependency on built-in sprites.
            _micBtnBg.sprite = IconFactory.Circle;
            _micBtnBg.type   = Image.Type.Simple;

            _micIconGroup  = IconFactory.MakeIconChild(_micBtn.transform, "MicIcon",  IconFactory.Mic);
            _stopIconGroup = IconFactory.MakeIconChild(_micBtn.transform, "StopIcon", IconFactory.Stop);
            _stopIconGroup.SetActive(false);

            // Diagnostic: log raw pointer events on the button so we can tell
            // whether taps are even reaching the GameObject independent of the
            // Button component's onClick path.
            var trig = _micBtn.gameObject.AddComponent<EventTrigger>();
            AddTrigger(trig, EventTriggerType.PointerEnter, () => Debug.Log("[StreamingUI] pointer ENTER mic"));
            AddTrigger(trig, EventTriggerType.PointerDown,  () => Debug.Log("[StreamingUI] pointer DOWN mic"));
            AddTrigger(trig, EventTriggerType.PointerUp,    () => Debug.Log("[StreamingUI] pointer UP mic"));
            AddTrigger(trig, EventTriggerType.PointerClick, () => Debug.Log("[StreamingUI] pointer CLICK mic"));

            _dictationStatus = Mk.Label(_streamingPanel.transform, "Tap the mic to speak",
                new Vector2(0, -115), 12, new Color(0.55f, 0.55f, 0.6f));
            // Clamp the status label so a long error message can't expand the
            // RectTransform and paint over the rest of the panel.
            var statusR = _dictationStatus.GetComponent<RectTransform>();
            statusR.sizeDelta = new Vector2(560, 18);
            _dictationStatus.overflowMode      = TextOverflowModes.Ellipsis;
            _dictationStatus.textWrappingMode  = TextWrappingModes.NoWrap;

            Mk.Btn(_streamingPanel.transform, "Disconnect", new Vector2(0, -140), new Vector2(200, 45), new Color(0.6f, 0.15f, 0.15f), 22, () => _orchestrator.Disconnect());
            _streamingPanel.SetActive(false);
        }

        void ChangeIp(int d)
        {
            _ipIndex = (_ipIndex + d + IpPresets.Length) % IpPresets.Length;
            _ipAddress = IpPresets[_ipIndex];
            _ipLabel.text = _ipAddress;
        }
        void ChangeFps(int d)
        {
            _fpsIndex = Mathf.Clamp(_fpsIndex + d, 0, FpsOptions.Length - 1);
            _selectedFps = FpsOptions[_fpsIndex];
            _fpsLabel.text = _selectedFps.ToString();
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
            Position();
        }

        void ResetDictation()
        {
            if (_audio != null) _audio.Cancel();
            if (_dictationText   != null) _dictationText.text = "";
            OnDictationListeningChanged(false);
            if (_dictationStatus != null) _dictationStatus.text = "Tap the mic to speak";
        }
        void ShowStreaming()
        {
            _connectPanel.SetActive(false);
            _streamingPanel.SetActive(true);
            _canvasRect.sizeDelta = new Vector2(650, 420);
            // Tell the audio client which server to send to — same IP as the
            // frames stream, separate user-configurable port for the
            // vis_proto VisualizerServer.
            if (_audio != null && int.TryParse(_audioPortString, out int audioPort))
                _audio.Configure(_ipAddress, audioPort);
            Position();
        }
        void ShowError(string msg) { _errorText.text = msg; _connectBtn.interactable = true; }

        void OnMicClicked()
        {
            Debug.Log($"[StreamingUI] mic clicked, listening={_audio.IsListening}");
            StartCoroutine(FlashMicButton());
            if (_dictationStatus != null)
                _dictationStatus.text = _audio.IsListening ? "Stopping..." : "Tap registered — starting mic...";
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
            if (_dictationStatus != null) _dictationStatus.text = s;
            if (_dictationText   != null) _dictationText.text   = s;  // also echo to the text box for now
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
            if (_dictationStatus != null) _dictationStatus.text = listening ? "Listening..." : "Tap the mic to speak";
        }

        void OnDictationErrorReceived(string msg)
        {
            Debug.LogWarning($"[StreamingUI] dictation error: {msg}");
            if (_dictationStatus != null && !string.IsNullOrEmpty(msg))
                _dictationStatus.text = $"<color=#ff6b6b>Error: {msg}</color>";
            // Button state is user-intent driven — do not flip it on error.
        }

        void Update()
        {
            if (!_positioned && Camera.main != null) { Position(); _positioned = true; }

            if (_keyboard != null)
            {
                if (_keyboard.status == TouchScreenKeyboard.Status.Visible ||
                    _keyboard.status == TouchScreenKeyboard.Status.Done)
                {
                    switch (_editField)
                    {
                        case "ip":        _ipAddress       = _keyboard.text; _ipLabel.text        = _ipAddress;       break;
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

                // Compact stats line: Sent · Q · Drops · FPS  (color-coded on drops).
                int totalDropped = _orchestrator.TotalDropped;
                _statsText.color = totalDropped > 0
                    ? new Color(1f, 0.7f, 0.3f)
                    : new Color(0.55f, 0.55f, 0.6f);
                string fpsStr = _orchestrator.CaptureFps > 0 ? $"{_orchestrator.CaptureFps:F1}" : "—";
                _statsText.text = $"Sent {_orchestrator.FrameCount} · Q {_orchestrator.QueuedFrames} · Drops {totalDropped} · {fpsStr} FPS";
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
            btn.onClick.AddListener(click);
            return btn;
        }
    }

    // Procedurally generates mic / stop / circle sprites so we never rely on
    // Unity's built-in resources (which may not load on every Android build).
    // Textures are 128x128 RGBA, cached as static singletons.
    static class IconFactory
    {
        static Sprite _mic, _stop, _circle;

        public static Sprite Mic    => _mic    ??= BuildMic();
        public static Sprite Stop   => _stop   ??= BuildStop();
        public static Sprite Circle => _circle ??= BuildCircle();

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
