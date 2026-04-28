# UI scripts — rendering rationale

Most files here are self-explanatory Unity UI plumbing. The one that's
non-trivial is `PointCloudVisualizer.cs`, which has gone through several
rewrites. This README captures *why* the current implementation looks the way
it does, what we tried and rejected, and which knobs do what.

If you only want to *use* the visualizer, skip to
[Inspector knobs](#inspector-knobs).

---

## What `PointCloudVisualizer` does

When a query hits the visualization gRPC server, the server returns an
`allPointClouds` proto: a list of `PointCloud` clusters (each with a flat
array of points in RH/OpenGL world coordinates), plus one RGB triplet per
cluster, plus a server processing time.

`PointCloudVisualizer.AddResponse(allPointClouds)` is the entry point. It:

1. **Flips Z per point** to convert from server-side RH/OpenGL (X-right,
   Y-up, Z-back) to Unity LH (X-right, Y-up, **+Z forward**). See
   `LhToRh()` in `TcpProtoClient.cs` / `GrpcFramesClient.cs` for the matching
   client → server transform that built the SLAM map.
2. **Spawns a small translucent sphere** at each point's world position.
3. **Computes the centroid** (mean of the cluster's points, post-flip) and
   appends it to `Centroids` — `OffscreenPointCloudArrow` consumes this list
   to point the user toward results outside their FOV.
4. **Logs** `[PointCloudVisualizer] Added N clusters, M points; total now X`.

`Clear()` destroys every spawned sphere + the materials they share, and empties
the centroid list. Triggered by the on-screen "Clear Points" button and on
disconnect.

---

## How rendering works (the implementation)

**One GameObject per point.** Unity's `PrimitiveType.Sphere`, scaled to
`pointSize`, parented under the visualizer. The sphere's collider is
destroyed on creation (we never need physics on these). One `Renderer` per
sphere, but **all spheres in a cluster share the same Material** — color +
alpha + blend mode are baked into the material per cluster.

**One Material per cluster.** The cluster's RGB color (from `response.Colors`)
is set with the configured `pointAlpha` as alpha. All spheres in the cluster
share that material instance. `Clear()` destroys these materials explicitly so
they don't leak as Unity scene assets.

**Shader resolution.** Tries this list at runtime, picks the first that
`Shader.Find` returns:

```
Universal Render Pipeline/Particles/Unlit
Particles/Standard Unlit
Sprites/Default
Universal Render Pipeline/Unlit
Standard
```

Why particle/sprite shaders first: the URP Lit/Unlit transparent path is
**often stripped at build time** in projects without a transparent
URP-Lit/Unlit material asset at edit time, leading to "I set `_Surface = 1` at
runtime and the cube is still opaque" mysteries. Particle and sprite shaders
ship their transparent variant unconditionally — they always work.

**Per-cluster setup** in `MakeTranslucentMaterial`:
- Color set on `_Color`, `_BaseColor`, and `_TintColor` (different shaders
  read different property names — covering all three is harmless).
- `_Cull` set per the `doubleSided` toggle (off = double-sided sphere with
  layered alpha, back = single-sided).
- `_Blend` (URP particles: 0 = Alpha, 2 = Additive) and explicit
  `_SrcBlend` / `_DstBlend` set per the `blendStyle` toggle.
- For URP Unlit / Standard fallbacks: extra ceremony to flip the shader
  to its transparent path (queue, keywords, override tag).

**Coordinate-frame test:** the original verification was an "RGB axis gnomon"
synthesized by the debug server's `--fake-points` flag — three clusters, red
along +X, green along +Y, blue along +Z (server frame). Confirmed
red→right, green→up, blue→toward-user (i.e. away from where the gnomon was
anchored). That's the correct outcome of `(x, y, -z)`.

---

## What we tried and rejected

Documenting these so the next person doesn't re-walk the path.

### `Graphics.DrawMeshInstanced` + low-poly icosphere

On paper, this is the right way to render thousands of identical small meshes:
one shared mesh, one shared material, per-instance matrices, one draw call per
cluster. Built it, confirmed the math worked (centroids appeared correctly,
arrow pointed at them), but **nothing rendered** on Quest. URP + XR + the
URP-Unlit shader's transparent variant being stripped seems to be the
culprit — same root cause as why URP transparent is so finicky here.

If you ever actually need to push tens of thousands of points, this is the
direction to revisit. With a known-shipped shader (e.g. `Particles/Standard
Unlit`) and `Graphics.DrawMeshInstanced`, it should work — we just didn't get
that combination to render in our sessions.

### Unity `ParticleSystem`

Worked — points appeared as expected — but the visual was "ugly" per the
user's read; the particle billboards didn't have the soft 3D feel that
GameObject spheres do. Reverted.

### Translucent **bounding boxes** (one cube per object)

Tried this for a few sessions to test "show the object envelope, not the
points." Visually it failed: rough/incorrect SLAM bboxes look bad as opaque
cubes; ambiguity between two near-overlapping objects becomes a confusing
mess; and the bbox itself isn't always tight enough to be informative. Failure
cases were "horrible" per the user. Reverted, and the BBox proto field was
also reverted (kept the spheres; bbox is no longer carried by `PointCloud` in
the proto).

The orientation-correct version of this would render the actual oriented box
from 8 corners with proper handedness; we built only the AABB version. Not
worth pursuing further unless the use case shifts.

---

## Inspector knobs

Exposed on the auto-spawned `PointClouds` child GameObject created by
`StreamingUI.Awake`:

| Field | Default | Effect |
|-------|---------|--------|
| `Point Size` | `0.02` (m) | Diameter of each sphere. 2 cm reads well in VR at typical query distances. |
| `Point Alpha` | `0.18` | In **Alpha** mode → opacity. In **Additive** mode → brightness scalar (controls how strongly the colour is added). Slider 0.01–1.0. |
| `Blend Style` | `Additive` | `Alpha` (overlay) vs `Additive` (glow). Hot-swap in the inspector to compare; flipping after a query needs a `Clear` + re-query to repaint existing spheres. |
| `Double Sided` | `true` | When on, both faces of each sphere render — effective alpha at the centre roughly doubles, gives a "soft dot with weight" look. Off = single layer, much more translucent. |

**Quick recipes:**
- **Subtle ghosty overlay** — Blend `Alpha`, single-sided, alpha 0.05–0.10.
- **Subtle glow** — Blend `Additive`, single-sided, alpha 0.10–0.20.
- **Luminous orb** — Blend `Additive`, double-sided, alpha 0.30–0.50 (heavy).
- **Original "soft dot"** — Blend `Alpha`, double-sided, alpha 0.18 (was the
  default before additive landed).

---

## Performance — what translucency costs

Honest accounting per sphere vs an opaque equivalent:

- Fragment-shader invocations: **2×** when double-sided (`_Cull = Off` runs
  both faces).
- Per-pixel framebuffer ops: **~2-3×** (read + blend + write vs just write).
- No early-Z rejection — translucent passes don't write depth.
- Quest's tile-based GPU can't do its overdraw-collapsing tricks on
  translucent geometry.
- Sort overhead: one sort across all transparent objects per frame (CPU,
  small).

**Net: ~3–5× more GPU work per sphere.** With voxel-downsampled clusters
(server side caps to roughly 20–150 points per cluster), this is in the
noise. With unbounded clusters (the abandoned 200-point random subset) it
mattered.

Server-side voxel downsampling is the load-bearing optimization here — see
the matching note in the visualization service.

---

## Future: cheap glow

The "additive" blend mode already gives a glow-against-passthrough feel for
free. If we want richer glow:

1. **Two-pass per point** — solid ~1 cm core + larger semi-transparent halo
   around it. Doubles sphere count but still trivial. No engine config
   change.
2. **HDR + bloom post-process** — actual emissive glow with a halo radius
   driven by a screen-space pass. Quest 3 can afford one bloom pass. Bigger
   commitment because it's a project-wide URP renderer-feature change.

We've stopped at additive blend. If "ok this is fine but I want more pop"
becomes a real ask, two-pass is the cheapest next step.

---

## Public API

What other code can rely on:

```csharp
PointCloudVisualizer pv;

(int objects, int points) = pv.AddResponse(allPointClouds response);
pv.Clear();

IReadOnlyList<Vector3> centroids = pv.Centroids;   // one per cluster, in add order
int batchCount  = pv.BatchCount;                   // how many AddResponse calls have stuck
int totalPoints = pv.TotalPointCount;              // total spheres on screen right now
```

`OffscreenPointCloudArrow` consumes `Centroids`; everything else uses just
`AddResponse` + `Clear`.
