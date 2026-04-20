r"""
RGB raycasting reconstruction.

Per frame:
  1. Build a ray from the RGB camera through every pixel (or every Nth via --stride).
  2. Cast rays against the TSDF mesh (from unity_reconstruct.py).
  3. Each hit point gets the RGB color at the originating pixel.

This produces a colored point cloud that respects the RGB camera's visibility
by construction: if a ray hits a foreground object first, we color the
foreground — the wall behind it is not touched. No parallax bleed.

Outputs:
  rgbd_raycast_cloud.ply        — accumulated colored point cloud
  debug/per_frame_raycast/      — per-frame colored clouds + origin PNGs
  debug/raycast_coverage.png    — which pixels of one frame produced hits

For object detection later, the same math:
  pixel_to_world(u, v, rgb_pose, rgb_intr, scene)
returns the world XYZ of the 2D detection.

Usage:
    python unity_rgbd_raycast.py debug_output/session_XXXX
    python unity_rgbd_raycast.py debug_output/session_XXXX --stride 2 --frames-range 28:40
    python unity_rgbd_raycast.py debug_output/session_XXXX --save-per-frame 5
"""
import argparse
import os
os.environ.setdefault("OPEN3D_CPU_RENDERING", "true")

from pathlib import Path
import numpy as np
import open3d as o3d
from PIL import Image

from reconstruct_tsdf import parse_metadata


def build_rays_for_frame(rgb_pose, fx, fy, cx, cy, img_w, img_h, stride=1):
    """For every stride'th pixel of a top-down RGB image at this resolution,
    compute world-space (origin, direction) rays via OpenGL convention:
      camera local: +X right, +Y up, -Z forward
      cy stored Y-up from bottom; stored image is top-down, so
      v_sensor_yup = img_h - v_jpeg.

    Returns (rays Nx6 float32, row_idx N int32, col_idx N int32).
    """
    vs = np.arange(0, img_h, stride)
    us = np.arange(0, img_w, stride)
    uu, vv = np.meshgrid(us, vs)
    v_sensor = img_h - vv                # Y-up from bottom
    rx = (uu - cx) / fx
    ry = (v_sensor - cy) / fy
    rz = -np.ones_like(rx)               # -Z = forward (OpenGL)
    dirs_local = np.stack([rx, ry, rz], axis=-1).reshape(-1, 3)

    R = rgb_pose[:3, :3]
    t = rgb_pose[:3, 3]
    dirs_world = dirs_local @ R.T
    norms = np.linalg.norm(dirs_world, axis=1, keepdims=True)
    dirs_norm = dirs_world / np.maximum(norms, 1e-9)

    origins = np.tile(t, (len(dirs_norm), 1)).astype(np.float32)
    rays = np.concatenate([origins, dirs_norm.astype(np.float32)], axis=1)
    return rays, vv.reshape(-1).astype(np.int32), uu.reshape(-1).astype(np.int32)


def raycast_frame(scene, rgb, rgb_pose, rgb_intr, stride=1, max_hit_dist=8.0):
    """Returns (hit_xyz Nx3 float32, hit_rgb Nx3 uint8) for valid hits from this
    frame's RGB camera."""
    img_h, img_w = rgb.shape[:2]
    fx, fy, cx, cy = rgb_intr
    rays, row_idx, col_idx = build_rays_for_frame(
        rgb_pose, fx, fy, cx, cy, img_w, img_h, stride=stride)
    results = scene.cast_rays(o3d.core.Tensor(rays))
    t_hit = results["t_hit"].numpy()
    valid = np.isfinite(t_hit) & (t_hit < max_hit_dist) & (t_hit > 0.05)

    origins = rays[valid, :3]
    dirs = rays[valid, 3:]
    hits_xyz = origins + t_hit[valid, None] * dirs
    hits_rgb = rgb[row_idx[valid], col_idx[valid]]
    return hits_xyz.astype(np.float32), hits_rgb, row_idx[valid], col_idx[valid]


def pixel_to_world(u, v, rgb_pose, rgb_intr, img_w, img_h, scene, max_dist=8.0):
    """Single-pixel variant for downstream use (e.g., object detection).

    Returns world XYZ (np.ndarray shape (3,)) or None if the ray misses.
    """
    fx, fy, cx, cy = rgb_intr
    v_sensor = img_h - v
    dir_local = np.array([(u - cx) / fx, (v_sensor - cy) / fy, -1.0])
    dir_world = rgb_pose[:3, :3] @ dir_local
    dir_world /= np.linalg.norm(dir_world)
    origin = rgb_pose[:3, 3]
    ray = np.concatenate([origin, dir_world]).astype(np.float32)[None, :]
    t_hit = scene.cast_rays(o3d.core.Tensor(ray))["t_hit"].numpy()[0]
    if not np.isfinite(t_hit) or t_hit > max_dist or t_hit <= 0.05:
        return None
    return origin + t_hit * dir_world


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("session_dir", nargs="?", default=None)
    ap.add_argument("--mesh", type=str, default=None,
                    help="mesh file (default: <session>/unity_mesh.ply)")
    ap.add_argument("--stride", type=int, default=4,
                    help="subsample RGB pixels by this stride (default 4 = 1/16 pixels)")
    ap.add_argument("--max-hit-dist", type=float, default=8.0)
    ap.add_argument("--frames-range", type=str, default=None, metavar="START:END")
    ap.add_argument("--save-per-frame", type=int, default=0, metavar="N",
                    help="dump per-frame raycast ply + coverage png for N "
                         "evenly-spaced frames into debug/per_frame_raycast/")
    ap.add_argument("--no-aggregate", action="store_true",
                    help="skip writing the combined cloud (per-frame only)")
    args = ap.parse_args()

    if args.session_dir is None:
        sessions = sorted(Path("debug_output").glob("session_*"))
        if not sessions:
            print("No sessions"); return
        args.session_dir = str(sessions[-1])
    sd = Path(args.session_dir)
    mesh_path = Path(args.mesh) if args.mesh else sd / "unity_mesh.ply"
    if not mesh_path.exists():
        print(f"Mesh not found: {mesh_path} — run unity_reconstruct.py first")
        return

    print(f"Session: {sd.name}")
    print(f"Mesh:    {mesh_path.name}")

    # Load mesh into raycasting scene
    mesh = o3d.io.read_triangle_mesh(str(mesh_path))
    t_mesh = o3d.t.geometry.TriangleMesh.from_legacy(mesh)
    scene = o3d.t.geometry.RaycastingScene()
    scene.add_triangles(t_mesh)
    print(f"Mesh loaded: {len(mesh.vertices)} verts, "
          f"{len(mesh.triangles)} tris")

    # Find frames
    jpg_dir = sd / "decoded_jpg"
    depth_files = sorted(sd.glob("depth_*.npy"))  # use for frame-num enumeration
    frames = []
    for df in depth_files:
        num = int(df.stem.split("_")[1])
        if (jpg_dir / f"frame_{num:06d}.jpg").exists() and \
           (sd / f"meta_{num:06d}.txt").exists():
            frames.append(num)
    if args.frames_range:
        lo, hi = (int(x) for x in args.frames_range.split(":"))
        frames = [n for n in frames if lo <= n <= hi]
    print(f"Frames: {len(frames)} (stride {args.stride})")

    # Per-frame pick
    if args.save_per_frame > 0:
        step = max(1, len(frames) // args.save_per_frame)
        per_set = set(frames[::step][:args.save_per_frame])
        per_dir = sd / "debug" / "per_frame_raycast"
        per_dir.mkdir(parents=True, exist_ok=True)
        print(f"Per-frame raycast dumps: {sorted(per_set)}")
    else:
        per_set = set(); per_dir = None

    all_pts, all_cols = [], []
    skipped = 0
    for i, num in enumerate(frames):
        meta = parse_metadata(sd / f"meta_{num:06d}.txt")
        rgb_pose = meta.get("rgb_camera_pose_matrix")
        if rgb_pose is None or abs(np.linalg.det(rgb_pose[:3, :3])) < 0.5:
            skipped += 1
            continue
        rgb = np.array(Image.open(jpg_dir / f"frame_{num:06d}.jpg").convert("RGB"))
        img_h, img_w = rgb.shape[:2]
        rgb_intr = (meta["intr_fx"], meta["intr_fy"],
                    meta["intr_cx"], meta["intr_cy"])
        xyz, cols, rows_hit, cols_hit = raycast_frame(
            scene, rgb, rgb_pose, rgb_intr,
            stride=args.stride, max_hit_dist=args.max_hit_dist)

        if xyz.size:
            all_pts.append(xyz)
            all_cols.append(cols.astype(np.float32) / 255.0)

        if num in per_set and xyz.size:
            pc = o3d.geometry.PointCloud()
            pc.points = o3d.utility.Vector3dVector(xyz)
            pc.colors = o3d.utility.Vector3dVector(cols.astype(np.float32) / 255.0)
            o3d.io.write_point_cloud(str(per_dir / f"frame_{num:06d}_raycast.ply"),
                                      pc)
            # Coverage mask — which RGB pixels got a hit
            cov = np.zeros((img_h, img_w), dtype=np.uint8)
            cov[rows_hit, cols_hit] = 255
            Image.fromarray(cov).save(per_dir / f"frame_{num:06d}_coverage.png")
            # Also save the original RGB for cross-reference
            Image.fromarray(rgb).save(per_dir / f"frame_{num:06d}_rgb.png")

        if (i + 1) % 10 == 0 or i == 0 or i == len(frames) - 1:
            print(f"  [{i+1}/{len(frames)}] frame {num}: "
                  f"{len(xyz)} hits")

    if skipped:
        print(f"Skipped {skipped} frames (bad pose)")

    if not args.no_aggregate and all_pts:
        pts_all = np.concatenate(all_pts, axis=0)
        cols_all = np.concatenate(all_cols, axis=0)
        pc = o3d.geometry.PointCloud()
        pc.points = o3d.utility.Vector3dVector(pts_all)
        pc.colors = o3d.utility.Vector3dVector(cols_all)
        out = sd / "rgbd_raycast_cloud.ply"
        o3d.io.write_point_cloud(str(out), pc)
        print(f"\n{len(pc.points)} points -> {out}")


if __name__ == "__main__":
    main()
