using System.Collections.Generic;
using UnityEngine;
using XrVis;

namespace SemanticXR.UI
{
    /// Renders VisualizerServer.allPointClouds responses as colored spheres in
    /// Unity world space. Accumulates across queries; Clear() removes everything.
    ///
    /// Coordinate frame:
    ///   Server (vis_proto) uses the same right-handed OpenGL convention the
    ///   Quest client uses on the wire (X-right, Y-up, Z-back — see LhToRh in
    ///   TcpProtoClient / GrpcFramesClient). To put points back into Unity
    ///   world space we flip Z: (x, y, z)_rh  →  (x, y, -z)_unity.
    public class PointCloudVisualizer : MonoBehaviour
    {
        [Tooltip("Diameter in meters of each rendered point.")]
        [SerializeField] float pointSize = 0.025f;

        // One batch per query response, so Clear() can wipe everything
        // in one sweep and the accumulate behavior is obvious.
        readonly List<List<GameObject>> _batches = new();
        Material _sharedMaterial;   // GPU-instanced, one copy shared by every sphere

        static readonly string[] CandidateShaders = {
            "Universal Render Pipeline/Unlit",
            "Unlit/Color",
            "Sprites/Default",
        };

        Material SharedMaterial
        {
            get
            {
                if (_sharedMaterial == null)
                {
                    Shader shader = null;
                    foreach (var name in CandidateShaders)
                    {
                        shader = Shader.Find(name);
                        if (shader != null) break;
                    }
                    if (shader == null)
                    {
                        // No known unlit shader available — let the sphere keep
                        // its default material. Per-sphere color via MPB still
                        // works if the default shader exposes _Color/_BaseColor.
                        Debug.LogWarning("[PointCloudVisualizer] No unlit shader found; using default sphere material.");
                        return null;
                    }
                    _sharedMaterial = new Material(shader) { enableInstancing = true };
                }
                return _sharedMaterial;
            }
        }

        public int BatchCount     => _batches.Count;
        public int TotalPointCount
        {
            get { int n = 0; foreach (var b in _batches) n += b.Count; return n; }
        }

        // One world-space centroid per rendered cluster, in the order AddResponse
        // added them. Consumed by OffscreenPointCloudArrow to pick what to point at.
        readonly List<Vector3> _centroids = new();
        public IReadOnlyList<Vector3> Centroids => _centroids;

        // Adds all clusters in `response` as a single batch.
        // Returns (numObjects, numPoints) for UI feedback.
        public (int objects, int points) AddResponse(allPointClouds response)
        {
            if (response == null || response.NumPointClouds == 0)
                return (0, 0);

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

                Vector3 sum = Vector3.zero;
                for (int i = 0; i < n; i++)
                {
                    float x = cloud.Points[i * 3];
                    float y = cloud.Points[i * 3 + 1];
                    float z = -cloud.Points[i * 3 + 2];    // RH → LH handedness flip
                    var p = new Vector3(x, y, z);
                    batch.Add(CreateSphere(p, color));
                    sum += p;
                }
                if (n > 0) _centroids.Add(sum / n);
                pointTotal += n;
            }

            _batches.Add(batch);
            return (objectCount, pointTotal);
        }

        public void Clear()
        {
            foreach (var batch in _batches)
                foreach (var go in batch)
                    if (go != null) Destroy(go);
            _batches.Clear();
            _centroids.Clear();
        }

        void OnDestroy()
        {
            Clear();
            if (_sharedMaterial != null) Destroy(_sharedMaterial);
        }

        GameObject CreateSphere(Vector3 pos, Color col)
        {
            var s = GameObject.CreatePrimitive(PrimitiveType.Sphere);
            s.transform.SetParent(transform, worldPositionStays: true);
            s.transform.position   = pos;
            s.transform.localScale = Vector3.one * pointSize;

            // Share one instanced material across all points; per-sphere
            // color goes through a MaterialPropertyBlock (no GC per instance).
            var r = s.GetComponent<Renderer>();
            var shared = SharedMaterial;
            if (shared != null) r.sharedMaterial = shared;

            var mpb = new MaterialPropertyBlock();
            // Unlit color property name varies by shader; set the common ones.
            mpb.SetColor("_BaseColor", col);   // URP Unlit
            mpb.SetColor("_Color",     col);   // Legacy Unlit/Color, Standard
            r.SetPropertyBlock(mpb);

            var col3d = s.GetComponent<Collider>();
            if (col3d != null) Destroy(col3d);  // no physics needed
            return s;
        }
    }
}
