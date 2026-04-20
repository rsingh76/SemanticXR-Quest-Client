r"""
Clean depth-only TSDF reconstruction for unity_server.py data.

Conventions:
  - Depth .npy on disk: top-down (unity_server.py flips on decode, matching
    cy = tanT * fy from the top).
  - Depth pose: Unity world space (trackingToWorld applied on client), then
    LhToRh on wire => right-handed, OpenGL camera (X-right, Y-up, Z-back).
  - flip = diag(1,-1,-1,1) converts that to OpenCV (X-right, Y-down, Z-forward)
    for Open3D's extrinsic.

Usage:
    python unity_reconstruct.py debug_output/session_XXXX
"""

import os
os.environ.setdefault("OPEN3D_CPU_RENDERING", "true")

import argparse
from pathlib import Path
import numpy as np
import open3d as o3d

from reconstruct_tsdf import parse_metadata


def build_tsdf(session_dir, voxel=0.01, max_depth=5.0):
    session_dir = Path(session_dir)

    depth_files = sorted(session_dir.glob("depth_*.npy"))
    frame_nums = []
    for df in depth_files:
        num = int(df.stem.split("_")[1])
        if (session_dir / f"meta_{num:06d}.txt").exists():
            frame_nums.append(num)

    if not frame_nums:
        print("No frames found")
        return None

    first_meta = parse_metadata(session_dir / f"meta_{frame_nums[0]:06d}.txt")
    first_depth = np.load(session_dir / f"depth_{frame_nums[0]:06d}.npy")
    dh, dw = first_depth.shape

    # Depth intrinsics from meta (computed from FOV tangents on client)
    if "depth_intr_fx" in first_meta and first_meta["depth_intr_fx"] > 0:
        dfx = first_meta["depth_intr_fx"]
        dfy = first_meta["depth_intr_fy"]
        dcx = first_meta["depth_intr_cx"]
        dcy = first_meta["depth_intr_cy"]
    else:
        dfx, dfy, dcx, dcy = dw / 2, dh / 2, dw / 2, dh / 2

    print(f"Depth: {dw}x{dh}, fx={dfx:.1f} fy={dfy:.1f} cx={dcx:.1f} cy={dcy:.1f}")
    print(f"TSDF: voxel={voxel}m, max_depth={max_depth}m")
    print(f"Frames: {len(frame_nums)}")

    # Open3D expects OpenCV convention extrinsic
    flip = np.diag([1.0, -1.0, -1.0, 1.0])
    intrinsic = o3d.camera.PinholeCameraIntrinsic(dw, dh, dfx, dfy, dcx, dcy)

    volume = o3d.pipelines.integration.ScalableTSDFVolume(
        voxel_length=voxel,
        sdf_trunc=voxel * 5.0,
        color_type=o3d.pipelines.integration.TSDFVolumeColorType.NoColor,
    )

    skipped = 0
    for i, num in enumerate(frame_nums):
        meta = parse_metadata(session_dir / f"meta_{num:06d}.txt")
        depth_metric = np.load(session_dir / f"depth_{num:06d}.npy").astype(np.float32)

        if "depth_pose_matrix" in meta:
            pose_c2w = meta["depth_pose_matrix"]
        elif "head_pose_matrix" in meta:
            pose_c2w = meta["head_pose_matrix"]
        else:
            pose_c2w = meta["pose_matrix"]

        det = np.linalg.det(pose_c2w[:3, :3])
        if abs(det) < 0.5 or abs(det) > 2.0:
            skipped += 1
            continue

        extrinsic = flip @ np.linalg.inv(pose_c2w)

        depth_metric[depth_metric <= 0.05] = 0.0
        depth_metric[depth_metric > max_depth] = 0.0

        depth_o3d = o3d.geometry.Image(np.ascontiguousarray(depth_metric))
        dummy = np.full((dh, dw, 3), 128, dtype=np.uint8)
        color_o3d = o3d.geometry.Image(np.ascontiguousarray(dummy))

        rgbd = o3d.geometry.RGBDImage.create_from_color_and_depth(
            color_o3d, depth_o3d, depth_scale=1.0, depth_trunc=max_depth,
            convert_rgb_to_intensity=False)
        volume.integrate(rgbd, intrinsic, extrinsic)

        if (i + 1) % 10 == 0 or i == 0 or i == len(frame_nums) - 1:
            print(f"  [{i+1}/{len(frame_nums)}] frame {num}")

    if skipped:
        print(f"Skipped {skipped} degenerate poses")

    mesh = volume.extract_triangle_mesh()
    mesh.compute_vertex_normals()

    if len(mesh.triangles) > 100:
        tc, cn, _ = mesh.cluster_connected_triangles()
        tc, cn = np.asarray(tc), np.asarray(cn)
        keep = cn[tc] >= max(50, int(len(mesh.triangles) * 0.01))
        mesh.remove_triangles_by_mask(~keep)
        mesh.remove_unreferenced_vertices()

    out = session_dir / "unity_mesh.ply"
    o3d.io.write_triangle_mesh(str(out), mesh)
    print(f"\n{len(mesh.vertices)} verts -> {out}")
    return out


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("session_dir", nargs="?", default=None)
    parser.add_argument("--voxel", type=float, default=0.01)
    parser.add_argument("--max-depth", type=float, default=5.0)
    args = parser.parse_args()

    if args.session_dir is None:
        sessions = sorted(Path("debug_output").glob("session_*"))
        if not sessions:
            print("No sessions")
            return
        args.session_dir = str(sessions[-1])

    build_tsdf(args.session_dir, voxel=args.voxel, max_depth=args.max_depth)


if __name__ == "__main__":
    main()
