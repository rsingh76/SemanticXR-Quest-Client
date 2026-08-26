using UnityEngine;
using UnityEngine.UI;
using TMPro;

namespace SemanticXR.UI
{
    /// <summary>
    /// Head-locked arrow near the edge of the viewport that points toward the
    /// closest point-cloud centroid that is NOT currently on-screen. Hides the
    /// moment the target enters the camera frustum.
    ///
    /// Reuses IconFactory.TriDown (apex points -Y in texture space) and rotates
    /// it so the apex points in the target's on-screen direction.
    /// </summary>
    public class OffscreenPointCloudArrow : MonoBehaviour
    {
        [SerializeField] float distanceFromCamera = 0.8f;
        [SerializeField] float canvasSizePx       = 1200f;
        [SerializeField] float arrowRadiusPx      = 480f;
        [SerializeField] Vector2 arrowSizePx      = new Vector2(110f, 110f);
        // Soft cyan — reads well against both bright and dark passthrough
        // backgrounds without the neon harshness of pure yellow.
        [SerializeField] Color  arrowColor        = new Color(0.4f, 0.85f, 1f);

        PointCloudVisualizer _viz;
        Camera _cam;
        GameObject _canvasGo;
        RectTransform _arrowRect;
        TextMeshProUGUI _label;

        public void Bind(PointCloudVisualizer viz) => _viz = viz;

        void Awake() => BuildCanvas();

        void BuildCanvas()
        {
            _canvasGo = new GameObject("OffscreenArrowCanvas");
            _canvasGo.transform.SetParent(transform, worldPositionStays: false);

            var canvas = _canvasGo.AddComponent<Canvas>();
            canvas.renderMode = RenderMode.WorldSpace;
            _canvasGo.AddComponent<CanvasScaler>().dynamicPixelsPerUnit = 10f;

            var canvasRect = canvas.GetComponent<RectTransform>();
            canvasRect.sizeDelta  = new Vector2(canvasSizePx, canvasSizePx);
            canvasRect.localScale = Vector3.one * 0.001f;

            var arrowGo = new GameObject("Arrow");
            arrowGo.transform.SetParent(_canvasGo.transform, false);
            _arrowRect = arrowGo.AddComponent<RectTransform>();
            _arrowRect.anchorMin = _arrowRect.anchorMax = new Vector2(0.5f, 0.5f);
            _arrowRect.sizeDelta = arrowSizePx;

            var img = arrowGo.AddComponent<Image>();
            img.sprite        = IconFactory.Arrow;       // right-pointing by default
            img.color         = arrowColor;
            img.raycastTarget = false;

            // Direction hint. Sibling of the arrow (parented to the canvas, NOT
            // the arrow) so it stays upright while the arrow rotates to point at
            // the target. Text + position are set each frame in LateUpdate.
            _label = Mk.Label(_canvasGo.transform, "", Vector2.zero, 30f, arrowColor,
                TextAlignmentOptions.Center, 320f);

            _canvasGo.SetActive(false);
        }

        void LateUpdate()
        {
            if (_cam == null) _cam = Camera.main;
            if (_cam == null || _viz == null || _viz.Centroids.Count == 0) { SetVisible(false); return; }

            // Find the closest centroid that is NOT inside the camera frustum.
            Vector3 camPos = _cam.transform.position;
            int bestIdx = -1;
            float bestDistSq = float.MaxValue;
            var centroids = _viz.Centroids;
            for (int i = 0; i < centroids.Count; i++)
            {
                var c  = centroids[i];
                var vp = _cam.WorldToViewportPoint(c);
                bool offscreen = vp.z < 0f || vp.x < 0f || vp.x > 1f || vp.y < 0f || vp.y > 1f;
                if (!offscreen) continue;
                float d2 = (c - camPos).sqrMagnitude;
                if (d2 < bestDistSq) { bestDistSq = d2; bestIdx = i; }
            }

            if (bestIdx < 0) { SetVisible(false); return; }

            SetVisible(true);

            // Head-lock the canvas — placed one canvas-depth in front of the camera
            // and rotated so it faces the camera (+Z of canvas points forward, same
            // convention as the existing body-locked StreamCanvas).
            Vector3 canvasPos = camPos + _cam.transform.forward * distanceFromCamera;
            _canvasGo.transform.position = canvasPos;
            _canvasGo.transform.rotation = Quaternion.LookRotation(canvasPos - camPos);

            // 2D direction to the target in camera space (x = right, y = up).
            // For a target directly behind (local.z < 0 but x,y ≈ 0) we fall back
            // to +x; in practice the target has SOME x or y offset from the
            // camera's forward axis so this branch is rare.
            Vector3 local = _cam.transform.InverseTransformDirection(centroids[bestIdx] - camPos);
            Vector2 dir   = new Vector2(local.x, local.y);
            if (dir.sqrMagnitude < 1e-4f) dir = Vector2.right;
            dir.Normalize();

            _arrowRect.anchoredPosition = dir * arrowRadiusPx;

            // Arrow sprite points +X (right) by default, so a rotation of aDeg
            // about Z sends the tip to (cos a, sin a) = dir.
            float aDeg = Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg;
            _arrowRect.localRotation = Quaternion.Euler(0f, 0f, aDeg);

            // Adaptive hint: "Turn around" when the target is behind the camera
            // (local.z < 0 — the case the arrow alone can't disambiguate),
            // otherwise the dominant on-screen direction. Kept upright (no
            // rotation) and nudged inboard of the arrow so it stays on-canvas.
            string hint;
            if (local.z < 0f)                              hint = "Turn around";
            else if (Mathf.Abs(dir.x) >= Mathf.Abs(dir.y)) hint = dir.x >= 0f ? "Look right" : "Look left";
            else                                           hint = dir.y >= 0f ? "Look up"    : "Look down";
            _label.text = hint;
            _label.rectTransform.anchoredPosition = dir * (arrowRadiusPx - 110f);
        }

        void SetVisible(bool v)
        {
            if (_canvasGo != null && _canvasGo.activeSelf != v) _canvasGo.SetActive(v);
        }
    }
}
