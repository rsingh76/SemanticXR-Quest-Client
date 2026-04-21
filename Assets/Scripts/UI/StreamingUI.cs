using System.Collections.Generic;
using UnityEngine;
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

        string _portString = "50051";
        TextMeshProUGUI _portLabel;

        static readonly int[] FpsOptions = { 2, 3, 5, 10, 15, 20, 25, 30 };
        int _fpsIndex;
        int _selectedFps = 2;
        TextMeshProUGUI _fpsLabel;

        Button _connectBtn;
        TextMeshProUGUI _errorText, _statusText, _statsText, _diagText, _validationText, _timingText;

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

            Mk.Label(_connectPanel.transform, "Port", new Vector2(0, 50), 16, new Color(0.6f, 0.6f, 0.65f));
            _portLabel = Mk.Label(_connectPanel.transform, _portString, new Vector2(0, 22), 20, Color.white);
            Mk.Btn(_connectPanel.transform, "", new Vector2(0, 22), new Vector2(200, 30), new Color(0.2f, 0.2f, 0.25f, 0.5f), 0, () => OpenKB("port"));

            Mk.Label(_connectPanel.transform, "FPS", new Vector2(-80, -15), 16, new Color(0.6f, 0.6f, 0.65f), TextAlignmentOptions.MidlineRight, 160);
            Mk.Btn(_connectPanel.transform, "<", new Vector2(30, -15), new Vector2(36, 30), new Color(0.3f, 0.3f, 0.4f), 20, () => ChangeFps(-1));
            _fpsLabel = Mk.Label(_connectPanel.transform, _selectedFps.ToString(), new Vector2(80, -15), 20, Color.white);
            Mk.Btn(_connectPanel.transform, ">", new Vector2(130, -15), new Vector2(36, 30), new Color(0.3f, 0.3f, 0.4f), 20, () => ChangeFps(1));

            _connectBtn = Mk.Btn(_connectPanel.transform, "Connect", new Vector2(0, -65), new Vector2(220, 50), new Color(0.15f, 0.55f, 0.25f), 24, OnConnect);
            _errorText = Mk.Label(_connectPanel.transform, "", new Vector2(0, -110), 15, new Color(1f, 0.4f, 0.4f));

            _streamingPanel = Mk.Panel(bg.transform, "Streaming", Color.clear);
            Mk.Stretch(_streamingPanel, 20);
            Mk.Label(_streamingPanel.transform, "Streaming", new Vector2(0, 155), 26, new Color(0.3f, 0.9f, 0.4f));
            _statusText = Mk.Label(_streamingPanel.transform, "", new Vector2(0, 120), 16, Color.white);
            _statsText = Mk.Label(_streamingPanel.transform, "", new Vector2(0, 95), 16, new Color(0.7f, 0.7f, 0.75f));

            // Validation status (green = passed, red = failed)
            _validationText = Mk.Label(_streamingPanel.transform, "Validating...", new Vector2(0, 65), 14, new Color(0.9f, 0.9f, 0.3f), TextAlignmentOptions.TopLeft, 560);
            _validationText.enableWordWrapping = true;
            _validationText.GetComponent<RectTransform>().sizeDelta = new Vector2(560, 40);

            // Diagnostics: drop counters
            _diagText = Mk.Label(_streamingPanel.transform, "", new Vector2(0, 35), 13, new Color(0.6f, 0.6f, 0.65f), TextAlignmentOptions.TopLeft, 560);
            _diagText.enableWordWrapping = true;
            _diagText.GetComponent<RectTransform>().sizeDelta = new Vector2(560, 30);

            // Timing: per-stage latency (ms), rolling average
            _timingText = Mk.Label(_streamingPanel.transform, "timing: warmup...", new Vector2(0, -5), 13, new Color(0.55f, 0.75f, 0.95f), TextAlignmentOptions.TopLeft, 560);
            _timingText.enableWordWrapping = true;
            _timingText.GetComponent<RectTransform>().sizeDelta = new Vector2(560, 50);

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
        void OpenKB(string field)
        {
            _editField = field;
            _keyboard = TouchScreenKeyboard.Open(
                field == "ip" ? _ipAddress : _portString,
                TouchScreenKeyboardType.DecimalPad, false, false, false, false, "", 21);
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
            _canvasRect.sizeDelta = new Vector2(650, 420);
            _connectBtn.interactable = true;
            _errorText.text = "";
            _connectPanel.SetActive(true);
            _streamingPanel.SetActive(false);
            Position();
        }
        void ShowStreaming()
        {
            _connectPanel.SetActive(false);
            _streamingPanel.SetActive(true);
            _canvasRect.sizeDelta = new Vector2(650, 420);
            Position();
        }
        void ShowError(string msg) { _errorText.text = msg; _connectBtn.interactable = true; }

        void Update()
        {
            if (!_positioned && Camera.main != null) { Position(); _positioned = true; }

            if (_keyboard != null)
            {
                if (_keyboard.status == TouchScreenKeyboard.Status.Visible ||
                    _keyboard.status == TouchScreenKeyboard.Status.Done)
                {
                    if (_editField == "ip") { _ipAddress = _keyboard.text; _ipLabel.text = _ipAddress; }
                    else { _portString = _keyboard.text; _portLabel.text = _portString; }
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
                _statsText.text = $"Sent: {_orchestrator.FrameCount}  |  Queue: {_orchestrator.QueuedFrames}";

                // Validation status
                var valErr = _orchestrator.ValidationError;
                if (valErr != null)
                {
                    _validationText.text = $"VALIDATION FAILED:\n{valErr}";
                    _validationText.color = new Color(1f, 0.3f, 0.3f);
                }
                else if (_orchestrator.PoseMethod != "unknown")
                {
                    _validationText.text = $"Validated | Pose: {_orchestrator.PoseMethod}";
                    _validationText.color = new Color(0.3f, 0.9f, 0.4f);
                }

                // Drop counters
                int totalDropped = _orchestrator.TotalDropped;
                var dropColor = totalDropped > 0 ? new Color(1f, 0.7f, 0.3f) : new Color(0.5f, 0.5f, 0.55f);
                _diagText.color = dropColor;
                _diagText.text = $"Dropped: {totalDropped}" +
                    (totalDropped > 0 ? $"  (pose={_orchestrator.DroppedNoPose}" +
                        $" ts={_orchestrator.DroppedNoTimestamp}" +
                        $" intr={_orchestrator.DroppedBadIntrinsics}" +
                        $" rgb={_orchestrator.DroppedNoRgb}" +
                        $" depth={_orchestrator.DroppedNoDepth})" : "");

                // Timing line (rolling avg over 30 frames from Orchestrator)
                _timingText.text = _orchestrator.TimingLine;
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
}
