using System.Collections.Generic;
using UnityEngine;
using XrVis;

namespace SemanticXR.UI
{
    /// Renders VisualizerServer.allPointClouds responses as small translucent
    /// colored spheres in Unity world space. Accumulates across queries;
    /// Clear() removes everything.
    ///
    /// Server is the source of truth for downsampling — no client-side voxel
    /// reduction. Whatever the server sends is what we render.
    ///
    /// Coordinate frame:
    ///   Server (vis_proto) uses right-handed OpenGL (X-right, Y-up, Z-back).
    ///   To put points back into Unity world space we flip Z:
    ///       (x, y, z)_rh  →  (x, y, -z)_unity.
    public enum BlendStyle
    {
        /// <summary>Standard alpha blend — overlays the point color on whatever is behind. Subtle, never gets brighter than the source colour.</summary>
        Alpha,
        /// <summary>Additive — colour is *added* to whatever is behind. Looks like a soft glow against the passthrough; can blow out on bright backgrounds.</summary>
        Additive,
    }

    public class PointCloudVisualizer : MonoBehaviour
    {
        [Tooltip("Diameter in meters of each rendered point.")]
        [SerializeField] float pointSize = 0.02f;

        [Tooltip("Per-point alpha (0 = invisible, 1 = opaque). For Alpha mode this is the " +
                 "transparency. For Additive mode it scales how strongly the colour is added " +
                 "(so it functions as a brightness knob).")]
        [SerializeField, Range(0.01f, 1f)] float pointAlpha = 0.18f;

        [Tooltip("How point colours combine with the world behind them. Additive looks " +
                 "glowing; Alpha looks subtle and never overbrights. Hot-swap to compare.")]
        [SerializeField] BlendStyle blendStyle = BlendStyle.Additive;

        [Tooltip("Render both faces of each sphere. ON = soft dot with weight, effective " +
                 "alpha at centre roughly doubles. OFF = single layer, much more translucent " +
                 "for the same alpha value.")]
        [SerializeField] bool doubleSided = true;

        // One batch per query response, so Clear() can wipe everything in one
        // sweep and the accumulate behaviour is obvious.
        readonly List<List<GameObject>> _batches = new();

        // One world-space centroid per rendered cluster, in the order
        // AddResponse added them. Consumed by OffscreenPointCloudArrow.
        readonly List<Vector3> _centroids = new();
        public IReadOnlyList<Vector3> Centroids => _centroids;

        public int BatchCount => _batches.Count;
        public int TotalPointCount
        {
            get { int n = 0; foreach (var b in _batches) n += b.Count; return n; }
        }

        // Shaders whose transparent variant is reliably compiled into builds.
        // Particle / sprite shaders ship with their transparent path always
        // included — URP Lit/Unlit's transparent variants get stripped if no
        // material asset opted into them at edit time.
        static readonly string[] TransparentShaderCandidates =
        {
            "Universal Render Pipeline/Particles/Unlit",
            "Particles/Standard Unlit",
            "Sprites/Default",
            "Universal Render Pipeline/Unlit",   // works only if its Transparent variant survived stripping
            "Standard",                          // last-ditch fallback (lit, but at least visible)
        };

        Shader _resolvedShader;
        bool   _shaderLogged;

        // Adds all clusters in `response` as a single batch.
        // Returns (numObjects, numPoints) for UI feedback.
        public (int objects, int points) AddResponse(allPointClouds response)
        {
            if (response == null || response.NumPointClouds == 0) return (0, 0);

            var batch = new List<GameObject>();
            int colorOffset = 0;
            int pointTotal  = 0;
            int objectCount = response.PointClouds.Count;

            for (int pc = 0; pc < objectCount; pc++)
            {
                var cloud = response.PointClouds[pc];

                // 3 floats per cluster in the flat Colors array.
                var color = Color.white;
                if (colorOffset + 2 < response.Colors.Count)
                {
                    color = new Color(
                        response.Colors[colorOffset],
                        response.Colors[colorOffset + 1],
                        response.Colors[colorOffset + 2]);
                }
                colorOffset += 3;

                int n = cloud.NumPoints;
                int available = cloud.Points.Count / 3;
                if (available < n) n = available;
                if (n == 0) continue;

                // One material per cluster, color (with alpha) baked in.
                // Avoids MaterialPropertyBlock pitfalls with URP shader instancing.
                var mat = MakeTranslucentMaterial(color);

                Vector3 sum = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    float x =  cloud.Points[i * 3];
                    float y =  cloud.Points[i * 3 + 1];
                    float z = -cloud.Points[i * 3 + 2];     // RH → LH handedness flip
                    var p = new Vector3(x, y, z);
                    batch.Add(CreateSphere(p, mat));
                    sum += p;
                }
                _centroids.Add(sum / n);
                pointTotal += n;
            }

            _batches.Add(batch);
            Debug.Log($"[PointCloudVisualizer] Added {objectCount} clusters, {pointTotal} points; total now {TotalPointCount}");
            return (objectCount, pointTotal);
        }

        public void Clear()
        {
            foreach (var batch in _batches)
                foreach (var go in batch)
                    if (go != null)
                    {
                        // Material per cluster was shared across all spheres in the
                        // cluster; destroy it via the first sphere we find with it.
                        var r = go.GetComponent<Renderer>();
                        if (r != null && r.sharedMaterial != null) Destroy(r.sharedMaterial);
                        Destroy(go);
                    }
            _batches.Clear();
            _centroids.Clear();
        }

        void OnDestroy() => Clear();

        GameObject CreateSphere(Vector3 pos, Material sharedMat)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.transform.SetParent(transform, worldPositionStays: true);
            s.transform.position   = pos;
            s.transform.localScale = Vector3.one * pointSize;

            var r = s.GetComponent<Renderer>();
            if (sharedMat != null) r.sharedMaterial = sharedMat;
            r.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            r.receiveShadows    = false;

            var col = s.GetComponent<Collider>();
            if (col != null) Destroy(col);
            return s;
        }

        // Build a translucent material with the supplied color + alpha.
        // Tries particle/sprite shaders first because their transparent
        // variants are always compiled into builds (URP Unlit's transparent
        // variant gets stripped in projects without a transparent URP/Unlit
        // material asset at edit time).
        Material MakeTranslucentMaterial(Color baseColor)
        {
            var col = baseColor; col.a = pointAlpha;

            if (_resolvedShader == null)
            {
                foreach (var name in TransparentShaderCandidates)
                {
                    _resolvedShader = Shader.Find(name);
                    if (_resolvedShader != null) break;
                }
                if (_resolvedShader == null)
                {
                    Debug.LogError("[PointCloudVisualizer] No transparent shader found; points will not render.");
                    return null;
                }
            }
            if (!_shaderLogged)
            {
                Debug.Log($"[PointCloudVisualizer] Point shader: '{_resolvedShader.name}', alpha={col.a:F2}");
                _shaderLogged = true;
            }

            var m = new Material(_resolvedShader);

            // Color — different shaders read different property names.
            m.color = col;                                                   // legacy _Color
            if (m.HasProperty("_BaseColor")) m.SetColor("_BaseColor", col);  // URP
            if (m.HasProperty("_TintColor")) m.SetColor("_TintColor", col);  // legacy particles

            // Cull mode is the secret translucency knob: Off (double-sided) renders
            // both sphere faces, doubling effective alpha at the centre. Back (single-
            // sided, default) gives a much lighter look for the same _Color.a.
            if (m.HasProperty("_Cull"))
                m.SetFloat("_Cull", (float)(doubleSided
                    ? UnityEngine.Rendering.CullMode.Off
                    : UnityEngine.Rendering.CullMode.Back));

            // Resolve src/dst blend factors for the chosen blend style.
            //   Alpha:    out = src*srcA + dst*(1-srcA)    — overlay
            //   Additive: out = src*srcA + dst              — glow on top of background
            float srcBlend = (float)UnityEngine.Rendering.BlendMode.SrcAlpha;
            float dstBlend = blendStyle == BlendStyle.Additive
                ? (float)UnityEngine.Rendering.BlendMode.One
                : (float)UnityEngine.Rendering.BlendMode.OneMinusSrcAlpha;

            // URP particle shaders expose _Blend as 0=Alpha / 1=Premultiply / 2=Additive / 3=Multiply.
            // Setting _Blend alone isn't enough at runtime — we set the explicit src/dst too,
            // and toggle the relevant keywords so the shader picks the right path.
            if (m.HasProperty("_Blend"))
                m.SetFloat("_Blend", blendStyle == BlendStyle.Additive ? 2f : 0f);
            if (m.HasProperty("_SrcBlend")) m.SetFloat("_SrcBlend", srcBlend);
            if (m.HasProperty("_DstBlend")) m.SetFloat("_DstBlend", dstBlend);

            if (blendStyle == BlendStyle.Additive)
            {
                m.EnableKeyword("_ALPHAMODULATE_ON");      // URP particles additive path
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            }
            else
            {
                m.DisableKeyword("_ALPHAMODULATE_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
            }

            // Coax URP Unlit / built-in Standard into transparent mode if those
            // were the shaders we resolved (lower-priority candidates).
            if (_resolvedShader.name == "Universal Render Pipeline/Unlit")
            {
                m.SetFloat("_Surface", 1f);
                m.SetFloat("_ZWrite", 0f);
                m.SetFloat("_AlphaClip", 0f);
                m.EnableKeyword("_SURFACE_TYPE_TRANSPARENT");
                m.DisableKeyword("_ALPHATEST_ON");
                m.SetOverrideTag("RenderType", "Transparent");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }
            else if (_resolvedShader.name == "Standard")
            {
                m.SetFloat("_Mode", 3f);
                m.SetInt("_SrcBlend", (int)srcBlend);
                m.SetInt("_DstBlend", (int)dstBlend);
                m.SetInt("_ZWrite", 0);
                m.DisableKeyword("_ALPHATEST_ON");
                m.EnableKeyword ("_ALPHABLEND_ON");
                m.DisableKeyword("_ALPHAPREMULTIPLY_ON");
                m.renderQueue = (int)UnityEngine.Rendering.RenderQueue.Transparent;
            }

            return m;
        }
    }
}
