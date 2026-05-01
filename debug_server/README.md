# SemanticXR Debug Server

Python-side pipeline that receives captures from the Quest client, saves them
to disk, and builds/visualizes 3D reconstructions offline.

## Setup

Use either `environment.yml` (conda) or `requirements.txt` (pip):

```bash
conda env create -f environment.yml
conda activate semanticxr
# or
pip install -r requirements.txt
```

Third-party dependencies: `av` (PyAV), `numpy`, `scipy`, `open3d`, `pillow`,
`matplotlib`, `plotly`, `grpcio`, `protobuf`.

## Typical workflow

Capture a session from the Quest client, then on the server machine:

```bash
# 1. Run the server (defaults to raw-only mode — fast, no live decoding)
python unity_grpc_server.py        # gRPC (default, mirrors production) — listens on :50051
python unity_server.py             # raw TCP debug transport — listens on :50055
python unity_server.py --decode    # decode JPG/NPY/PNG as frames arrive (slower)

# 2. If captured in raw-only mode, decode the raw files to JPG + NPY first
python decode_session.py debug_output/session_0

# 3. Build a colorless depth-only mesh
python unity_reconstruct.py debug_output/session_0 --voxel 0.02

# 4. Project RGB frames onto the mesh
python unity_recolor.py debug_output/session_0
# -> debug_output/session_0/unity_recolor_unity_mesh_all.ply
```

Open the resulting `.ply` in MeshLab. Enable **Render → Color → Per Vertex**
to see color.

## Session directory layout

Each session writes under `debug_server/debug_output/session_N/`. The layout
is a **strict superset** of semantic-slam-server's Quest replay layout
(`datasets/quest/dataset_<N>/`), so any session captured here is also loadable
by `slam.datasets.quest.QuestDataset` over there. Raw streamed bytes live in
their own `raw/` subdir to keep the scene root tidy.

```
session_N/
├── intrinsics.json           # scene-level RGB+depth intrinsics + sizes (once, on first frame)
├── meta/                     # per-frame poses + timestamps + frame stats (always)
│   └── meta_NNNNNN.json
├── decoded_jpg/              # decoded RGB (JPEG, only when --decode or after decode_session.py)
│   └── frame_NNNNNN.jpg
├── depth/                    # float32 metric depth in meters (only when decoded)
│   └── depth_NNNNNN.npy
├── depth_png/                # 16-bit depth in mm (only when decoded; debug viewer)
│   └── depth_NNNNNN.png
├── raw/                      # raw streamed bytes (always; debug-server-only)
│   ├── frame_NNNNNN.h265     #   raw H.265 NAL units
│   └── depth_NNNNNN.raw      #   raw R16_SFloat depth bytes
├── unity_mesh.ply            # colorless TSDF mesh (after unity_reconstruct)
└── unity_recolor_*.ply       # recolored mesh (after unity_recolor)
```

Sessions are numbered `session_0`, `session_1`, … up to `session_99` — the
server always creates the lowest free slot and never overwrites.

### Single source of truth for layout: `session_io.py`

Every consumer (servers, decoder, reconstruction scripts, plotters) goes
through [`session_io.Session`](session_io.py). Path helpers, JSON parsing, and
the writers all live there — if a file's location ever changes, change it in
one place. Typical use:

```python
from session_io import Session
s = Session("debug_output/session_3")
for num in s.complete_frames():               # frames with rgb + depth + meta
    meta = s.load_meta(num)                   # flat dict (poses, intrinsics, ...)
    rgb  = Image.open(s.jpg_path(num))
    depth = np.load(s.depth_npy_path(num))
```

The writer functions (`write_meta`, `write_intrinsics_once`,
`ensure_session_dirs`) live in the same module and are imported by both
servers, so the read and write paths can never drift.

### Pose conventions on disk

All 4×4 poses (`head_pose`, `rgb_camera_pose`, `depth_pose`) are stored
**as received from the proto** — camera-to-world, right-handed, X-right Y-up
Z-back (OpenGL / OpenXR tracking space). The Unity-side `LhToRh` flip
(negate Z column + row) runs on the device before transmit; nothing is
re-transformed on the server. The OpenGL → OpenCV flip needed by Open3D /
standard pinhole projection is applied at consumption time:
`reconstruct_tsdf.get_extrinsic_for_open3d` does `diag(1, -1, -1, 1) @ inv(pose)`.

### Intrinsics conventions on disk

`intrinsics.json` and the `intrinsics`/`depth_intrinsics` fields inside
`meta/meta_NNNNNN.json` use the bare key names `cx` / `cy`, holding what the
client sent. The Quest client pre-flips depth top-down before transmit
(see DEPTH_CONVERSION.md), so `cy` is interpretable directly as a top-down
image principal point — reconstruction code feeds it straight into
`o3d.camera.PinholeCameraIntrinsic`. **Note:** semantic-slam-server's Quest
layout calls the same value `cy_yup` (Y-up viewport, measured from the image
bottom). The numerical value is the same; the field-name difference is
documented in `intrinsics.json`'s `convention` string.

## File reference

### Server and protocol

| File | Purpose |
|---|---|
| `unity_grpc_server.py` | **Default.** gRPC server (mirrors production) — listens on `:50051`. Writes raw `.h265`/`.raw` into `raw/` always; `meta/*.json` always; `decoded_jpg/`, `depth/`, `depth_png/` only with `--decode`. |
| `unity_server.py` | TCP server (legacy debug transport) — listens on `:50055`. Identical on-disk layout to the gRPC server. `--decode` flag turns on inline JPG/NPY/PNG generation (expensive; default is raw-only). |
| `session_io.py` | **Single source of truth** for the on-disk layout — `Session` reader class, `write_meta` / `write_intrinsics_once` / `ensure_session_dirs` writers, and the layout constants both servers and consumers import. |
| `proto/` | gRPC contract: `xr_service.proto` + generated `*_pb2.py`/`*_pb2_grpc.py`/`*.pyi`, plus `vis.proto` for the audio server. Kept in sync with `semantic-slam-server/server/xr_service.proto` (same field numbers, same wire format). Regenerate with `python -m grpc_tools.protoc -I proto --python_out=proto --grpc_python_out=proto --pyi_out=proto proto/xr_service.proto` if the proto changes. The `proto/__init__.py` puts the directory on `sys.path` so generated cross-imports between `*_pb2_grpc.py` and `*_pb2.py` keep working without post-edit. |

### Decoding

| File | Purpose |
|---|---|
| `decode_session.py` | Offline decoder for raw-only sessions. Feeds the `.h265` packets in `raw/` through a stateful PyAV HEVC decoder (in order) and writes `decoded_jpg/*.jpg`. Also converts `raw/depth_*.raw` to `depth/*.npy` + `depth_png/*.png`. Handles both R16_SFloat (2 bytes/px, current client) and R16G16B16A16_SFloat (8 bytes/px, legacy) depth formats automatically. |

### Reconstruction (pick one)

| File | Approach | When to use |
|---|---|---|
| **`unity_reconstruct.py`** | Pure depth-only TSDF, clean coordinate conventions | **Default.** Build geometry, color separately. |
| `reconstruct_tsdf.py` | Older color-aware TSDF that bakes RGB into voxels during integration | Legacy; skips frames missing either RGB or depth, has parallax issues because RGB and depth sensors are physically offset. |
| `unity_rgbd_reconstruct.py` | Projects every depth pixel through both poses to build an aligned-RGB image, feeds `(rgb, depth)` into color-aware TSDF | Diagnostic — validates that depth→world→RGB unprojection math is correct end-to-end. Outputs lower-res color (320×320 of the RGB image). |

### Coloring

| File | Output | Notes |
|---|---|---|
| **`unity_recolor.py`** | Recolored mesh (`.ply`) | **Default.** Takes an existing `unity_mesh.ply`, projects each RGB frame using its saved RGB pose with depth-image occlusion. |
| `unity_rgbd_raycast.py` | Colored **point cloud** (not a mesh) | Casts a ray from every RGB pixel; first mesh hit gets that pixel's color. Stricter occlusion than vertex projection (no parallax bleed) but output is points, not surfaces. |
| `unity_rgbd_fixed_extrinsic.py` | Colored cloud | Computes a single `T_rgb←depth` rig transform averaged over the session and uses it identically every frame. Fixes per-frame pose jitter (~3 mm / 0.5°) between depth and RGB pose streams. Use when recoloring looks jittery or flickers. |

### Calibration

| File | Purpose |
|---|---|
| `bake_calibration.py` | Solves for a fixed depth↔RGB rig transform from a session, writes a calibration JSON. |
| `apply_calibration.py` | Re-projects RGB frames using a baked calibration (overlay debug images). |

### Visualization

| File | Purpose |
|---|---|
| `plot_trajectories.py` | 2D/3D plots of head / RGB-camera / depth-camera poses over a session (matplotlib + plotly). |
| `visualize_with_trajectory.py` | Open3D viewer combining the TSDF mesh with the camera trajectory. |

### Documentation

| File | Purpose |
|---|---|
| `DEPTH_CONVERSION.md` | Derivation of the `metric_depth = sensor_near_z / R_channel` formula used to convert Meta's inverted-NDC depth texture to meters. |

### Scratchpad / experimental scripts

Underscore-prefixed scripts (`_*.py`) live under `_scratchpad/` — one-off
debugging experiments: pose-convention tests, projection sanity checks,
calibration trials, etc. The whole directory is listed in `.gitignore` and
not maintained. Safe to delete; kept locally for reference. Their imports
of production modules may have rotted since the layout refactor — fix only
if you actually need them.

## Key concepts

**Why "raw-only" server mode?** Decoding H.265 to JPG and depth to NPY inline
on the server blocks the receive loop for ~60 ms/frame. On a streaming
pipeline that caps server FPS at ~15 regardless of network. Raw-only writes
only `raw/*.h265`, `raw/*.raw`, and `meta/*.json` per frame (~5 ms), so the
server becomes network-limited. Decoded outputs are produced after the fact
with `decode_session.py`.

**Why a "superset" of semantic-slam-server's layout, instead of an exact
match?** semantic-slam-server is the production SLAM pipeline; it decodes
inline because it has to (downstream inference needs RGB+depth tensors per
frame). The debug server's job is the opposite: capture as fast as the
network allows and decode later. The session layout is identical for the
files semantic-slam-server cares about (`intrinsics.json`, `meta/*.json`,
`decoded_jpg/`, `depth/`), so any session captured here is loadable by its
`QuestDataset` — but we additionally keep the raw `.h265` / `.raw` files in a
sibling `raw/` subdir so re-decode doesn't require re-capture.

**Why R16 depth (not R16G16B16A16)?** Meta's
`_PreprocessedEnvironmentDepthTexture` is R16G16B16A16_SFloat, but only the R
channel carries the depth for the active eye. The client strips the other
three channels before transmit, cutting depth payload by 4×
(~800 KB → ~200 KB per frame).

**Pose conventions on the wire.** Client sends all 4×4 poses converted from
Unity left-handed to right-handed via `LhToRh` (negate Z column + row). The
server stores them **as-is** under their canonical names (`head_pose`,
`rgb_camera_pose`, `depth_pose`); the legacy `pose` proto field is treated
as an alias of `head_pose`. The OpenGL→OpenCV flip needed by Open3D /
pinhole projection (`diag(1, -1, -1, 1) @ inv(pose)`) is applied at
consumption — see `reconstruct_tsdf.get_extrinsic_for_open3d`. This matches
semantic-slam-server's convention exactly, so a session captured here can
feed either pipeline without re-encoding poses.

**Depth format on the wire.** Depth is sent **top-down** (client flips the
OpenGL-order texture on the encode side), so the server and downstream
scripts can use `cy = tanT * fy` directly without further flipping.
