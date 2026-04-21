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
python unity_server.py             # listens on :50051
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

Each session writes under `debug_server/debug_output/session_N/`:

```
session_N/
├── frame_NNNNNN.h265         # raw H.265 NAL units from the Quest
├── depth_NNNNNN.raw          # raw R16_SFloat depth bytes (top-down)
├── depth_NNNNNN.npy          # metric depth in meters (float32)
├── meta_NNNNNN.txt           # per-frame pose, intrinsics, timestamps
├── decoded_jpg/              # decoded RGB (JPEG, only when decoded)
│   └── frame_NNNNNN.jpg
├── depth_png/                # 16-bit depth in mm (only when decoded)
│   └── depth_NNNNNN.png
├── unity_mesh.ply            # colorless TSDF mesh (after unity_reconstruct)
└── unity_recolor_*.ply       # recolored mesh (after unity_recolor)
```

Sessions are numbered `session_0`, `session_1`, … up to `session_99` — the
server always creates the lowest free slot and never overwrites.

## File reference

### Server and protocol

| File | Purpose |
|---|---|
| `unity_server.py` | TCP server that accepts Quest connections, writes raw bytes + metadata per frame. `--decode` flag turns on inline JPG/NPY/PNG generation (expensive; default is raw-only). |
| `xr_service_pb2.py`, `xr_service_pb2_grpc.py` | Generated protobuf stubs from `Assets/Scripts/Proto/xr_service.proto`. Do not edit by hand — regenerate with `protoc` if the proto changes. |

### Decoding

| File | Purpose |
|---|---|
| `decode_session.py` | Offline decoder for raw-only sessions. Feeds all `.h265` packets through a stateful PyAV HEVC decoder (in order) and writes `decoded_jpg/*.jpg`. Also converts `.raw` depth to `.npy` + 16-bit `.png`. Handles both R16_SFloat (2 bytes/px, current client) and R16G16B16A16_SFloat (8 bytes/px, legacy) depth formats automatically. |

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

Files with an underscore prefix (`_*.py`) are one-off debugging experiments:
pose-convention tests, projection sanity checks, calibration trials, etc. They
are listed in `.gitignore` and not maintained. Safe to delete; kept locally for
reference.

## Key concepts

**Why "raw-only" server mode?** Decoding H.265 to JPG and depth to NPY inline
on the server blocks the receive loop for ~60 ms/frame. On a streaming
pipeline that caps server FPS at ~15 regardless of network. Raw-only writes
only `.h265`, `.raw`, and `.txt` per frame (~5 ms), so the server becomes
network-limited. Decoded outputs are produced after the fact with
`decode_session.py`.

**Why R16 depth (not R16G16B16A16)?** Meta's
`_PreprocessedEnvironmentDepthTexture` is R16G16B16A16_SFloat, but only the R
channel carries the depth for the active eye. The client strips the other
three channels before transmit, cutting depth payload by 4×
(~800 KB → ~200 KB per frame).

**Pose conventions on the wire.** Client sends all 4×4 poses converted from
Unity left-handed to right-handed via `LhToRh` (negate Z column + row). The
server stores them as-is. `unity_reconstruct.py` applies
`diag(1,-1,-1,1) @ inv(pose)` to convert to OpenCV camera extrinsic for
Open3D.

**Depth format on the wire.** Depth is sent **top-down** (client flips the
OpenGL-order texture on the encode side), so the server and downstream
scripts can use `cy = tanT * fy` directly without further flipping.
