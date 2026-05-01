"""Session I/O for the SemanticXR debug server.

Single source of truth for the on-disk layout. **Every consumer goes through
this module** — if a file's location changes, change it here and nowhere else.
Likewise, the two server scripts (unity_grpc_server.py, unity_server.py)
import the writers from here so the read and write paths can never drift.

On-disk layout (matches semantic-slam-server's ``datasets/quest/dataset_<N>/``
for the shared subset; ``raw/`` is debug-server-only and holds the streamed
HEVC/depth bytes for offline re-decode):

    <session>/
    ├── intrinsics.json              # scene-level RGB+depth intrinsics + sizes (once)
    ├── meta/
    │   └── meta_NNNNNN.json         # per-frame poses + timestamps + frame stats
    ├── decoded_jpg/
    │   └── frame_NNNNNN.jpg         # decoded RGB (with --decode)
    ├── depth/
    │   └── depth_NNNNNN.npy         # float32 metric meters (with --decode)
    ├── depth_png/
    │   └── depth_NNNNNN.png         # 16-bit mm — debug viewer (with --decode)
    └── raw/
        ├── frame_NNNNNN.h265        # raw HEVC packets (always)
        └── depth_NNNNNN.raw         # raw R16_SFloat depth bytes (always)

Pose convention (consistent with semantic-slam-server):
    All 4×4 poses (head_pose, rgb_camera_pose, depth_pose) are stored AS
    RECEIVED from the proto — camera-to-world, right-handed, X-right Y-up
    Z-back (OpenGL/OpenXR; Unity LH→RH already applied client-side). The
    OpenGL→OpenCV flip needed by Open3D / pinhole projection is applied at
    consumption time, never baked into the on-disk pose. See
    ``reconstruct_tsdf.get_extrinsic_for_open3d``.

Intrinsics convention:
    cx/cy use the bare keys ``cx`` / ``cy``. The Quest client pre-flips depth
    top-down before transmit (see DEPTH_CONVERSION.md), so cy is interpretable
    directly as a top-down image principal point — reconstruction code feeds
    it straight into ``o3d.camera.PinholeCameraIntrinsic``. NOTE:
    semantic-slam-server's quest layout calls the same value ``cy_yup`` (Y-up
    viewport, from image bottom). Numerical value is identical; only the
    field name differs. ``intrinsics.json``'s ``convention`` string documents
    this on disk.
"""
from __future__ import annotations

import json
from pathlib import Path
from typing import List, Optional, Union

import numpy as np


# ---------------------------------------------------------------------------
# Layout constants — one place. Writers and readers agree by importing these,
# not by hard-coding strings. If you rename a subdir, change it here.
# ---------------------------------------------------------------------------

META_DIR = "meta"
DEPTH_DIR = "depth"
JPG_DIR = "decoded_jpg"
DEPTH_PNG_DIR = "depth_png"
RAW_DIR = "raw"
INTRINSICS_FILE = "intrinsics.json"


# ---------------------------------------------------------------------------
# Reader API
# ---------------------------------------------------------------------------

class Session:
    """A SemanticXR debug-server session on disk.

    Construct from the session root path; methods return paths and loaded
    data for the layout above. Cheap to instantiate — intrinsics are loaded
    lazily and cached on first access.

    Example:
        s = Session("debug_output/session_3")
        for num in s.complete_frames():
            meta = s.load_meta(num)
            depth = np.load(s.depth_npy_path(num))
            ...
    """

    def __init__(self, root: Union[str, Path]):
        self.root = Path(root)
        self._intrinsics_cache: Optional[dict] = None

    # --- Subdirectory paths (computed; never mkdir'd by readers) ----------

    @property
    def meta_dir(self) -> Path:        return self.root / META_DIR
    @property
    def depth_dir(self) -> Path:       return self.root / DEPTH_DIR
    @property
    def jpg_dir(self) -> Path:         return self.root / JPG_DIR
    @property
    def depth_png_dir(self) -> Path:   return self.root / DEPTH_PNG_DIR
    @property
    def raw_dir(self) -> Path:         return self.root / RAW_DIR
    @property
    def intrinsics_path(self) -> Path: return self.root / INTRINSICS_FILE

    # --- Per-frame paths --------------------------------------------------

    def meta_path(self, frame: int) -> Path:
        return self.meta_dir / f"meta_{int(frame):06d}.json"

    def depth_npy_path(self, frame: int) -> Path:
        return self.depth_dir / f"depth_{int(frame):06d}.npy"

    def jpg_path(self, frame: int) -> Path:
        return self.jpg_dir / f"frame_{int(frame):06d}.jpg"

    def depth_png_path(self, frame: int) -> Path:
        return self.depth_png_dir / f"depth_{int(frame):06d}.png"

    def raw_h265_path(self, frame: int) -> Path:
        return self.raw_dir / f"frame_{int(frame):06d}.h265"

    def raw_depth_path(self, frame: int) -> Path:
        return self.raw_dir / f"depth_{int(frame):06d}.raw"

    # --- Listing ----------------------------------------------------------

    def list_depth_npys(self) -> List[Path]:
        return sorted(self.depth_dir.glob("depth_*.npy")) if self.depth_dir.is_dir() else []

    def list_meta_files(self) -> List[Path]:
        return sorted(self.meta_dir.glob("meta_*.json")) if self.meta_dir.is_dir() else []

    def list_raw_h265(self) -> List[Path]:
        return sorted(self.raw_dir.glob("frame_*.h265")) if self.raw_dir.is_dir() else []

    def list_raw_depth(self) -> List[Path]:
        return sorted(self.raw_dir.glob("depth_*.raw")) if self.raw_dir.is_dir() else []

    def frames_with_depth(self) -> List[int]:
        return [int(p.stem.split("_")[1]) for p in self.list_depth_npys()]

    def frames_with_meta(self) -> List[int]:
        return [int(p.stem.split("_")[1]) for p in self.list_meta_files()]

    def complete_frames(self) -> List[int]:
        """Frame numbers with all of: decoded JPG + depth NPY + meta JSON."""
        return [n for n in self.frames_with_depth()
                if self.meta_path(n).exists() and self.jpg_path(n).exists()]

    # --- Loaders ----------------------------------------------------------

    def load_intrinsics(self) -> Optional[dict]:
        """Scene-level ``intrinsics.json`` as a dict, or ``None`` if missing.

        Shape:
            {"convention": str,
             "rgb":   {"fx", "fy", "cx", "cy", "image_width", "image_height"},
             "depth": {"fx", "fy", "cx", "cy", "image_width", "image_height",
                       "depth_near_z", "depth_far_z", "fov_tangents"?}}
        """
        if self._intrinsics_cache is not None:
            return self._intrinsics_cache
        p = self.intrinsics_path
        if not p.exists():
            return None
        with open(p, "r") as f:
            self._intrinsics_cache = json.load(f)
        return self._intrinsics_cache

    def load_meta(self, frame_or_path: Union[int, np.integer, str, Path]) -> dict:
        """Per-frame meta as a flat dict (merged with scene-level intrinsics).

        ``frame_or_path`` may be an int frame number or a path to a meta JSON
        (the latter is convenient when iterating ``list_meta_files()``).

        Returned dict — kept stable across layout changes so downstream code
        can pin to these keys:
          * ``depth_pose_matrix``, ``rgb_camera_pose_matrix``,
            ``head_pose_matrix``, ``pose_matrix`` — 4×4 numpy float64 (c2w,
            RH, OpenGL/OpenXR). ``pose_matrix`` is the legacy alias of
            head_pose; identity fallback when neither is present.
          * ``intr_fx/fy/cx/cy``, ``depth_intr_fx/fy/cx/cy`` — float
            intrinsics (from scene-level ``intrinsics.json``; per-frame
            overrides used as fallback only).
          * ``image_size``, ``depth_size`` — string ``"WxH"`` (consumers
            split on ``x``).
          * ``sensor_near_z``, ``depth_inv_depth_factor``, ``depth_offset``
            — strings of floats (kept that way so ``float()`` in callers
            stays correct).
          * ``frame_number``, ``timestamp_ns/us``, ``rgb/depth/server_timestamp_ns``,
            ``h265_bytes``, ``depth_bytes``, ``rgb_decoded``,
            ``depth_decoded``, ``fps_setting`` — stringified scalars.
        """
        if isinstance(frame_or_path, (int, np.integer)):
            path = self.meta_path(int(frame_or_path))
        else:
            path = Path(frame_or_path)
        with open(path, "r") as f:
            frame = json.load(f)
        return _flatten_meta(frame, self.load_intrinsics())


def _flatten_meta(frame: dict, scene_intrinsics: Optional[dict]) -> dict:
    """Flatten a per-frame JSON + scene intrinsics into the consumer-facing dict.

    Kept private — callers go through ``Session.load_meta``. See that
    docstring for the dict shape.
    """
    meta: dict = {}

    # Pose matrices: stored as nested 4×4 lists in JSON.
    for name in ("depth_pose", "rgb_camera_pose", "head_pose"):
        v = frame.get(name)
        if v is not None and len(v) == 4 and len(v[0]) == 4:
            meta[f"{name}_matrix"] = np.array(v, dtype=np.float64)
    # Legacy ``pose`` alias of head_pose; identity fallback so consumers that
    # always read ``pose_matrix`` keep working when head_pose is absent.
    if "head_pose_matrix" in meta:
        meta["pose_matrix"] = meta["head_pose_matrix"]
    else:
        meta["pose_matrix"] = np.eye(4)

    # Scalar fields kept as strings — many existing call sites do
    # ``float(meta[k])`` or ``int(meta[k])`` and depend on this shape.
    for k in ("frame_number", "timestamp_ns", "timestamp_us",
              "rgb_timestamp_ns", "depth_timestamp_ns", "server_timestamp_ns",
              "h265_bytes", "depth_bytes", "rgb_decoded", "depth_decoded",
              "fps_setting", "depth_format",
              "depth_inv_depth_factor", "depth_offset", "sensor_near_z"):
        if k in frame:
            meta[k] = str(frame[k])

    # Sizes serialized as "WxH" so callers keep using ``meta["depth_size"].split("x")``.
    if "image_width" in frame and "image_height" in frame:
        meta["image_size"] = f"{frame['image_width']}x{frame['image_height']}"
    if "depth_width" in frame and "depth_height" in frame:
        meta["depth_size"] = f"{frame['depth_width']}x{frame['depth_height']}"

    # Intrinsics: scene-level ``intrinsics.json`` is canonical; per-frame
    # ``intrinsics``/``depth_intrinsics`` are fallbacks only.
    rgb_intr = (scene_intrinsics or {}).get("rgb") or frame.get("intrinsics")
    depth_intr = (scene_intrinsics or {}).get("depth") or frame.get("depth_intrinsics")
    if rgb_intr:
        for k in ("fx", "fy", "cx", "cy"):
            if k in rgb_intr:
                meta[f"intr_{k}"] = float(rgb_intr[k])
    if depth_intr:
        for k in ("fx", "fy", "cx", "cy"):
            if k in depth_intr:
                meta[f"depth_intr_{k}"] = float(depth_intr[k])

    return meta


# ---------------------------------------------------------------------------
# Writer API
#
# Used by both server scripts (gRPC + TCP). The proto-aware bits live here
# because the field names are part of the on-disk schema — keeping read and
# write next to each other prevents schema drift.
# ---------------------------------------------------------------------------

_INTRINSICS_CONVENTION_NOTE = (
    "Poses are camera-to-world, right-handed, X-right Y-up Z-back "
    "(OpenGL/OpenXR after Unity LH->RH flip on client). "
    "Depth texture is sent top-down (client pre-flipped on encode), so cx/cy "
    "are top-down image principal points (cy from image top). NOTE: "
    "semantic-slam-server's Quest layout writes the same value under the key "
    "'cy_yup' (Y-up viewport, from image bottom)."
)


def _pose_or_none(values) -> Optional[List[List[float]]]:
    """Return a 4×4 nested-list pose, or None if the proto field was unset."""
    v = list(values)
    if len(v) < 16:
        return None
    return [v[r * 4:(r + 1) * 4] for r in range(4)]


def ensure_session_dirs(root: Union[str, Path], *, decoded: bool) -> None:
    """Create the subdirs a session needs.

    ``meta/`` and ``raw/`` are always created (always-on outputs).
    ``decoded_jpg/``, ``depth/``, ``depth_png/`` are created only when
    ``decoded=True`` — i.e. when the server is running with ``--decode`` and
    will produce them inline. ``decode_session.py`` calls this with
    ``decoded=True`` after-the-fact.
    """
    root = Path(root)
    (root / META_DIR).mkdir(parents=True, exist_ok=True)
    (root / RAW_DIR).mkdir(parents=True, exist_ok=True)
    if decoded:
        (root / JPG_DIR).mkdir(parents=True, exist_ok=True)
        (root / DEPTH_DIR).mkdir(parents=True, exist_ok=True)
        (root / DEPTH_PNG_DIR).mkdir(parents=True, exist_ok=True)


def write_intrinsics_once(session_root: Union[str, Path], msg) -> None:
    """Write scene-level ``intrinsics.json`` if it doesn't already exist.

    Idempotent — call on every frame; only the first call writes. Format
    mirrors semantic-slam-server's Quest layout
    (``slam.datasets.quest.load_quest_intrinsics``) but uses the bare key
    ``cy`` instead of ``cy_yup`` to reflect the debug-server top-down
    convention. The ``convention`` string documents the difference.
    """
    out = Path(session_root) / INTRINSICS_FILE
    if out.exists():
        return

    intr = msg.intrinsics
    dintr = msg.depth_intrinsics
    fov_tan = list(msg.depth_fov_tangents)

    rgb = {
        "fx": float(intr.fx), "fy": float(intr.fy),
        "cx": float(intr.cx), "cy": float(intr.cy),
        "image_width": int(msg.image_width),
        "image_height": int(msg.image_height),
    }
    depth = {
        "image_width": int(msg.depth_width),
        "image_height": int(msg.depth_height),
        "depth_near_z": float(msg.depth_near_z),
        "depth_far_z": float(msg.depth_far_z),
    }
    if dintr and (dintr.fx > 0 or dintr.fy > 0):
        depth.update({
            "fx": float(dintr.fx), "fy": float(dintr.fy),
            "cx": float(dintr.cx), "cy": float(dintr.cy),
        })
    if len(fov_tan) >= 4:
        depth["fov_tangents"] = {
            "L": float(fov_tan[0]), "R": float(fov_tan[1]),
            "T": float(fov_tan[2]), "D": float(fov_tan[3]),
        }

    with open(out, "w") as f:
        json.dump({
            "convention": _INTRINSICS_CONVENTION_NOTE,
            "rgb": rgb,
            "depth": depth,
        }, f, indent=2)


def write_meta(session_root: Union[str, Path], msg, h265_data: bytes,
               depth_data: bytes, sensor_near_z: float,
               inv_depth_factor: float, depth_offset: float,
               *, depth_decoded: bool, rgb_decoded: bool) -> None:
    """Write ``meta/meta_NNNNNN.json`` for one frame.

    Superset of semantic-slam-server's per-frame meta (frame_number,
    rgb_camera_pose, depth_pose, rgb/depth/server timestamps): adds head_pose,
    raw byte counts, decode flags, and depth-conversion params so
    ``decode_session.py`` can re-derive everything offline.

    Pose policy: stored AS RECEIVED from the proto. The legacy ``pose`` proto
    field is treated as an alias of ``head_pose`` and only used as a fallback.
    """
    img = msg.image
    frame_num = int(img.frame_number)

    rgb_intr = {
        "fx": float(msg.intrinsics.fx), "fy": float(msg.intrinsics.fy),
        "cx": float(msg.intrinsics.cx), "cy": float(msg.intrinsics.cy),
    }
    dintr = msg.depth_intrinsics
    depth_intr = None
    if dintr and (dintr.fx > 0 or dintr.fy > 0):
        depth_intr = {
            "fx": float(dintr.fx), "fy": float(dintr.fy),
            "cx": float(dintr.cx), "cy": float(dintr.cy),
        }

    doc = {
        "frame_number": frame_num,
        "timestamp_us": int(img.timestamp_us),
        "timestamp_ns": int(msg.timestamp_ns),
        "rgb_timestamp_ns": int(msg.rgb_timestamp_ns),
        "depth_timestamp_ns": int(msg.depth_timestamp_ns),
        "server_timestamp_ns": _now_ns(),
        "image_width": int(msg.image_width),
        "image_height": int(msg.image_height),
        "depth_width": int(msg.depth_width),
        "depth_height": int(msg.depth_height),
        "depth_format": "R16_SFloat",
        "depth_inv_depth_factor": float(inv_depth_factor),
        "depth_offset": float(depth_offset),
        "sensor_near_z": float(sensor_near_z),
        "h265_bytes": int(len(h265_data)),
        "depth_bytes": int(len(depth_data)),
        "rgb_decoded": bool(rgb_decoded),
        "depth_decoded": bool(depth_decoded),
        "fps_setting": int(msg.fps),
        "intrinsics": rgb_intr,
    }
    if depth_intr is not None:
        doc["depth_intrinsics"] = depth_intr
    fov_tan = list(msg.depth_fov_tangents)
    if len(fov_tan) >= 4:
        doc["depth_fov_tangents"] = {
            "L": float(fov_tan[0]), "R": float(fov_tan[1]),
            "T": float(fov_tan[2]), "D": float(fov_tan[3]),
        }

    depth_pose = _pose_or_none(msg.depth_pose)
    rgb_camera_pose = _pose_or_none(msg.rgb_camera_pose)
    head_pose = _pose_or_none(msg.head_pose) or _pose_or_none(msg.pose)
    if depth_pose is not None:
        doc["depth_pose"] = depth_pose
    if rgb_camera_pose is not None:
        doc["rgb_camera_pose"] = rgb_camera_pose
    if head_pose is not None:
        doc["head_pose"] = head_pose

    out = Path(session_root) / META_DIR / f"meta_{frame_num:06d}.json"
    with open(out, "w") as f:
        json.dump(doc, f)


def _now_ns() -> int:
    """Wall-clock-ish monotonic ns for server arrival timestamps."""
    import time
    return time.perf_counter_ns()
