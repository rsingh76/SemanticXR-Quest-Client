using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using TMPro;

namespace SemanticXR.UI
{
    /// <summary>
    /// Connect-time settings sub-panel — opened by a gear on the Connect panel.
    /// Currently holds a single setting: the per-session far-depth cap stamped
    /// on every upstream frame (read by the server on the first frame).
    ///
    /// Three semantic states (proto wire convention):
    ///   • Default — value 0.0  → server uses YAML default
    ///   • No cap  — value -1.0 → far-depth filter disabled
    ///   • Cap     — positive cap in meters
    ///
    /// UI is the same drag+button slider style as SettingsPanel: drag latches
    /// on track press and uses ray-plane projection during the held phase, so
    /// vertical drift mid-drag doesn't break it. ± buttons step ±0.5 m.
    /// </summary>
    public class ConnectSettingsPanel : MonoBehaviour
    {
        public enum Mode { Default, NoCap, Cap }

        const float TrackWidthPx  = 240f;
        const float TrackHeightPx = 24f;
        const float CapMin = 0.5f;
        const float CapMax = 8.0f;
        const float CapStep = 0.5f;

        Mode _mode = Mode.Default;
        float _capValue = 4.0f;

        GameObject _root;
        Button _modeDefaultBtn, _modeNoCapBtn, _modeCapBtn;
        RectTransform _track, _thumb;
        TextMeshProUGUI _valueText;
        TextMeshProUGUI _summaryText;

        bool _dragging;

        public Mode CurrentMode => _mode;
        public float CurrentCap => _capValue;

        // Wire value the orchestrator should send. Encodes the three modes per
        // the proto comment.
        public float WireValue => _mode switch
        {
            Mode.Default => 0f,
            Mode.NoCap   => -1f,
            Mode.Cap     => _capValue,
            _            => 0f,
        };

        public void Build(Transform parent)
        {
            _root = Mk.Panel(parent, "ConnectSettings", new Color(0.07f, 0.07f, 0.11f, 0.95f));
            var r = _root.GetComponent<RectTransform>();
            // Connect panel is 650 x 420. Drop the sub-panel below the panel's
            // bottom edge (y = -210) with a small gap. Sub-panel is 600 x 180.
            r.anchoredPosition = new Vector2(0, -310);
            r.sizeDelta = new Vector2(600, 180);

            // Top row: header + three mode buttons.
            // Center at x=-130 with width 300 → rect spans [-280, +20], so the
            // text's left edge sits 20 px inside the sub-panel's left edge (-300).
            Mk.Label(_root.transform, "Mapping depth cap (read once at connect):",
                     new Vector2(-130, 60), 14, new Color(0.85f, 0.9f, 1f),
                     TextAlignmentOptions.MidlineLeft, 300);

            _modeDefaultBtn = Mk.Btn(_root.transform, "Default", new Vector2(-180, 25), new Vector2(110, 32),
                                     new Color(0.3f, 0.3f, 0.4f), 14, () => SetMode(Mode.Default));
            _modeNoCapBtn   = Mk.Btn(_root.transform, "No cap",  new Vector2( -50, 25), new Vector2(110, 32),
                                     new Color(0.3f, 0.3f, 0.4f), 14, () => SetMode(Mode.NoCap));
            _modeCapBtn     = Mk.Btn(_root.transform, "Cap",     new Vector2(  80, 25), new Vector2(110, 32),
                                     new Color(0.3f, 0.3f, 0.4f), 14, () => SetMode(Mode.Cap));

            // Cap-value slider: drag track + thumb, − value +.
            var trackGo = new GameObject("Track");
            trackGo.transform.SetParent(_root.transform, false);
            _track = trackGo.AddComponent<RectTransform>();
            _track.anchoredPosition = new Vector2(-60, -25);
            _track.sizeDelta = new Vector2(TrackWidthPx, TrackHeightPx);
            trackGo.AddComponent<Image>().color = new Color(0.18f, 0.20f, 0.28f);

            var thumbGo = new GameObject("Thumb");
            thumbGo.transform.SetParent(_track, false);
            _thumb = thumbGo.AddComponent<RectTransform>();
            _thumb.anchorMin = _thumb.anchorMax = new Vector2(0.5f, 0.5f);
            _thumb.sizeDelta = new Vector2(22, 30);
            var thumbImg = thumbGo.AddComponent<Image>();
            thumbImg.color = new Color(0.95f, 0.85f, 0.35f);
            thumbImg.raycastTarget = false;

            Mk.Btn(_root.transform, "−", new Vector2(90, -25), new Vector2(28, 26),
                   new Color(0.3f, 0.3f, 0.4f), 18, () => StepCap(-CapStep));
            _valueText = Mk.Label(_root.transform, "", new Vector2(135, -25), 16, Color.white,
                                  TextAlignmentOptions.Center, 50);
            Mk.Btn(_root.transform, "+", new Vector2(180, -25), new Vector2(28, 26),
                   new Color(0.3f, 0.3f, 0.4f), 18, () => StepCap(+CapStep));

            // Summary line — shows what will be sent.
            _summaryText = Mk.Label(_root.transform, "", new Vector2(0, -65), 12,
                                    new Color(0.65f, 0.7f, 0.8f),
                                    TextAlignmentOptions.Center, 560);

            UpdateAllVisuals();
            _root.SetActive(false);
        }

        public void Show() { if (_root != null) _root.SetActive(true); }
        public void Hide() { if (_root != null) _root.SetActive(false); _dragging = false; }
        public bool IsVisible => _root != null && _root.activeSelf;

        // ---------- input ----------

        public void Tick()
        {
            if (_root == null || !_root.activeSelf) return;
            // Drag is only meaningful in Cap mode — ignore otherwise so a stray
            // press on the (visually present but inert) slider doesn't move it.
            if (_mode != Mode.Cap) { _dragging = false; return; }

            bool triggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                            || OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            bool triggerUp   = OVRInput.GetUp  (OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                            || OVRInput.GetUp  (OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            bool triggerHeld = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                            || OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);

            if (triggerDown && IsRayOn(_track))
            {
                _dragging = true;
                Debug.Log("[ConnectSettingsPanel] cap drag START");
            }

            if (_dragging && triggerHeld && TryGetRayXOnTrackPlane(_track, out float xLocal))
            {
                float frac = Mathf.Clamp01((xLocal + TrackWidthPx / 2f) / TrackWidthPx);
                float v = CapMin + frac * (CapMax - CapMin);
                if (Mathf.Abs(v - _capValue) > 1e-4f)
                {
                    _capValue = Mathf.Clamp(v, CapMin, CapMax);
                    UpdateAllVisuals();
                    Debug.Log($"[ConnectSettingsPanel] cap (drag) → {_capValue:F2} m");
                }
            }

            if (triggerUp && _dragging)
            {
                _dragging = false;
                Debug.Log("[ConnectSettingsPanel] cap drag END");
            }
        }

        // ---------- state changes ----------

        void SetMode(Mode m)
        {
            _mode = m;
            UpdateAllVisuals();
            Debug.Log($"[ConnectSettingsPanel] mode → {m}");
        }

        void StepCap(float delta)
        {
            // Stepping the cap also implies the user wants to BE in Cap mode —
            // promote them. Without this, ± would do nothing in Default/NoCap
            // mode and look broken.
            if (_mode != Mode.Cap) _mode = Mode.Cap;

            float v = _capValue + delta;
            v = Mathf.Round(v / CapStep) * CapStep;
            v = Mathf.Clamp(v, CapMin, CapMax);
            if (Mathf.Approximately(v, _capValue))
            {
                UpdateAllVisuals();   // mode may have flipped even if value didn't
                return;
            }
            _capValue = v;
            UpdateAllVisuals();
            Debug.Log($"[ConnectSettingsPanel] cap (step) → {_capValue:F2} m");
        }

        void UpdateAllVisuals()
        {
            // Highlight the active mode button (brighter color); de-tone others.
            var active = new Color(0.55f, 0.45f, 0.15f);
            var inactive = new Color(0.3f, 0.3f, 0.4f);
            SetButtonBg(_modeDefaultBtn, _mode == Mode.Default ? active : inactive);
            SetButtonBg(_modeNoCapBtn,   _mode == Mode.NoCap   ? active : inactive);
            SetButtonBg(_modeCapBtn,     _mode == Mode.Cap     ? active : inactive);

            // Slider visuals always reflect the cap value, but visually dim if
            // mode != Cap so it's clear the value isn't what's being sent.
            float frac = (_capValue - CapMin) / Mathf.Max(1e-6f, CapMax - CapMin);
            _thumb.anchoredPosition = new Vector2(-TrackWidthPx / 2f + frac * TrackWidthPx, 0);
            _valueText.text = $"{_capValue:F1} m";

            float dim = _mode == Mode.Cap ? 1f : 0.45f;
            var thumbImg = _thumb.GetComponent<Image>();
            if (thumbImg != null) thumbImg.color = new Color(0.95f, 0.85f, 0.35f, dim);
            _valueText.color = new Color(1f, 1f, 1f, dim);

            _summaryText.text = _mode switch
            {
                Mode.Default => "Will send 0.0 — server uses YAML default.",
                Mode.NoCap   => "Will send -1.0 — far-depth filter disabled.",
                Mode.Cap     => $"Will send {_capValue:F1} m — cap at this distance.",
                _            => "",
            };
        }

        static void SetButtonBg(Button b, Color c)
        {
            if (b == null) return;
            var img = b.GetComponent<Image>();
            if (img != null) img.color = c;
            var cs = b.colors;
            cs.highlightedColor = c * 1.3f;
            cs.pressedColor     = c * 0.7f;
            b.colors = cs;
        }

        // ---------- ray helpers (mirror SettingsPanel) ----------

        bool IsRayOn(RectTransform target)
        {
            foreach (var ray in FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None))
            {
                if (!ray.TryGetCurrentUIRaycastResult(out var result) || result.gameObject == null)
                    continue;
                for (var t = result.gameObject.transform; t != null; t = t.parent)
                    if (t == target) return true;
            }
            return false;
        }

        bool TryGetRayXOnTrackPlane(RectTransform track, out float xLocal)
        {
            xLocal = 0f;
            foreach (var ray in FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None))
            {
                Vector3 origin = ray.transform.position;
                Vector3 dir    = ray.transform.forward;
                Vector3 pPoint = track.position;
                Vector3 pNorm  = track.forward;
                float denom = Vector3.Dot(dir, pNorm);
                if (Mathf.Abs(denom) < 1e-6f) continue;
                float t = Vector3.Dot(pPoint - origin, pNorm) / denom;
                if (t < 0f) continue;
                Vector3 worldHit = origin + dir * t;
                Vector3 localHit = track.InverseTransformPoint(worldHit);
                xLocal = localHit.x;
                return true;
            }
            return false;
        }
    }
}
