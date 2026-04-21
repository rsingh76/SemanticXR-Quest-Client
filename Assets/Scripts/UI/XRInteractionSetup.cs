using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.XR.Interaction.Toolkit.Interactors;
using UnityEngine.XR.Interaction.Toolkit.Interactors.Visuals;
using UnityEngine.XR.Interaction.Toolkit.UI;

namespace SemanticXR.UI
{
    public class XRInteractionSetup : MonoBehaviour
    {
        void Awake()
        {
            if (FindAnyObjectByType<XRInteractionManager>() == null)
                new GameObject("XRInteractionManager").AddComponent<XRInteractionManager>();

            var es = FindAnyObjectByType<EventSystem>();
            if (es == null)
                es = new GameObject("EventSystem").AddComponent<EventSystem>();
            foreach (var old in es.GetComponents<StandaloneInputModule>())
                Destroy(old);
            if (FindAnyObjectByType<XRUIInputModule>() == null)
                es.gameObject.AddComponent<XRUIInputModule>();

            SetupRay("RightControllerAnchor");
        }

        void SetupRay(string anchorName)
        {
            var anchor = GameObject.Find(anchorName);
            if (anchor == null) return;
            if (anchor.GetComponent<XRRayInteractor>() != null) return;

            var ray = anchor.AddComponent<XRRayInteractor>();
            ray.maxRaycastDistance = 10f;
            ray.enableUIInteraction = true;

            Debug.LogWarning($"[XRSetup] Ray on {anchorName}");
        }

        LineRenderer _laserLine;
        GameObject _reticle;
        Transform _reticleT;

        void LateUpdate()
        {
            // Draw laser from right controller
            var anchor = GameObject.Find("RightControllerAnchor");
            if (anchor == null) return;

            if (_laserLine == null)
            {
                _laserLine = gameObject.AddComponent<LineRenderer>();
                _laserLine.startWidth = 0.003f;
                _laserLine.endWidth = 0.001f;
                _laserLine.positionCount = 2;
                _laserLine.material = new Material(Shader.Find("Sprites/Default"));
                _laserLine.startColor = new Color(0.4f, 0.8f, 1f, 0.6f);
                _laserLine.endColor = new Color(0.4f, 0.8f, 1f, 0.1f);
            }

            if (_reticle == null)
            {
                _reticle = GameObject.CreatePrimitive(PrimitiveType.Sphere);
                _reticle.name = "RayReticle";
                var col = _reticle.GetComponent<Collider>();
                if (col != null) Destroy(col);
                _reticleT = _reticle.transform;
                _reticleT.SetParent(transform, false);
                _reticleT.localScale = Vector3.one * 0.012f;
                var mr = _reticle.GetComponent<MeshRenderer>();
                mr.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                mr.receiveShadows = false;
                var mat = new Material(Shader.Find("Unlit/Color"));
                mat.color = new Color(0.55f, 0.85f, 1f, 1f);
                mr.material = mat;
                _reticle.SetActive(false);
            }

            var origin = anchor.transform.position;
            var end = origin + anchor.transform.forward * 5f;
            bool hit = false;

            // Prefer the ray interactor's UI hit so the dot lines up with
            // whatever the XRUIInputModule considers the actual target.
            foreach (var ray in FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None))
            {
                if (ray.TryGetCurrentUIRaycastResult(out var result) && result.gameObject != null)
                {
                    end = result.worldPosition;
                    hit = true;
                    break;
                }
            }

            _laserLine.SetPosition(0, origin);
            _laserLine.SetPosition(1, end);

            if (hit)
            {
                if (!_reticle.activeSelf) _reticle.SetActive(true);
                _reticleT.position = end;
                // Keep the reticle a constant apparent size regardless of distance.
                float dist = Vector3.Distance(origin, end);
                _reticleT.localScale = Vector3.one * Mathf.Clamp(0.012f * dist, 0.006f, 0.03f);
            }
            else if (_reticle.activeSelf)
            {
                _reticle.SetActive(false);
            }
        }

        void Update()
        {
            bool triggerDown = OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.RTouch)
                            || OVRInput.GetDown(OVRInput.Button.PrimaryIndexTrigger, OVRInput.Controller.LTouch);

            if (!triggerDown) return;

            // Find all ray interactors and force-select whatever they're hovering
            foreach (var ray in FindObjectsByType<XRRayInteractor>(FindObjectsSortMode.None))
            {
                // The ray interactor tracks what UI element it's hovering via the XRUI system
                // We need to get the current UI target and click it
                if (ray.TryGetCurrentUIRaycastResult(out var result) && result.gameObject != null)
                {
                    var target = result.gameObject;
                    var eventSystem = EventSystem.current;
                    if (eventSystem == null) continue;

                    var pointerData = new PointerEventData(eventSystem)
                    {
                        position = result.screenPosition,
                        pointerPressRaycast = result,
                    };

                    var clickHandler = ExecuteEvents.GetEventHandler<UnityEngine.EventSystems.IPointerClickHandler>(target);
                    if (clickHandler != null)
                    {
                        ExecuteEvents.Execute(clickHandler, pointerData, ExecuteEvents.pointerClickHandler);
                        Debug.LogWarning($"[XRSetup] Clicked: {clickHandler.name}");
                    }
                }
            }
        }
    }
}
