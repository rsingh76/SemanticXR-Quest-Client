using System.Collections.Generic;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;
using UnityEngine.XR;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
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

        [Header("Body-Locked UI (shared: panel + mic orb)")]
        [SerializeField] float bodyPosSmoothTime = 0.3f;
        // Larger = slower yaw catch-up. Head twists don't drag the UI; body turns do.
        [SerializeField] float bodyYawSmoothTime = 1.2f;

        [Header("Streaming Panel Placement (below orb row)")]
        [SerializeField] float streamingPanelDistance = 1.0f;
        // Placed so the panel top sits ~4° below the orb-row bottom at default orb offsets.
        [SerializeField] float streamingPanelVerticalOffset = -0.9f;

        // Single body-forward vector shared by the panel and the orb so they
        // always yaw together. Each widget keeps its own position velocity
        // because they sit at different distances/heights.
        Vector3 _bodyForward = Vector3.forward;
        Vector3 _panelVelocity;

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
            ("Lab Server (ILLIXR)", "10.193.192.179"),
        };
        int _ipIndex;
        string _ipAddress;
        Mk.DropdownHandle _ipDropdown;

        // Mirror production: the real server talks gRPC on 50051, so gRPC
        // debug/prod lives at 50051. TCP debug server moves to 50055.
        const string DefaultTcpPort  = "50055";
        const string DefaultGrpcPort = "50051";
        const string DefaultIllixrServerPort = "50057";
        const string DefaultIllixrClientPort = "50058";
        
        string _portString = DefaultGrpcPort;   // initial value updated in Awake based on orchestrator's transport
        TextMeshProUGUI _portLabel;

        string _audioPortString = "50054";
        TextMeshProUGUI _audioPortLabel;

        string          _illixrServerPort    = DefaultIllixrServerPort;
        string          _illixrClientPort    = DefaultIllixrClientPort;
        TextMeshProUGUI _illixrServerPortLabel;
        TextMeshProUGUI _illixrClientPortLabel;
        GameObject      _tcpGrpcConfigRows;
        GameObject      _illixrConfigRows;
        
        static readonly int[] FpsOptions = { 2, 3, 5, 6, 7, 10, 15, 20, 25 };
        int _fpsIndex;
        int _selectedFps = 2;
        Mk.DropdownHandle _fpsDropdown;

        Button _transportBtn;
        TextMeshProUGUI _transportLabel;

        Button _connectBtn;
        TextMeshProUGUI _errorText, _statusText;

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
        bool _recallWasDown;

        [Header("Mic Orb Position")]
        [SerializeField] float micOrbDistance = 0.55f;
        [SerializeField] float micOrbVerticalOffset = -0.35f;

        GameObject _micOrb;
        Vector3 _orbVelocity;

        CoachMarks _coach;
        Button _skipTutorialBtn;
        TextMeshProUGUI _skipTutorialLabel;

        SettingsPanel _settings;
        Button _gearBtn;

        ConnectSettingsPanel _connectSettings;
        Button _connectGearBtn;

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
            _orchestrator.OnQueryResponse += OnIllixrQueryResponseReceived;

            // Point-cloud visualizer lives on a separate child GameObject so
            // spheres are parented there, not on the StreamCanvas.
            var pcHost = new GameObject("PointClouds");
            pcHost.transform.SetParent(transform, worldPositionStays: false);
            _visualizer = pcHost.AddComponent<PointCloudVisualizer>();

            // Offscreen-arrow hint: head-locked arrow that appears at the edge
            // of the viewport whenever any rendered centroid is outside the
            // camera frustum. Sibling to PointClouds so it shares the session's
            // world-space origin.
            var arrowHost = new GameObject("OffscreenArrow");
            arrowHost.transform.SetParent(transform, worldPositionStays: false);
            arrowHost.AddComponent<OffscreenPointCloudArrow>().Bind(_visualizer);

            // Onboarding coach marks (S1 Connect, S2 Hold-to-talk, S3 Color popup).
            // Targets are wired in BuildUI() once the panels exist.
            _coach = gameObject.AddComponent<CoachMarks>();
            _visualizer.OnFirstClusterRendered += () => _coach?.OnFirstCluster();

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

        // Snap the shared body-forward to the current head flat-forward.
        // Called by InitStreamingPanelPose / InitMicOrbPose on ShowStreaming
        // and A-button recall so panel and orb restart perfectly in sync.
        void SnapSharedBodyForward()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var fwd = cam.transform.forward; fwd.y = 0;
            if (fwd.sqrMagnitude < 0.001f) fwd = Vector3.forward;
            _bodyForward = fwd.normalized;
        }

        // Slowly catch body-forward up to head flat-forward. Runs once per frame
        // in Update so both panel and orb see the same updated vector.
        void UpdateSharedBodyForward()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var headFwd = cam.transform.forward; headFwd.y = 0;
            if (headFwd.sqrMagnitude < 0.001f) return;
            headFwd.Normalize();
            float t = 1f - Mathf.Exp(-Time.deltaTime / Mathf.Max(0.01f, bodyYawSmoothTime));
            _bodyForward = Vector3.Slerp(_bodyForward, headFwd, t).normalized;
        }

        // Snap the streaming panel to below-the-orbs. Uses streaming-specific
        // distance/vertical so the panel sits visually underneath the orb row,
        // not at eye level like the Connect panel. Shares body-forward with
        // the orb so the pair yaws together.
        void InitStreamingPanelPose()
        {
            var cam = Camera.main;
            if (cam == null || _canvas == null) return;
            SnapSharedBodyForward();
            _panelVelocity = Vector3.zero;
            var p = cam.transform.position + _bodyForward * streamingPanelDistance;
            p.y = cam.transform.position.y + streamingPanelVerticalOffset;
            _canvas.transform.position = p;
            _canvas.transform.rotation = Quaternion.LookRotation(p - cam.transform.position);
        }

        // Body-lock update for the streaming panel. Reads the shared body-forward
        // (already yaw-stepped this frame by UpdateSharedBodyForward) and damps
        // the canvas position toward its body-locked target below the orb row.
        void UpdateStreamingPanelPose()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var target = cam.transform.position + _bodyForward * streamingPanelDistance;
            target.y = cam.transform.position.y + streamingPanelVerticalOffset;
            _canvas.transform.position = Vector3.SmoothDamp(
                _canvas.transform.position, target, ref _panelVelocity, bodyPosSmoothTime);
            _canvas.transform.rotation = Quaternion.LookRotation(
                _canvas.transform.position - cam.transform.position);
        }

        // Resolve the current IP back to a preset name, or fall back to the raw IP
        // for custom entries. Used for the "Streaming to X" status line.
        string CurrentServerName()
        {
            for (int i = 0; i < IpPresets.Length; i++)
                if (IpPresets[i].ip == _ipAddress) return IpPresets[i].name;
            return _ipAddress;
        }

        // Press A on the right controller to snap panel + mic orb back in front of the user.
        // Does not touch the tracking origin — we just reposition our own world-space GameObjects.
        void TryHandleRecallButton()
        {
            var rh = InputDevices.GetDeviceAtXRNode(XRNode.RightHand);
            if (!rh.isValid) return;
            if (!rh.TryGetFeatureValue(CommonUsages.primaryButton, out bool down)) return;
            if (down && !_recallWasDown)
            {
                if (_streamingPanel != null && _streamingPanel.activeSelf) InitStreamingPanelPose();
                else Position();
                InitMicOrbPose();
                Debug.Log("[StreamingUI] Recall (A): repositioned panel and orb");
            }
            _recallWasDown = down;
        }

        // Hold-to-talk on the mic orb. We poll the trigger directly here for the
        // same reason XRInteractionSetup does: the project's XRRayInteractors
        // don't have a UI press input action wired, so XRUIInputModule never
        // dispatches PointerDown/PointerUp and EventTrigger handlers don't fire.
        // Mirrors XRInteractionSetup's pattern: edge-detect trigger, look up the
        // current UI raycast hit, dispatch when the hit is the mic button.
        //
        // Latch behavior: once down-on-mic starts recording, ANY trigger-up
        // stops and sends — the controller can wander off the orb mid-sentence
        // without losing the take.
        void TryHandleMicHold()
        {
            if (_audio == null || _micBtn == null) return;

            bool triggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                            || OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            bool triggerUp   = OVRInput.GetUp  (OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                            || OVRInput.GetUp  (OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);

            if (triggerDown && !_audio.IsListening && IsRayHittingMic())
            {
                Debug.Log("[StreamingUI] mic press — starting recording");
                _audio.StartListening();
            }
            else if (triggerUp && _audio.IsListening)
            {
                Debug.Log("[StreamingUI] mic release — stopping & sending");
                _audio.StopListening();
            }
        }

        bool IsRayHittingMic()
        {
            var micT = _micBtn.transform;
            foreach (var ray in FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None))
            {
                if (!ray.TryGetCurrentUIRaycastResult(out var result) || result.gameObject == null)
                    continue;
                // Walk up — the raycast hit can be the icon child rather than
                // the button itself, depending on which graphic the ray pierced.
                for (var t = result.gameObject.transform; t != null; t = t.parent)
                    if (t == micT) return true;
            }
            return false;
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

            Mk.Label(_connectPanel.transform, "System IP", new Vector2(0, 115), 16, new Color(0.6f, 0.6f, 0.65f));
            var ipNames = new string[IpPresets.Length];
            var ipAddrs = new string[IpPresets.Length];
            for (int i = 0; i < IpPresets.Length; i++) { ipNames[i] = IpPresets[i].name; ipAddrs[i] = IpPresets[i].ip; }
            _ipDropdown = Mk.Dropdown(_connectPanel.transform, new Vector2(-40, 85), new Vector2(260, 36),
                ipNames, _ipIndex,
                i => { _ipIndex = i; _ipAddress = ipAddrs[i]; },
                itemHeight: 34f, secondaries: ipAddrs);
            Mk.Btn(_connectPanel.transform, "Custom", new Vector2(150, 85), new Vector2(80, 30), new Color(0.3f, 0.3f, 0.4f), 14, () => OpenKB("ip"));

            // Row 2: three columns — frames port (TCP/gRPC), audio port (gRPC VisualizerServer), and Transport toggle.
            var framesPortGroup = Mk.Panel(_connectPanel.transform, "FramesPortGroup", Color.clear);
            framesPortGroup.GetComponent<RectTransform>().anchoredPosition = new Vector2(-175, 17);
            framesPortGroup.GetComponent<RectTransform>().sizeDelta = new Vector2(160, 50);
            Mk.Label(framesPortGroup.transform, "Frames Port", new Vector2(0, 20), 16, new Color(0.6f, 0.6f, 0.65f));
            _portLabel = Mk.Label(framesPortGroup.transform, _portString, Vector2.zero, 20, Color.white);
            Mk.Btn(framesPortGroup.transform, "", Vector2.zero, new Vector2(160, 30),
                new Color(0.2f, 0.2f, 0.25f, 0.5f), 0, () => OpenKB("port"));

            // Audio port group
            var audioPortGroup = Mk.Panel(_connectPanel.transform, "AudioPortGroup", Color.clear);
            audioPortGroup.GetComponent<RectTransform>().anchoredPosition = new Vector2(0, 17);
            audioPortGroup.GetComponent<RectTransform>().sizeDelta = new Vector2(160, 50);
            Mk.Label(audioPortGroup.transform, "Audio Port", new Vector2(0, 20), 16, new Color(0.6f, 0.6f, 0.65f));
            _audioPortLabel = Mk.Label(audioPortGroup.transform, _audioPortString, Vector2.zero, 20, Color.white);
            Mk.Btn(audioPortGroup.transform, "", Vector2.zero, new Vector2(160, 30),
                new Color(0.2f, 0.2f, 0.25f, 0.5f), 0, () => OpenKB("audioPort"));

            _tcpGrpcConfigRows = Mk.Panel(_connectPanel.transform, "TcpGrpcRows", Color.clear);
            framesPortGroup.transform.SetParent(_tcpGrpcConfigRows.transform, false);
            audioPortGroup.transform.SetParent(_tcpGrpcConfigRows.transform, false);
            
            Mk.Label(_connectPanel.transform, "Transport", new Vector2(175, 45), 16, new Color(0.6f, 0.6f, 0.65f));
            _transportBtn = Mk.Btn(_connectPanel.transform, _orchestrator.Transport.ToString(),
                new Vector2(175, 17), new Vector2(160, 30),
                new Color(0.3f, 0.3f, 0.4f), 16, ToggleTransport);
            _transportLabel = _transportBtn.GetComponentInChildren<TextMeshProUGUI>();

            // Wrap existing Frames Port + Audio Port labels into a hideable container
            // so they disappear when ILLIXR transport is selected.
            // The labels were already parented to _connectPanel by Mk.Label/Btn above,
            // so we re-parent their immediate parents (the button GameObjects) here.
            _tcpGrpcConfigRows = Mk.Panel(_connectPanel.transform, "TcpGrpcRows", Color.clear);
            {
                var r = _tcpGrpcConfigRows.GetComponent<RectTransform>();
                r.anchoredPosition = Vector2.zero;
                r.sizeDelta        = new Vector2(590, 70);
            }
            _portLabel.transform.parent.SetParent(_tcpGrpcConfigRows.transform, false);
            _audioPortLabel.transform.parent.SetParent(_tcpGrpcConfigRows.transform, false);

            // ILLIXR config rows: server port + client port.
            // Server IP reuses the existing _ipAddress / IpPresets dropdown.
            // Client IP is auto-detected from the headset's active network interface.
            _illixrConfigRows = Mk.Panel(_connectPanel.transform, "IllixrRows", Color.clear);
            {
                var r = _illixrConfigRows.GetComponent<RectTransform>();
                r.anchoredPosition = new Vector2(0, 17);
                r.sizeDelta        = new Vector2(590, 70);
            }

            Mk.Label(_illixrConfigRows.transform, "Server Port",
                new Vector2(-175, 20), 16, new Color(0.6f, 0.6f, 0.65f));
            _illixrServerPortLabel = Mk.Label(_illixrConfigRows.transform,
                _illixrServerPort, new Vector2(-175, -5), 20, Color.white);
            Mk.Btn(_illixrConfigRows.transform, "", new Vector2(-175, -5),
                new Vector2(160, 30), new Color(0.2f, 0.2f, 0.25f, 0.5f), 0,
                () => OpenKB("illixrServerPort"));

            Mk.Label(_illixrConfigRows.transform, "Client Port",
                new Vector2(0, 20), 16, new Color(0.6f, 0.6f, 0.65f));
            _illixrClientPortLabel = Mk.Label(_illixrConfigRows.transform,
                _illixrClientPort, new Vector2(0, -5), 20, Color.white);
            Mk.Btn(_illixrConfigRows.transform, "", new Vector2(0, -5),
                new Vector2(160, 30), new Color(0.2f, 0.2f, 0.25f, 0.5f), 0,
                () => OpenKB("illixrClientPort"));

            // Hidden by default — TCP is the default transport
            _illixrConfigRows.SetActive(false);

            Mk.Label(_connectPanel.transform, "FPS", new Vector2(-150, -35), 16, new Color(0.6f, 0.6f, 0.65f), TextAlignmentOptions.MidlineRight, 160);
            var fpsStrings = new string[FpsOptions.Length];
            for (int i = 0; i < FpsOptions.Length; i++) fpsStrings[i] = FpsOptions[i].ToString();
            _fpsDropdown = Mk.Dropdown(_connectPanel.transform, new Vector2(30, -35), new Vector2(140, 30),
                fpsStrings, _fpsIndex,
                i => { _fpsIndex = i; _selectedFps = FpsOptions[i]; },
                openUpward: true, itemHeight: 22f, itemFontSize: 15f, arrowRightMargin: 18f);

            _connectBtn = Mk.Btn(_connectPanel.transform, "Connect", new Vector2(0, -90), new Vector2(220, 50), new Color(0.15f, 0.55f, 0.25f), 24, OnConnect);
            _errorText = Mk.Label(_connectPanel.transform, "", new Vector2(0, -140), 15, new Color(1f, 0.4f, 0.4f));

            // Skip-tutorial toggle, bottom-right of the Connect panel. Defaults
            // to "Skip tutorial" (i.e., tutorial is currently ON). The state
            // resets every time the Connect panel is shown — see ShowConnect().
            // y=-170 sits just below the error-text band (y=[-158, -122]) and
            // within the panel's drawable y∈[-180, +180].
            _skipTutorialBtn = Mk.Btn(_connectPanel.transform, "Skip tutorial",
                new Vector2(220, -170), new Vector2(130, 22),
                new Color(0.25f, 0.25f, 0.32f), 12, ToggleSkipTutorial);
            _skipTutorialLabel = _skipTutorialBtn.GetComponentInChildren<TextMeshProUGUI>();

            // Connect-side settings: gear at top-right opens a sub-panel below
            // the Connect panel for the per-session max-depth cap. Same pattern
            // as the streaming-side gear/SettingsPanel.
            _connectGearBtn = Mk.Btn(_connectPanel.transform, "",
                new Vector2(260, 170), new Vector2(28, 28),
                new Color(0.25f, 0.27f, 0.35f), 0, ToggleConnectSettings);
            var connectGearIcon = IconFactory.MakeIconChild(_connectGearBtn.transform, "GearIcon", IconFactory.Gear);
            connectGearIcon.GetComponent<RectTransform>().sizeDelta = new Vector2(22, 22);

            _connectSettings = gameObject.AddComponent<ConnectSettingsPanel>();
            _connectSettings.Build(_connectPanel.transform);

            _streamingPanel = Mk.Panel(bg.transform, "Streaming", Color.clear);
            Mk.Stretch(_streamingPanel, 20);

            // Combined single-line status + stats. Green half = "Streaming to X",
            // gray half = "Sent N · Q N · Drops N · N FPS · Upstream N Mbps". Rich-text
            // <color> tags handle the two-tone look inside one label; NoWrap keeps it
            // on one line even when slightly wider than the bg.
            _statusText = Mk.Label(_streamingPanel.transform, "", new Vector2(0, 38), 13, Color.white,
                TextAlignmentOptions.Center, 600);
            _statusText.textWrappingMode = TextWrappingModes.NoWrap;

            var textBoxBg = Mk.Panel(_streamingPanel.transform, "DictationBox",
                new Color(0.12f, 0.12f, 0.18f, 0.9f));
            var tbR = textBoxBg.GetComponent<RectTransform>();
            tbR.anchoredPosition = new Vector2(0, -15);
            tbR.sizeDelta = new Vector2(520, 70);
            textBoxBg.GetComponent<Image>().raycastTarget = false;

            _dictationText = Mk.Label(textBoxBg.transform, "",
                Vector2.zero, 16, new Color(0.92f, 0.92f, 0.95f),
                TextAlignmentOptions.TopLeft, 500);
            var dtR = _dictationText.GetComponent<RectTransform>();
            dtR.sizeDelta = new Vector2(500, 60);
            _dictationText.textWrappingMode = TextWrappingModes.Normal;

            // Settings sub-panel (translucence + similarity + min-match sliders).
            // Hidden by default; toggled by the gear button below.
            _settings = gameObject.AddComponent<SettingsPanel>();
            _settings.Build(
                _streamingPanel.transform,
                translucence: 0.18f,
                similarity:   0.95f,
                minMatch:     0.25f);
            _settings.OnTranslucenceChanged += a => _visualizer?.SetPointAlpha(a);
            _settings.OnSimilarityChanged   += s => _audio?.SetThresholds(s, _audio.MinMatchSimilarity);
            _settings.OnMinMatchChanged     += m => _audio?.SetThresholds(_audio.SimilarityThreshold, m);

            // Gear button: top-right of the streaming panel. Tap toggles the
            // settings sub-panel below. Uses an IconFactory.Gear sprite as a
            // child — TMP's bundled font has no "⚙" glyph, so a sprite is the
            // only option that renders reliably on Quest.
            _gearBtn = Mk.Btn(_streamingPanel.transform, "",
                new Vector2(245, 32), new Vector2(28, 28),
                new Color(0.25f, 0.27f, 0.35f), 0, ToggleSettings);
            var gearIcon = IconFactory.MakeIconChild(_gearBtn.transform, "GearIcon", IconFactory.Gear);
            gearIcon.GetComponent<RectTransform>().sizeDelta = new Vector2(22, 22);

            // Mic + Clear + Disconnect all live on a body-locked orb (see BuildMicOrb).
            _streamingPanel.SetActive(false);

            BuildMicOrb();

            // Hand the coach-marks system its anchor points now that every
            // panel/orb exists. CoachMarks builds its own canvases lazily here.
            if (_coach != null)
            {
                _coach.SetTargets(
                    connectPanel:   _connectPanel.transform,
                    connectBtn:     _connectBtn.GetComponent<RectTransform>(),
                    streamingPanel: _streamingPanel.transform,
                    micOrb:         _micOrb.transform);
                _coach.OnConnectPanelShown();
            }
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

            // Hold-to-talk: no onClick listener — the mic press is driven by
            // TryHandleMicHold() polling OVRInput each frame and matching against
            // the ray's current UI raycast hit. EventTrigger PointerDown/Up
            // would be cleaner, but XRUIInputModule doesn't dispatch them in
            // this project (no UI press input wired on the XRRayInteractor).
            _micBtn = MakeOrbButton(_micOrb.transform, Vector2.zero,
                MicIdleColor, IconFactory.Mic, null);
            _micBtnBg = _micBtn.GetComponent<Image>();
            _micIconGroup  = _micBtn.transform.Find("Icon").gameObject;
            _stopIconGroup = IconFactory.MakeIconChild(_micBtn.transform, "StopIcon", IconFactory.Stop);
            _stopIconGroup.SetActive(false);

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
            SnapSharedBodyForward();
            _orbVelocity = Vector3.zero;
            var p = cam.transform.position + _bodyForward * micOrbDistance;
            p.y = cam.transform.position.y + micOrbVerticalOffset;
            _micOrb.transform.position = p;
            _micOrb.transform.rotation = Quaternion.LookRotation(p - cam.transform.position);
        }

        void UpdateMicOrbPose()
        {
            var cam = Camera.main;
            if (cam == null) return;
            var target = cam.transform.position + _bodyForward * micOrbDistance;
            target.y = cam.transform.position.y + micOrbVerticalOffset;
            _micOrb.transform.position = Vector3.SmoothDamp(
                _micOrb.transform.position, target, ref _orbVelocity, bodyPosSmoothTime);
            _micOrb.transform.rotation = Quaternion.LookRotation(
                _micOrb.transform.position - cam.transform.position);
        }

        void ToggleTransport()
        {
            _orchestrator.Transport = _orchestrator.Transport switch
            {
                FramesTransport.Tcp   => FramesTransport.Grpc,
                FramesTransport.Grpc  => FramesTransport.Illixr,
                _                     => FramesTransport.Tcp,
            };
            if (_transportLabel != null)
                _transportLabel.text = _orchestrator.Transport.ToString();

            bool isIllixr = _orchestrator.Transport == FramesTransport.Illixr;
            _portString = DefaultPortFor(_orchestrator.Transport);
            if (_portLabel != null) _portLabel.text = _portString;

            if (_tcpGrpcConfigRows != null) _tcpGrpcConfigRows.SetActive(!isIllixr);
            if (_illixrConfigRows  != null) _illixrConfigRows.SetActive(isIllixr);

            Debug.Log($"[StreamingUI] Transport → {_orchestrator.Transport}");
        }

        static string DefaultPortFor(FramesTransport t) => t switch
        {
            FramesTransport.Grpc   => DefaultGrpcPort,
            FramesTransport.Illixr => "",
            _                      => DefaultTcpPort,
        };
        
        void OpenKB(string field)
        {
            _editField = field;
            string seed = field switch
            {
                "ip"        => _ipAddress,
                "port"      => _portString,
                "audioPort" => _audioPortString,
                "illixrServerPort" => _illixrServerPort,
                "illixrClientPort" => _illixrClientPort,
                _           => ""
            };
            _keyboard = TouchScreenKeyboard.Open(
                seed, TouchScreenKeyboardType.DecimalPad, false, false, false, false, "", 21);
        }
        void OnConnect()
        {
            _errorText.text = "";
            bool isIllixr  = _orchestrator.Transport == FramesTransport.Illixr;
            float maxDepthM    = _connectSettings != null ? _connectSettings.WireValue : 0f;
            bool depthDisabled = _connectSettings != null && !_connectSettings.DepthEnabled;

            if (isIllixr)
            {
                if (!int.TryParse(_illixrServerPort, out int sp) || sp < 1 || sp > 65535)
                { ShowError("Invalid server port"); return; }
                if (!int.TryParse(_illixrClientPort, out int cp) || cp < 1 || cp > 65535)
                { ShowError("Invalid client port"); return; }
                _connectBtn.interactable = false;
                _errorText.text = "Connecting...";
                // Server IP reuses _ipAddress from the existing dropdown.
                // Client IP is auto-detected inside ConnectIllixr().
                _orchestrator.ConnectIllixr(_ipAddress, sp, cp, _selectedFps, maxDepthM, depthDisabled);
            }
            else
            {
                if (string.IsNullOrEmpty(_ipAddress)) { ShowError("Enter IP"); return; }
                if (!int.TryParse(_portString, out int port) || port < 1 || port > 65535)
                { ShowError("Invalid port"); return; }
                _connectBtn.interactable = false;
                _errorText.text = "Connecting...";
                _orchestrator.Connect(_ipAddress, port, _selectedFps, maxDepthM, depthDisabled);
            }        }
        void ShowConnect()
        {
            ResetDictation();
            _canvasRect.sizeDelta = new Vector2(650, 420);
            _connectBtn.interactable = true;
            _errorText.text = "";
            _connectPanel.SetActive(true);
            _streamingPanel.SetActive(false);
            if (_micOrb != null) _micOrb.SetActive(false);
            _settings?.Hide();
            Position();

            // Tutorial defaults back to ON every time the Connect panel returns,
            // so the next user (after a Disconnect) automatically gets the tour.
            // Repeat users uncheck Skip during their own session.
            if (_coach != null)
            {
                _coach.Enabled = true;
                if (_skipTutorialLabel != null) _skipTutorialLabel.text = "Skip tutorial";
                _coach.OnConnectPanelShown();
            }
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
            _canvasRect.sizeDelta = new Vector2(570, 110);
            System.Array.Clear(_bwSnapshots, 0, _bwSnapshots.Length);
            _bwIdx = 0;
            _bwNextSampleTime = Time.unscaledTime + 1f;
            _bwRateMbps = 0f;
            // Tell the audio client which server to send to — same IP as the
            // frames stream, separate user-configurable port for the
            // vis_proto VisualizerServer — and which transport to route over.
            // Always configure transport, even in ILLIXR mode where the audio
            // port is blank (ILLIXR ignores address/port and uses the native
            // bridge), so the voice query doesn't fall back to gRPC.
            if (_audio != null)
            {
                int.TryParse(_audioPortString, out int audioPort);
                _audio.Configure(_ipAddress, audioPort, _orchestrator.Transport);
            }
            InitStreamingPanelPose();
            if (_micOrb != null) { _micOrb.SetActive(true); InitMicOrbPose(); }
            _connectSettings?.Hide();
            _coach?.OnStreamingShown();
        }
        void ShowError(string msg) { _errorText.text = msg; _connectBtn.interactable = true; }

        void ToggleSettings()
        {
            if (_settings == null) return;
            if (_settings.IsVisible) _settings.Hide();
            else _settings.Show();
        }

        void ToggleConnectSettings()
        {
            if (_connectSettings == null) return;
            if (_connectSettings.IsVisible) _connectSettings.Hide();
            else _connectSettings.Show();
        }

        void ToggleSkipTutorial()
        {
            if (_coach == null) return;
            _coach.Enabled = !_coach.Enabled;
            if (_skipTutorialLabel != null)
                _skipTutorialLabel.text = _coach.Enabled ? "Skip tutorial" : "Show tutorial";
            // Re-show or hide S1 immediately so the toggle's effect is visible.
            if (_connectPanel != null && _connectPanel.activeSelf)
            {
                if (_coach.Enabled) _coach.OnConnectPanelShown();
                else _coach.HideAll();
            }
            Debug.Log($"[StreamingUI] Tutorial {(_coach.Enabled ? "ON" : "OFF")}");
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
            _visualizer.Clear();
            var (objects, points) = _visualizer.AddResponse(response);
            string query = string.IsNullOrEmpty(response.TextQuery) ? "(none)" : response.TextQuery;
            string msg = $"Query: {query}\nReceived: {objects} objects, {points} points ({response.ServerQueryProcessing:F0} ms)";
            Debug.Log($"[StreamingUI] {msg}");
            if (_dictationText != null) _dictationText.text = msg;
        }

        void OnIllixrQueryResponseReceived(QueryResponseData data)
        {
            if (data == null) return;

            var response = new XrVis.allPointClouds
            {
                TextQuery             = data.TextQuery ?? "",
                ServerQueryProcessing = data.ServerLatency * 1000f,  // seconds → ms
                NumPointClouds        = data.NumClouds,
            };

            for (int i = 0; i < data.NumClouds; i++)
            {
                var pc = new XrVis.PointCloud
                {
                    NumPoints = 1,
                };

                if (data.Centroids != null && data.Centroids.Length >= (i + 1) * 3)
                {
                    pc.Centroid.Add(data.Centroids[i * 3]);
                    pc.Centroid.Add(data.Centroids[i * 3 + 1]);
                    pc.Centroid.Add(data.Centroids[i * 3 + 2]);

                    pc.Points.Add(data.Centroids[i * 3]);
                    pc.Points.Add(data.Centroids[i * 3 + 1]);
                    pc.Points.Add(data.Centroids[i * 3 + 2]);
                }

                response.PointClouds.Add(pc);
            }

            if (data.Colors != null)
                foreach (var c in data.Colors)
                    response.Colors.Add(c);

            OnPointCloudsReceived(response);
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
            if (listening) _coach?.OnListeningStarted();
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

            TryHandleRecallButton();
            TryHandleMicHold();
            _settings?.Tick();
            _connectSettings?.Tick();

            // Any trigger press dismisses the S3 popup. Fires before the
            // XRInteractionSetup click is dispatched so a tap aimed at the
            // mic orb both closes the popup AND starts recording the next take.
            if (_coach != null &&
                (OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch) ||
                 OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch)))
            {
                _coach.OnAnyTriggerPress();
            }

            // Step the shared body-forward once per frame, then let each body-locked
            // widget damp its own position using the new value. Keeps panel + orb
            // yaw-in-sync regardless of which ones happen to be visible.
            bool panelActive = _streamingPanel != null && _streamingPanel.activeSelf;
            bool orbActive   = _micOrb != null && _micOrb.activeSelf;
            if (panelActive || orbActive) UpdateSharedBodyForward();
            if (panelActive) UpdateStreamingPanelPose();
            if (orbActive)   UpdateMicOrbPose();

            if (_keyboard != null)
            {
                if (_keyboard.status == TouchScreenKeyboard.Status.Visible ||
                    _keyboard.status == TouchScreenKeyboard.Status.Done)
                {
                    switch (_editField)
                    {
                        case "ip":
                            _ipAddress = _keyboard.text;
                            _ipDropdown.label.text = "Custom";
                            if (_ipDropdown.subLabel != null) _ipDropdown.subLabel.text = _ipAddress;
                            break;
                        case "audioPort":
                            _audioPortString = _keyboard.text;
                            _audioPortLabel.text = _audioPortString;
                            break;
                        case "illixrServerPort":
                            _illixrServerPort = _keyboard.text;
                            if (_illixrServerPortLabel != null) _illixrServerPortLabel.text = _illixrServerPort;
                            break;
                        case "illixrClientPort":
                            _illixrClientPort = _keyboard.text;
                            if (_illixrClientPortLabel != null) _illixrClientPortLabel.text = _illixrClientPort;
                            break;
                        default:
                            _portString = _keyboard.text;
                            _portLabel.text = _portString;
                            break;
                    }
                }
                if (_keyboard.status != TouchScreenKeyboard.Status.Visible)
                    _keyboard = null;
            }

            if (_streamingPanel.activeSelf)
            {
                var err = _orchestrator.LastError;
                if (!string.IsNullOrEmpty(err)) { _orchestrator.Disconnect(); ShowConnect(); ShowError(err); return; }

                int totalDropped = _orchestrator.TotalDropped;
                string statsHex = totalDropped > 0 ? "#FFB24C" : "#8C8C99";
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

                string name = CurrentServerName();
                _statusText.text = _orchestrator.IsConnected
                    ? $"<color=#4DE666>Streaming to {name}</color>   <color={statsHex}>Sent {_orchestrator.FrameCount} · Q {_orchestrator.QueuedFrames} · Drops {totalDropped} · {fpsStr} FPS · Upstream {_bwRateMbps:F2} Mbps</color>"
                    : $"<color=#4DE666>Connecting to {name}...</color>";
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
        static Sprite _mic, _stop, _circle, _trash, _power, _triDown, _arrow, _gear;

        public static Sprite Mic     => _mic     ??= BuildMic();
        public static Sprite Stop    => _stop    ??= BuildStop();
        public static Sprite Circle  => _circle  ??= BuildCircle();
        public static Sprite Trash   => _trash   ??= BuildTrash();
        public static Sprite Power   => _power   ??= BuildPower();
        public static Sprite TriDown => _triDown ??= BuildTriDown();
        public static Sprite Arrow   => _arrow   ??= BuildArrow();
        public static Sprite Gear    => _gear    ??= BuildGear();

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

        static Sprite BuildGear()
        {
            var px = ClearBuffer();
            FillGear(px, cx: 64f, cy: 64f,
                rOuter: 56f,    // tooth tips
                rInner: 44f,    // base of teeth (between teeth)
                rHole:  20f,    // center hole
                teeth:  8,
                color: Color.white);
            return MakeSprite(px);
        }

        // Eight-tooth gear with anti-aliased outer + hole edges.
        // Tooth side edges are hard (one-pixel jaggy on the 128² buffer); the
        // sprite is downscaled to ~28 px in the UI so bilinear filtering hides
        // those edges.
        static void FillGear(Color32[] px, float cx, float cy,
                             float rOuter, float rInner, float rHole,
                             int teeth, Color color)
        {
            int xmin = Mathf.Max(0, Mathf.FloorToInt(cx - rOuter - 1));
            int xmax = Mathf.Min(Size - 1, Mathf.CeilToInt(cx + rOuter + 1));
            int ymin = Mathf.Max(0, Mathf.FloorToInt(cy - rOuter - 1));
            int ymax = Mathf.Min(Size - 1, Mathf.CeilToInt(cy + rOuter + 1));
            float twoPi = Mathf.PI * 2f;

            for (int y = ymin; y <= ymax; y++)
            {
                for (int x = xmin; x <= xmax; x++)
                {
                    float pxF = x + 0.5f, pyF = y + 0.5f;
                    float dx = pxF - cx, dy = pyF - cy;
                    float r = Mathf.Sqrt(dx * dx + dy * dy);
                    if (r > rOuter + 1f) continue;

                    // Tooth/gap modulation: phase 0..0.5 = tooth (rOuter), 0.5..1 = gap (rInner).
                    float angle = Mathf.Atan2(dy, dx);
                    if (angle < 0) angle += twoPi;
                    float toothPhase = (angle * teeth / twoPi) % 1f;
                    float currentOuter = toothPhase < 0.5f ? rOuter : rInner;

                    // AA at outer edge of current radius, AA at hole edge.
                    float aOuter = Mathf.Clamp01(0.5f + currentOuter - r);
                    float aHole  = Mathf.Clamp01(0.5f + r - rHole);
                    float a = aOuter * aHole;
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

        static Sprite BuildArrow()
        {
            var px = ClearBuffer();
            // Right-pointing rounded isoceles triangle on 128x128.
            // Elongated horizontally (100 wide × 56 tall → 1.8:1) so the tip
            // direction reads unambiguously from any rotation. Corners rounded
            // by 8 px for a soft modern look.
            //   Apex: (114, 64)
            //   Base: (14, 36) bottom — (14, 92) top
            FillRoundedTriangle(px,
                ax: 14f,  ay: 36f,
                bx: 114f, by: 64f,
                cx: 14f,  cy: 92f,
                radius: 8f, color: Color.white);
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

        // Rounded filled triangle with vertices (ax,ay), (bx,by), (cx,cy) and
        // corner radius `radius` (all in pixel units on the 128x128 buffer).
        // Produces the Minkowski sum of the core triangle with a disc of the
        // given radius — i.e. corners and edges are rounded uniformly.
        //
        // Uses a signed-distance-function (inigo quilez's sdTriangle): for each
        // pixel, compute signed distance to the triangle boundary (negative
        // inside), then anti-alias a 1-pixel band around `sdf = radius`.
        static void FillRoundedTriangle(Color32[] px,
                                        float ax, float ay,
                                        float bx, float by,
                                        float cx, float cy,
                                        float radius, Color color)
        {
            float e0x = bx - ax, e0y = by - ay;
            float e1x = cx - bx, e1y = cy - by;
            float e2x = ax - cx, e2y = ay - cy;
            float e0e0 = e0x * e0x + e0y * e0y;
            float e1e1 = e1x * e1x + e1y * e1y;
            float e2e2 = e2x * e2x + e2y * e2y;
            float s    = Mathf.Sign(e0x * e2y - e0y * e2x);    // winding direction

            float xminF = Mathf.Min(Mathf.Min(ax, bx), cx) - radius - 1f;
            float xmaxF = Mathf.Max(Mathf.Max(ax, bx), cx) + radius + 1f;
            float yminF = Mathf.Min(Mathf.Min(ay, by), cy) - radius - 1f;
            float ymaxF = Mathf.Max(Mathf.Max(ay, by), cy) + radius + 1f;
            int xmin = Mathf.Max(0, Mathf.FloorToInt(xminF));
            int xmax = Mathf.Min(Size - 1, Mathf.CeilToInt(xmaxF));
            int ymin = Mathf.Max(0, Mathf.FloorToInt(yminF));
            int ymax = Mathf.Min(Size - 1, Mathf.CeilToInt(ymaxF));

            for (int y = ymin; y <= ymax; y++)
            {
                float pyF = y + 0.5f;
                for (int x = xmin; x <= xmax; x++)
                {
                    float pxF = x + 0.5f;
                    float v0x = pxF - ax, v0y = pyF - ay;
                    float v1x = pxF - bx, v1y = pyF - by;
                    float v2x = pxF - cx, v2y = pyF - cy;

                    float t0 = Mathf.Clamp01((v0x * e0x + v0y * e0y) / e0e0);
                    float t1 = Mathf.Clamp01((v1x * e1x + v1y * e1y) / e1e1);
                    float t2 = Mathf.Clamp01((v2x * e2x + v2y * e2y) / e2e2);

                    float pq0x = v0x - e0x * t0, pq0y = v0y - e0y * t0;
                    float pq1x = v1x - e1x * t1, pq1y = v1y - e1y * t1;
                    float pq2x = v2x - e2x * t2, pq2y = v2y - e2y * t2;

                    float d0 = pq0x * pq0x + pq0y * pq0y;
                    float d1 = pq1x * pq1x + pq1y * pq1y;
                    float d2 = pq2x * pq2x + pq2y * pq2y;
                    float minD2 = Mathf.Min(Mathf.Min(d0, d1), d2);

                    float w0 = s * (v0x * e0y - v0y * e0x);
                    float w1 = s * (v1x * e1y - v1y * e1x);
                    float w2 = s * (v2x * e2y - v2y * e2x);
                    float minW = Mathf.Min(Mathf.Min(w0, w1), w2);

                    float sdf = -Mathf.Sqrt(minD2) * Mathf.Sign(minW);
                    float a   = Mathf.Clamp01(0.5f + radius - sdf);
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
