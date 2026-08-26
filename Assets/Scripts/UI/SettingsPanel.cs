using System;
using UnityEngine;
using UnityEngine.UI;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using TMPro;

namespace SemanticXR.UI
{
    /// <summary>
    /// In-VR settings sub-panel. Three identical drag+step sliders.
    ///
    /// Each row: a draggable track with a thumb, plus − / + step buttons and a
    /// value readout. Per-row min/max/step encode the slider's tuning range.
    ///
    /// Drag latching: trigger-down on the track latches the row as dragging.
    /// While latched, the value is computed by intersecting the controller ray
    /// with the track's plane — NOT by requiring the UI raycast to keep hitting
    /// the rect. Vertical drift is therefore irrelevant once the drag has begun.
    /// Trigger-up anywhere ends the drag.
    /// </summary>
    public class SettingsPanel : MonoBehaviour
    {
        public event Action<float> OnTranslucenceChanged;
        public event Action<float> OnSimilarityChanged;
        public event Action<float> OnMinMatchChanged;
        public event Action<bool>  OnGlowChanged;

        const float TrackWidthPx  = 240f;
        const float TrackHeightPx = 24f;     // hit area (was 12 — too thin to aim at)

        GameObject _root;
        SliderRow _translucence, _similarity, _minMatch;

        // Glow is a boolean, not a slider — rendered as an On/Off button pair
        // (the codebase has no Toggle component; ConnectSettingsPanel's depth
        // toggle uses the same two-button + highlight pattern).
        Button _glowOnBtn, _glowOffBtn;
        bool   _glow;

        public void Build(Transform parent, float translucence, float similarity, float minMatch,
                          bool glow = false)
        {
            _root = Mk.Panel(parent, "Settings", new Color(0.07f, 0.07f, 0.11f, 0.95f));
            var r = _root.GetComponent<RectTransform>();
            // 520 x 320. Anchored at y=-250 so the rect's TOP edge still lands at
            // panel y=-90 (-250 + 320/2), comfortably below the dictation box
            // (bottom at -50). Height grew from 240 to fit the 4th (Glow) row.
            r.anchoredPosition = new Vector2(0, -250);
            r.sizeDelta = new Vector2(520, 320);

            _translucence = BuildRow(
                y: 120, label: "Translucence",
                min: 0.01f, max: 1.00f, step: 0.05f, initial: translucence,
                onChanged: v => OnTranslucenceChanged?.Invoke(v));

            _similarity = BuildRow(
                y: 40, label: "Similarity",
                min: 0.85f, max: 0.99f, step: 0.01f, initial: similarity,
                onChanged: v => OnSimilarityChanged?.Invoke(v));

            _minMatch = BuildRow(
                y: -40, label: "Min match",
                min: 0.14f, max: 0.30f, step: 0.01f, initial: minMatch,
                onChanged: v => OnMinMatchChanged?.Invoke(v));

            BuildGlowRow(y: -120, initial: glow);

            _root.SetActive(false);
        }

        // Glow toggle row: label + On/Off button pair. No track / no ± buttons
        // (it's boolean) and no Tick() involvement — button taps route through
        // Button.onClick, unlike the sliders' manual ray-drag polling.
        void BuildGlowRow(float y, bool initial)
        {
            Mk.Label(_root.transform, "Glow  (attention pulse)", new Vector2(-130, y), 13,
                     new Color(0.8f, 0.85f, 0.95f), TextAlignmentOptions.MidlineLeft, 240);

            var inactive = new Color(0.3f, 0.3f, 0.4f);
            _glowOnBtn  = Mk.Btn(_root.transform, "On",  new Vector2(60,  y), new Vector2(70, 30),
                                 inactive, 14, () => SetGlow(true));
            _glowOffBtn = Mk.Btn(_root.transform, "Off", new Vector2(140, y), new Vector2(70, 30),
                                 inactive, 14, () => SetGlow(false));

            _glow = initial;
            UpdateGlowVisuals();
        }

        // Flip glow state, repaint the active button, and fire the event only on
        // an actual change (mirrors ConnectSettingsPanel.SetDepthEnabled).
        void SetGlow(bool on)
        {
            if (_glow == on) return;
            _glow = on;
            UpdateGlowVisuals();
            OnGlowChanged?.Invoke(on);
            Debug.Log($"[SettingsPanel] glow → {on}");
        }

        void UpdateGlowVisuals()
        {
            var active   = new Color(0.55f, 0.45f, 0.15f);
            var inactive = new Color(0.3f, 0.3f, 0.4f);
            SetButtonBg(_glowOnBtn,   _glow ? active : inactive);
            SetButtonBg(_glowOffBtn, !_glow ? active : inactive);
        }

        static void SetButtonBg(Button b, Color c)
        {
            if (b == null) return;
            if (b.targetGraphic is Image img) img.color = c;
            var cols = b.colors;
            cols.highlightedColor = c * 1.3f;
            cols.pressedColor     = c * 0.7f;
            b.colors = cols;
        }

        public void Show() { if (_root != null) _root.SetActive(true); }
        public void Hide()
        {
            if (_root != null) _root.SetActive(false);
            if (_translucence != null) _translucence.Dragging = false;
            if (_similarity   != null) _similarity.Dragging   = false;
            if (_minMatch     != null) _minMatch.Dragging     = false;
        }
        public bool IsVisible => _root != null && _root.activeSelf;

        // ---------- per-row ----------

        SliderRow BuildRow(float y, string label, float min, float max, float step, float initial,
                           Action<float> onChanged)
        {
            // Label rect at x=-130 wide 240 with MidlineLeft → text starts at
            // x=-250 (10 px inset from the panel's left edge at -260).
            Mk.Label(_root.transform, $"{label}  (drag or ±)", new Vector2(-130, y + 30), 13,
                     new Color(0.8f, 0.85f, 0.95f), TextAlignmentOptions.MidlineLeft, 240);

            var row = new SliderRow { Min = min, Max = max, Step = step, OnChanged = onChanged };

            // Track centered at x=-60 → spans [-180, +60]. Tall enough (24 px)
            // for the ray to land on it without aiming at the centerline.
            var trackGo = new GameObject("Track");
            trackGo.transform.SetParent(_root.transform, false);
            row.Track = trackGo.AddComponent<RectTransform>();
            row.Track.anchoredPosition = new Vector2(-60, y);
            row.Track.sizeDelta = new Vector2(TrackWidthPx, TrackHeightPx);
            var trackImg = trackGo.AddComponent<Image>();
            trackImg.color = new Color(0.18f, 0.20f, 0.28f);
            // raycastTarget defaults true — that's what catches the press.

            // Thumb is a child of the track, positioned in track-local x.
            var thumbGo = new GameObject("Thumb");
            thumbGo.transform.SetParent(row.Track, false);
            row.Thumb = thumbGo.AddComponent<RectTransform>();
            row.Thumb.anchorMin = row.Thumb.anchorMax = new Vector2(0.5f, 0.5f);
            row.Thumb.sizeDelta = new Vector2(22, 30);
            var thumbImg = thumbGo.AddComponent<Image>();
            thumbImg.color = new Color(0.95f, 0.85f, 0.35f);
            thumbImg.raycastTarget = false;   // taps pass through to the track

            // − value + buttons to the right of the track. Track right edge at
            // x=+60, so controls live in x ∈ [+76, +189].
            Mk.Btn(_root.transform, "−", new Vector2(90,  y), new Vector2(28, 26),
                   new Color(0.3f, 0.3f, 0.4f), 18, () => Step(row, -row.Step));
            row.ValueText = Mk.Label(_root.transform, "", new Vector2(135, y), 16, Color.white,
                                     TextAlignmentOptions.Center, 50);
            Mk.Btn(_root.transform, "+", new Vector2(180, y), new Vector2(28, 26),
                   new Color(0.3f, 0.3f, 0.4f), 18, () => Step(row, +row.Step));

            row.Set(initial, fire: false);
            return row;
        }

        // ---------- per-frame drag tick ----------

        public void Tick()
        {
            if (_root == null || !_root.activeSelf) return;

            bool triggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                            || OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            bool triggerUp   = OVRInput.GetUp  (OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                            || OVRInput.GetUp  (OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);
            bool triggerHeld = OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                            || OVRInput.Get(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);

            TickRow(_translucence, "translucence", triggerDown, triggerHeld, triggerUp);
            TickRow(_similarity,   "similarity",   triggerDown, triggerHeld, triggerUp);
            TickRow(_minMatch,     "minMatch",     triggerDown, triggerHeld, triggerUp);
        }

        void TickRow(SliderRow row, string tag, bool triggerDown, bool triggerHeld, bool triggerUp)
        {
            if (row == null) return;

            // Latch on the press only if the UI raycast actually lands on the
            // track. After that, vertical drift no longer matters (see below).
            if (triggerDown && IsRayOn(row.Track))
            {
                row.Dragging = true;
                Debug.Log($"[SettingsPanel] {tag} drag START");
            }

            if (row.Dragging && triggerHeld)
            {
                if (TryGetRayXOnTrackPlane(row.Track, out float xLocal))
                {
                    float frac = Mathf.Clamp01((xLocal + TrackWidthPx / 2f) / TrackWidthPx);
                    float v = row.Min + frac * (row.Max - row.Min);
                    if (Mathf.Abs(v - row.Current) > 1e-4f)
                    {
                        row.Set(v, fire: true);
                        Debug.Log($"[SettingsPanel] {tag} (drag) → {v:F2}");
                    }
                }
            }

            if (triggerUp && row.Dragging)
            {
                row.Dragging = false;
                Debug.Log($"[SettingsPanel] {tag} drag END");
            }
        }

        // ± button handler. Snaps to the row's step grid so successive clicks
        // never drift to non-grid values like 0.9499999.
        void Step(SliderRow row, float delta)
        {
            float v = row.Current + delta;
            v = Mathf.Round(v / row.Step) * row.Step;
            v = Mathf.Clamp(v, row.Min, row.Max);
            if (Mathf.Approximately(v, row.Current)) return;
            row.Set(v, fire: true);
            Debug.Log($"[SettingsPanel] step → {v:F2}");
        }

        // ---------- ray helpers ----------

        // UI raycast — used ONLY to detect the initial press on a track.
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

        // Geometric ray-plane intersection — used DURING a latched drag so the
        // value tracks the controller's pointing direction even if the ray
        // drifts off the track rect (vertical drift, or off the right edge).
        // We pretend the track is an infinite plane in track-space (XY plane),
        // intersect the controller ray with it, and read the hit's local x.
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
                if (Mathf.Abs(denom) < 1e-6f) continue;          // ray parallel to plane

                float t = Vector3.Dot(pPoint - origin, pNorm) / denom;
                if (t < 0f) continue;                            // intersection behind controller

                Vector3 worldHit = origin + dir * t;
                Vector3 localHit = track.InverseTransformPoint(worldHit);
                xLocal = localHit.x;
                return true;
            }
            return false;
        }

        // ---------- row state ----------

        class SliderRow
        {
            public RectTransform Track;
            public RectTransform Thumb;
            public TextMeshProUGUI ValueText;
            public float Min, Max, Step;
            // NaN sentinel so the very first Set always paints the visuals,
            // even if the caller-passed initial happens to equal 0.
            public float Current = float.NaN;
            public bool Dragging;
            public Action<float> OnChanged;

            public void Set(float v, bool fire)
            {
                v = Mathf.Clamp(v, Min, Max);
                if (Mathf.Approximately(v, Current)) return;
                Current = v;
                UpdateVisuals();
                if (fire) OnChanged?.Invoke(v);
            }

            void UpdateVisuals()
            {
                float frac = (Current - Min) / Mathf.Max(1e-6f, Max - Min);
                Thumb.anchoredPosition = new Vector2(-TrackWidthPx / 2f + frac * TrackWidthPx, 0);
                ValueText.text = Current.ToString("F2");
            }
        }
    }
}
