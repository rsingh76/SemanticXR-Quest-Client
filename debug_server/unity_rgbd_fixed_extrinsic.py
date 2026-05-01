r"""
Fixed-extrinsic reprojection: treat depth↔RGB as a calibrated stereo rig.

Why this file:
  Our earlier pipeline did depth-pixel -> world (via depth_pose) ->
  RGB pixel (via rgb_pose). That implicitly trusts TWO independent pose
  streams to share a perfectly consistent world frame. Per-frame analysis
  showed they don't — translation std ~3 mm, rotation std ~0.5°, enough
  jitter to cause visible color misalignment across frames.

  Fix: compute ONE fixed transform T_rgb<-depth = T_rgb^-1 @ T_depth across
  the session (robust average), and use it directly. No more shared world
  frame for the sampling step.

Pipeline (per frame):
  1. Unproject depth pixel into depth CAMERA-LOCAL coords (no pose).
  2. Apply the fixed T_rgb<-depth to get the point in RGB CAMERA-LOCAL coords.
  3. Project with RGB intrinsics.
  4. Sample RGB color at that pixel.
  5. Attach color to the WORLD position (computed via depth_pose, for output).

Usage:
    python unity_rgbd_fixed_extrinsic.py debug_output/session_XXXX
    python unity_rgbd_fixed_extrinsic.py debug_output/session_XXXX --save-per-frame 3
"""
import argparse
import os
os.environ.setdefault("OPEN3D_CPU_RENDERING", "true")

from pathlib import Path
import numpy as np
import open3d as o3d
from PIL import Image

from session_io import Session


def _quat_from_matrix(R):
    """3x3 -> quaternion (w, x, y, z)."""
    trace = np.trace(R)
    if trace > 0:
        s = np.sqrt(trace + 1.0) * 2
        w = 0.25 * s
        x = (R[2, 1] - R[1, 2]) / s
        y = (R[0, 2] - R[2, 0]) / s
        z = (R[1, 0] - R[0, 1]) / s
    elif R[0, 0] > R[1, 1] and R[0, 0] > R[2, 2]:
        s = np.sqrt(1.0 + R[0, 0] - R[1, 1] - R[2, 2]) * 2
        w = (R[2, 1] - R[1, 2]) / s
        x = 0.25 * s
        y = (R[0, 1] + R[1, 0]) / s
        z = (R[0, 2] + R[2, 0]) / s
    elif R[1, 1] > R[2, 2]:
        s = np.sqrt(1.0 + R[1, 1] - R[0, 0] - R[2, 2]) * 2
        w = (R[0, 2] - R[2, 0]) / s
        x = (R[0, 1] + R[1, 0]) / s
        y = 0.25 * s
        z = (R[1, 2] + R[2, 1]) / s
    else:
        s = np.sqrt(1.0 + R[2, 2] - R[0, 0] - R[1, 1]) * 2
        w = (R[1, 0] - R[0, 1]) / s
        x = (R[0, 2] + R[2, 0]) / s
        y = (R[1, 2] + R[2, 1]) / s
        z = 0.25 * s
    return np.array([w, x, y, z])


def _matrix_from_quat(q):
    w, x, y, z = q / np.linalg.norm(q)
    return np.array([
        [1 - 2 * (y*y + z*z), 2 * (x*y - z*w),     2 * (x*z + y*w)],
        [2 * (x*y + z*w),     1 - 2 * (x*x + z*z), 2 * (y*z - x*w)],
        [2 * (x*z - y*w),     2 * (y*z + x*w),     1 - 2 * (x*x + y*y)],
    ])


def robust_mean_transform(transforms):
    """Given a list of 4x4 T_rel matrices, return a single robust-mean 4x4.
    Translation: median per component. Rotation: quaternion average,
    sign-aligned to the first quaternion.
    """
    ts = np.array([T[:3, 3] for T in transforms])
    # median per axis is more outlier-robust than mean
    t_mean = np.median(ts, axis=0)

    qs = np.array([_quat_from_matrix(T[:3, :3]) for T in transforms])
    # Sign-align quaternions to q[0] (double-cover ambiguity)
    signs = np.sign(qs @ qs[0])
    signs[signs == 0] = 1
    qs = qs * signs[:, None]
    q_mean = qs.mean(axis=0)
    q_mean /= np.linalg.norm(q_mean)
    R_mean = _matrix_from_quat(q_mean)

    T = np.eye(4)
    T[:3, :3] = R_mean
    T[:3, 3] = t_mean
    return T


def compute_fixed_extrinsic(session: Session, frames):
    """Estimate a single depth->rgb local-frame transform across the session."""
    transforms = []
    for num in frames:
        m = session.load_meta(num)
        dp = m.get("depth_pose_matrix")
        rp = m.get("rgb_camera_pose_matrix")
        if dp is None or rp is None:
            continue
        if abs(np.linalg.det(dp[:3, :3])) < 0.5 or \
           abs(np.linalg.det(rp[:3, :3])) < 0.5:
            continue
        transforms.append(np.linalg.inv(rp) @ dp)
    if not transforms:
        raise RuntimeError("No valid transforms to average")
    T = robust_mean_transform(transforms)
    return T, len(transforms)


def unproject_depth_to_cam_local(depth_img, d_fx, d_fy, d_cx, d_cy):
    """Depth image (top-down) -> points in depth-camera-LOCAL coords.
    Camera convention: +X right, +Y up, -Z forward (OpenGL, same as the
    stored poses after LhToRh).

    Returns: pts_cam_local (dh, dw, 3), valid (dh, dw).
    """
    dh, dw = depth_img.shape
    uu, vv = np.meshgrid(np.arange(dw), np.arange(dh))
    d = depth_img.astype(np.float32)
    valid = d > 0.05
    x_l = (uu - d_cx) / d_fx * d
    y_l = (d_cy - vv) / d_fy * d
    z_l = -d
    return np.stack([x_l, y_l, z_l], axis=-1), valid


def project_rgb_cam_local_to_pixel(pts_rgb_local, fx, fy, cx, cy, img_w, img_h):
    """Points in RGB camera-LOCAL coords -> jpeg pixel indices + forward dist.

    Same conventions as project_world_to_rgb in unity_rgbd_reconstruct.py:
    camera +Y up, -Z forward, cy stored Y-up from bottom, JPEG top-down.
    """
    flat = pts_rgb_local.reshape(-1, 3)
    z_fwd = -flat[:, 2]
    safe_z = np.where(z_fwd > 0.1, z_fwd, 1e-6)
    u = fx * flat[:, 0] / safe_z + cx
    v_sensor = fy * flat[:, 1] / safe_z + cy
    v_jpeg = img_h - v_sensor
    return (u.reshape(pts_rgb_local.shape[:2]),
            v_jpeg.reshape(pts_rgb_local.shape[:2]),
            z_fwd.reshape(pts_rgb_local.shape[:2]))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("session_dir", nargs="?", default=None)
    ap.add_argument("--save-per-frame", type=int, default=0, metavar="N",
                    help="per-frame colored cloud dumps (for visual check)")
    ap.add_argument("--frames-range", type=str, default=None, metavar="START:END")
    args = ap.parse_args()

    if args.session_dir is None:
        sessions = sorted(Path("debug_output").glob("session_*"))
        if not sessions:
            print("No sessions"); return
        args.session_dir = str(sessions[-1])
    session = Session(args.session_dir)
    sd = session.root

    frames = session.complete_frames()
    if args.frames_range:
        lo, hi = (int(x) for x in args.frames_range.split(":"))
        frames = [n for n in frames if lo <= n <= hi]
    print(f"Session: {sd.name}  frames: {len(frames)}")

    # Estimate fixed extrinsic
    T_rgb_from_depth, n_used = compute_fixed_extrinsic(session, frames)
    print(f"Fixed T_rgb<-depth from {n_used} frames:")
    print(f"  translation (m): {T_rgb_from_depth[:3, 3]}")
    print(f"  rotation (upper 3x3):")
    for row in T_rgb_from_depth[:3, :3]:
        print(f"    {row}")

    # Per-frame pick
    if args.save_per_frame > 0:
        step = max(1, len(frames) // args.save_per_frame)
        per_set = set(frames[::step][:args.save_per_frame])
        per_dir = sd / "debug" / "per_frame_fixed_extr"
        per_dir.mkdir(parents=True, exist_ok=True)
        print(f"Per-frame dumps: {sorted(per_set)}")
    else:
        per_set = set(); per_dir = None

    all_pts, all_cols = [], []
    skipped = 0
    for i, num in enumerate(frames):
        meta = session.load_meta(num)
        d_pose = meta.get("depth_pose_matrix")
        if d_pose is None or abs(np.linalg.det(d_pose[:3, :3])) < 0.5:
            skipped += 1
            continue

        depth = np.load(session.depth_npy_path(num)).astype(np.float32)
        rgb = np.array(Image.open(session.jpg_path(num)).convert("RGB"))
        img_h, img_w = rgb.shape[:2]

        d_fx = meta["depth_intr_fx"]; d_fy = meta["depth_intr_fy"]
        d_cx = meta["depth_intr_cx"]; d_cy = meta["depth_intr_cy"]
        fx = meta["intr_fx"]; fy = meta["intr_fy"]
        cx = meta["intr_cx"]; cy = meta["intr_cy"]

        # Step 1: unproject to depth camera local coords (no world pose)
        pts_d_local, valid_d = unproject_depth_to_cam_local(
            depth, d_fx, d_fy, d_cx, d_cy)

        # Step 2: apply fixed rig transform -> RGB camera local coords
        pts_d_flat = pts_d_local.reshape(-1, 3)
        pts_rgb_flat = (T_rgb_from_depth[:3, :3] @ pts_d_flat.T).T + \
                       T_rgb_from_depth[:3, 3]
        pts_rgb_local = pts_rgb_flat.reshape(pts_d_local.shape)

        # Step 3: project with RGB intrinsics
        ju, jv, zf = project_rgb_cam_local_to_pixel(
            pts_rgb_local, fx, fy, cx, cy, img_w, img_h)

        valid = (valid_d & (zf > 0.2) &
                 (ju >= 0) & (ju < img_w) &
                 (jv >= 0) & (jv < img_h))

        # Step 4: sample RGB
        iu = np.clip(ju, 0, img_w - 1).astype(np.int32)
        iv = np.clip(jv, 0, img_h - 1).astype(np.int32)
        cols = rgb[iv[valid], iu[valid]].astype(np.float32) / 255.0

        # Step 5: world positions via depth_pose (unchanged)
        pts_world_flat = (d_pose[:3, :3] @ pts_d_flat.T).T + d_pose[:3, 3]
        pts_world = pts_world_flat.reshape(pts_d_local.shape)
        xyz = pts_world[valid]

        if xyz.size:
            all_pts.append(xyz)
            all_cols.append(cols)

        if num in per_set and xyz.size:
            pc = o3d.geometry.PointCloud()
            pc.points = o3d.utility.Vector3dVector(xyz)
            pc.colors = o3d.utility.Vector3dVector(cols)
            o3d.io.write_point_cloud(str(per_dir / f"frame_{num:06d}_fixed.ply"),
                                      pc)
            Image.fromarray(rgb).save(per_dir / f"frame_{num:06d}_rgb.png")
            print(f"  per-frame {num}: {len(xyz)} pts")

        if (i + 1) % 10 == 0 or i == 0 or i == len(frames) - 1:
            print(f"  [{i+1}/{len(frames)}] frame {num}: {len(xyz)} pts")

    if skipped:
        print(f"Skipped {skipped} bad-pose frames")

    if all_pts:
        pts_all = np.concatenate(all_pts, axis=0)
        cols_all = np.concatenate(all_cols, axis=0)
        pc = o3d.geometry.PointCloud()
        pc.points = o3d.utility.Vector3dVector(pts_all)
        pc.colors = o3d.utility.Vector3dVector(cols_all)
        out = sd / "rgbd_fixed_extrinsic_cloud.ply"
        o3d.io.write_point_cloud(str(out), pc)
        print(f"\n{len(pc.points)} points -> {out}")


if __name__ == "__main__":
    main()
