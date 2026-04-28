using System.Collections;
using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SemanticXR.UI
{
    /// <summary>
    /// Progressive, event-driven onboarding hints. Three states only:
    ///
    ///   S1 · Connect       — pulsing arrow + caption above the Connect button.
    ///   S2 · Hold to talk  — pulsing arrow + caption above the mic orb teaching
    ///                        hold-trigger-to-record.
    ///   S3 · Color popup   — one-time popup next to the streaming panel
    ///                        explaining the red→green confidence gradient.
    ///
    /// Each state self-dismisses when the user completes the gesture it teaches
    /// (Connect clicked, mic listening started, popup acknowledged or 8 s pass).
    /// `Enabled = false` suppresses S1/S2/S3 entirely; the HUD legend is owned
    /// by ConfidenceLegendHud and is intentionally NOT gated by this flag.
    ///
    /// State resets to S1 every time the Connect panel returns (i.e., on app
    /// start AND on Disconnect), so the next user automatically gets the tour.
    /// </summary>
    public class CoachMarks : MonoBehaviour
    {
        public bool Enabled = true;

        // Targets — all set by StreamingUI via SetTargets() once they exist.
        Transform _connectPanelXf;     // RectTransform of the Connect panel
        RectTransform _connectBtnRect; // for S1 anchor
        Transform _streamingPanelXf;   // for S3 anchor
        Transform _micOrbXf;           // for S2 anchor (world-space)

        GameObject _s1Group, _s2Group, _s3Group;
        RectTransform _s1Rect, _s3Rect;
        Image _s1Arrow, _s2Arrow;
        GameObject _s2Canvas;
        TextMeshProUGUI _s1Text, _s2Text;

        Coroutine _s1Pulse, _s2Pulse, _s3Timeout;

        public void SetTargets(Transform connectPanel, RectTransform connectBtn,
                               Transform streamingPanel, Transform micOrb)
        {
            _connectPanelXf  = connectPanel;
            _connectBtnRect  = connectBtn;
            _streamingPanelXf = streamingPanel;
            _micOrbXf        = micOrb;
            BuildAll();
        }

        // ---------- public lifecycle ----------

        // Called by StreamingUI when the Connect panel becomes visible (app
        // start AND Disconnect). Resets all state and shows S1 if enabled.
        public void OnConnectPanelShown()
        {
            HideAll();
            if (!Enabled) return;
            ShowS1();
        }

        // Called by StreamingUI when the streaming panel becomes visible.
        public void OnStreamingShown()
        {
            HideAll();
            if (!Enabled) return;
            ShowS2();
        }

        // Called when AudioStreamController begins recording. Dismisses S2
        // (the user clearly figured out the hold gesture).
        public void OnListeningStarted()
        {
            HideS2();
        }

        // Called by PointCloudVisualizer.OnFirstClusterRendered.
        public void OnFirstCluster()
        {
            if (!Enabled) return;
            ShowS3();
        }

        // Forwarded from StreamingUI's trigger handler so a tap dismisses the S3
        // popup. We don't attach a button to the popup itself because clicking
        // a head-locked widget while gestures are still landing would feel
        // arbitrary; ANY trigger press dismisses, which matches user intent.
        public void OnAnyTriggerPress()
        {
            if (_s3Group != null && _s3Group.activeSelf) HideS3();
        }

        // ---------- build ----------

        void BuildAll()
        {
            BuildS1();
            BuildS2();
            BuildS3();
            HideAll();
        }

        // S1: leftward arrow + short caption placed to the right of the Connect
        // button. Connect button spans x=[-110, +110] at y=-90, so we sit at
        // x∈[130, 270], y=-90 — to the right of Connect, well clear of the FPS
        // row (y=-35) above and the error line (y=-140) below.
        void BuildS1()
        {
            if (_connectPanelXf == null || _connectBtnRect == null) return;

            _s1Group = new GameObject("Coach_S1");
            _s1Group.transform.SetParent(_connectPanelXf, false);
            _s1Rect = _s1Group.AddComponent<RectTransform>();
            _s1Rect.anchoredPosition = new Vector2(200, -90);
            _s1Rect.sizeDelta = new Vector2(140, 40);

            _s1Text = MakeCaption(_s1Group.transform, "Tap Connect", new Vector2(15, 0), 14);
            var tr = _s1Text.GetComponent<RectTransform>();
            tr.sizeDelta = new Vector2(110, 30);

            var arrowGo = new GameObject("Arrow");
            arrowGo.transform.SetParent(_s1Group.transform, false);
            var ar = arrowGo.AddComponent<RectTransform>();
            ar.anchoredPosition = new Vector2(-55, 0);
            ar.sizeDelta = new Vector2(26, 26);
            // IconFactory.Arrow points +X; rotate 180° so it points -X (toward
            // the Connect button on its left).
            ar.localRotation = Quaternion.Euler(0, 0, 180f);
            _s1Arrow = arrowGo.AddComponent<Image>();
            _s1Arrow.sprite = IconFactory.Arrow;
            _s1Arrow.color  = new Color(1f, 0.85f, 0.2f);
            _s1Arrow.raycastTarget = false;
        }

        // S2: world-space billboard above the mic orb. Lives on its own canvas
        // because the orb is a separate canvas in world space.
        void BuildS2()
        {
            if (_micOrbXf == null) return;

            _s2Canvas = new GameObject("Coach_S2_Canvas");
            _s2Canvas.transform.SetParent(_micOrbXf, worldPositionStays: false);
            // Sit ~70 px above the mic orb (orb canvas is 340x100 at 0.001 scale).
            _s2Canvas.transform.localPosition = new Vector3(0, 70, 0);

            var c = _s2Canvas.AddComponent<Canvas>();
            c.renderMode = RenderMode.WorldSpace;
            _s2Canvas.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;

            var cr = c.GetComponent<RectTransform>();
            cr.sizeDelta = new Vector2(420, 80);

            _s2Group = _s2Canvas;

            _s2Text = MakeCaption(_s2Canvas.transform, "Press and Hold trigger on mic to speak. Release to send.",
                                   new Vector2(0, 14), 18);

            var arrowGo = new GameObject("Arrow");
            arrowGo.transform.SetParent(_s2Canvas.transform, false);
            var ar = arrowGo.AddComponent<RectTransform>();
            ar.anchorMin = ar.anchorMax = new Vector2(0.5f, 0.5f);
            ar.anchoredPosition = new Vector2(0, -22);
            ar.sizeDelta = new Vector2(34, 34);
            ar.localRotation = Quaternion.Euler(0, 0, -90f);
            _s2Arrow = arrowGo.AddComponent<Image>();
            _s2Arrow.sprite = IconFactory.Arrow;
            _s2Arrow.color  = new Color(1f, 0.85f, 0.2f);
            _s2Arrow.raycastTarget = false;
        }

        // S3: small popup card off the side of the streaming panel showing the
        // gradient with "low" and "high" labels and the one-line caption.
        void BuildS3()
        {
            if (_streamingPanelXf == null) return;

            _s3Group = new GameObject("Coach_S3");
            _s3Group.transform.SetParent(_streamingPanelXf, false);
            _s3Rect = _s3Group.AddComponent<RectTransform>();
            // Position below the dictation box so we don't fight for space.
            _s3Rect.anchoredPosition = new Vector2(0, -90);
            _s3Rect.sizeDelta = new Vector2(520, 90);

            var bg = _s3Group.AddComponent<Image>();
            bg.color = new Color(0.08f, 0.08f, 0.12f, 0.95f);
            bg.raycastTarget = false;

            // Caption — top of card.
            MakeCaption(_s3Group.transform, "Greener = the system is more confident.",
                        new Vector2(0, 22), 16);

            // Gradient strip in the middle.
            var stops = ConfidencePalette.Stops;
            const float stripW = 360f, stripH = 16f;
            float cellW = stripW / stops.Length;
            float xStart = -stripW / 2f + cellW / 2f;
            for (int i = 0; i < stops.Length; i++)
            {
                var cellGo = new GameObject($"Cell{i}");
                cellGo.transform.SetParent(_s3Group.transform, false);
                var r = cellGo.AddComponent<RectTransform>();
                r.anchorMin = r.anchorMax = new Vector2(0.5f, 0.5f);
                r.anchoredPosition = new Vector2(xStart + i * cellW, -8);
                r.sizeDelta = new Vector2(cellW, stripH);
                var img = cellGo.AddComponent<Image>();
                img.color = stops[i];
                img.raycastTarget = false;
            }

            // "low" / "high" labels under each end.
            var lo = MakeCaption(_s3Group.transform, "low",  new Vector2(-stripW / 2f, -28), 12);
            lo.color = new Color(0.75f, 0.55f, 0.55f);
            var hi = MakeCaption(_s3Group.transform, "high", new Vector2( stripW / 2f, -28), 12);
            hi.color = new Color(0.55f, 0.85f, 0.6f);
        }

        TextMeshProUGUI MakeCaption(Transform parent, string text, Vector2 pos, float size)
        {
            var go = new GameObject("Caption");
            go.transform.SetParent(parent, false);
            var r = go.AddComponent<RectTransform>();
            r.anchoredPosition = pos;
            r.sizeDelta = new Vector2(420, 30);
            var t = go.AddComponent<TextMeshProUGUI>();
            t.text = text;
            t.fontSize = size;
            t.alignment = TextAlignmentOptions.Center;
            t.color = Color.white;
            t.raycastTarget = false;
            return t;
        }

        // ---------- show / hide ----------

        void ShowS1()
        {
            if (_s1Group == null) return;
            _s1Group.SetActive(true);
            if (_s1Pulse != null) StopCoroutine(_s1Pulse);
            _s1Pulse = StartCoroutine(Pulse(_s1Arrow));
        }

        void HideS1()
        {
            if (_s1Pulse != null) { StopCoroutine(_s1Pulse); _s1Pulse = null; }
            if (_s1Group != null) _s1Group.SetActive(false);
        }

        void ShowS2()
        {
            if (_s2Group == null) return;
            _s2Group.SetActive(true);
            if (_s2Pulse != null) StopCoroutine(_s2Pulse);
            _s2Pulse = StartCoroutine(Pulse(_s2Arrow));
        }

        void HideS2()
        {
            if (_s2Pulse != null) { StopCoroutine(_s2Pulse); _s2Pulse = null; }
            if (_s2Group != null) _s2Group.SetActive(false);
        }

        void ShowS3()
        {
            if (_s3Group == null) return;
            _s3Group.SetActive(true);
            if (_s3Timeout != null) StopCoroutine(_s3Timeout);
            _s3Timeout = StartCoroutine(AutoHideS3(8f));
        }

        void HideS3()
        {
            if (_s3Timeout != null) { StopCoroutine(_s3Timeout); _s3Timeout = null; }
            if (_s3Group != null) _s3Group.SetActive(false);
        }

        IEnumerator AutoHideS3(float seconds)
        {
            yield return new WaitForSeconds(seconds);
            HideS3();
        }

        public void HideAll()
        {
            HideS1();
            HideS2();
            HideS3();
        }

        IEnumerator Pulse(Image img)
        {
            if (img == null) yield break;
            var baseColor = img.color;
            while (true)
            {
                float t = (Mathf.Sin(Time.unscaledTime * 3.5f) + 1f) * 0.5f;     // 0..1
                var c = baseColor;
                c.a = Mathf.Lerp(0.45f, 1f, t);
                img.color = c;
                yield return null;
            }
        }
    }
}