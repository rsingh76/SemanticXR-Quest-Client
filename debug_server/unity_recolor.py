r"""
Clean recoloring co-designed with client + unity_server.py.

Pipeline assumptions:
  Client:
    - GetColors() returns top-down image (Quest 3S/Vulkan, same as Meta sample GetTexture)
    - NV12 conversion: NO vertical flip (removed in fix/unity-recolor)
    - Intrinsics: PrincipalPoint in sensor/viewport convention (Y increases upward)
    - RGB pose: Unity world space, LhToRh on wire

  unity_server.py:
    - H.265 decoded JPEG: top-down (right-side-up)
    - Depth .npy: top-down (no [::-1] flip)

  Projection (confirmed 0.00px on-device sanity test):
    - p_local = R^T @ (vertex - cam_pos)  [RH camera local]
    - z_fwd = -p_local[2]  [negative Z for in-front after LhToRh]
    - sensor_u = fx * p_local[0] / z_fwd + cx
    - sensor_v = fy * p_local[1] / z_fwd + cy  [viewport: Y-up]
    - jpeg_y = img_h - sensor_v  [JPEG: Y-down]

Usage:
    python unity_recolor.py session_dir
    python unity_recolor.py session_dir --mesh unity_mesh.ply --frame 30
"""

import os
os.environ.setdefault("OPEN3D_CPU_RENDERING", "true")

import argparse
from pathlib import Path
import numpy as np
from PIL import Image
import open3d as o3d

from reconstruct_tsdf import parse_metadata


def project_vertices(verts, pose_c2w_rh, fx, fy, cx, cy, img_h):
    """Project vertices to JPEG pixel coords. Returns (uv, z_fwd)."""
    R = pose_c2w_rh[:3, :3]
    t = pose_c2w_rh[:3, 3]
    p_local = (R.T @ (verts - t).T).T

    z_fwd = -p_local[:, 2]
    safe_z = np.where(z_fwd > 0.2, z_fwd, 1e-6)

    sensor_u = fx * p_local[:, 0] / safe_z + cx
    sensor_v = fy * p_local[:, 1] / safe_z + cy
    jpeg_x = sensor_u
    jpeg_y = img_h - sensor_v

    return jpeg_x, jpeg_y, z_fwd


def recolor(session_dir, mesh_path, frame_num=None, multi=False, occlude_tol=0.03):
    session_dir = Path(session_dir)
    mesh = o3d.io.read_triangle_mesh(str(mesh_path))
    mesh.compute_vertex_normals()
    verts = np.asarray(mesh.vertices)
    normals = np.asarray(mesh.vertex_normals)
    n = len(verts)
    print(f"Mesh: {n} verts from {mesh_path.name}")

    if multi:
        print(f"Per-frame depth-image occlusion enabled; tolerance = {occlude_tol} m")

    jpg_dir = session_dir / "decoded_jpg"
    depth_files = sorted(session_dir.glob("depth_*.npy"))
    frame_nums = []
    for df in depth_files:
        num = int(df.stem.split("_")[1])
        if (jpg_dir / f"frame_{num:06d}.jpg").exists() and \
           (session_dir / f"meta_{num:06d}.txt").exists():
            frame_nums.append(num)

    if frame_num is not None:
        frame_nums = [frame_num]
    print(f"Using {len(frame_nums)} frames")

    color_sum = np.zeros((n, 3), dtype=np.float64)
    weight_sum = np.zeros(n, dtype=np.float64)

    for i, num in enumerate(frame_nums):
        meta = parse_metadata(session_dir / f"meta_{num:06d}.txt")
        pose = meta.get("rgb_camera_pose_matrix",
                        meta.get("head_pose_matrix", meta["pose_matrix"]))
        det = np.linalg.det(pose[:3, :3])
        if abs(det) < 0.5 or abs(det) > 2.0:
            continue

        rgb = np.array(Image.open(jpg_dir / f"frame_{num:06d}.jpg").convert("RGB"))
        img_h, img_w = rgb.shape[:2]
        fx = meta.get("intr_fx", 859.2)
        fy = meta.get("intr_fy", 859.2)
        cx = meta.get("intr_cx", 639.2)
        cy = meta.get("intr_cy", 637.3)

        jpeg_x, jpeg_y, z_fwd = project_vertices(verts, pose, fx, fy, cx, cy, img_h)

        # Validity + backface culling
        cam_pos = pose[:3, 3]
        view_dirs = cam_pos[None, :] - verts
        view_norm = np.linalg.norm(view_dirs, axis=1, keepdims=True)
        cos_angle = np.einsum("ij,ij->i", normals, view_dirs / np.maximum(view_norm, 1e-6))

        # Stricter grazing-angle threshold: oblique samples amplify projection
        # errors and are the main source of "red on walls" artifacts when an
        # off-mesh object intercepts a sightline.
        valid = ((z_fwd > 0.2) &
                 (jpeg_x >= 0) & (jpeg_x < img_w - 1) &
                 (jpeg_y >= 0) & (jpeg_y < img_h - 1) &
                 (cos_angle > 0.25))
        idx = np.where(valid)[0]
        if len(idx) == 0:
            continue

        iu = np.clip(jpeg_x[idx].astype(int), 0, img_w - 1)
        iv = np.clip(jpeg_y[idx].astype(int), 0, img_h - 1)

        if multi:
            # Per-frame depth-image occlusion: project vertex into the depth
            # camera's image, compare its forward distance to the observed
            # depth. This uses what the depth sensor actually saw at the
            # moment, so it catches occluders that the TSDF mesh missed
            # (small objects, transparent/dark surfaces, etc.) which is where
            # mesh-raycasting fails.
            d_pose = meta.get("depth_pose_matrix", pose)
            d_fx = meta.get("depth_intr_fx")
            d_fy = meta.get("depth_intr_fy")
            d_cx = meta.get("depth_intr_cx")
            d_cy = meta.get("depth_intr_cy")

            if d_fx and d_fx > 0:
                depth_img = np.load(session_dir / f"depth_{num:06d}.npy")
                depth_h, depth_w = depth_img.shape

                cand = verts[idx]
                Rd = d_pose[:3, :3]; td = d_pose[:3, 3]
                pL = (Rd.T @ (cand - td).T).T
                zf_d = -pL[:, 2]
                safe_zd = np.where(zf_d > 0.1, zf_d, 1e-6)
                du = d_fx * pL[:, 0] / safe_zd + d_cx
                dv_sensor = d_fy * pL[:, 1] / safe_zd + d_cy
                dv = depth_h - dv_sensor  # viewport Y-up -> stored top-down

                in_depth = ((zf_d > 0.1) &
                            (du >= 0) & (du < depth_w - 1) &
                            (dv >= 0) & (dv < depth_h - 1))

                dui = np.clip(du.astype(int), 0, depth_w - 1)
                dvi = np.clip(dv.astype(int), 0, depth_h - 1)
                observed = depth_img[dvi, dui]
                # Accept only if depth sensor saw a surface right at the
                # vertex's forward distance (and the depth sample is valid).
                visible = in_depth & (observed > 0.1) & (np.abs(observed - zf_d) < occlude_tol)
                good = np.where(visible)[0]
                idx = idx[good]
                iu = iu[good]
                iv = iv[good]

        if len(idx) == 0:
            continue

        sampled = rgb[iv, iu].astype(np.float64)
        w = cos_angle[idx] / np.maximum(np.linalg.norm(verts[idx] - cam_pos, axis=1), 0.1)
        color_sum[idx] += sampled * w[:, None]
        weight_sum[idx] += w

        if (i + 1) % 10 == 0 or i == 0 or i == len(frame_nums) - 1:
            covered = (weight_sum > 0).sum()
            print(f"  [{i+1}/{len(frame_nums)}] frame {num}  colored: {covered}/{n}")

    colored = weight_sum > 0
    print(f"\nFinal: {colored.sum()}/{n} vertices ({100*colored.sum()/n:.1f}%)")

    final_colors = np.full((n, 3), 0.5)
    final_colors[colored] = color_sum[colored] / weight_sum[colored, None] / 255.0
    final_colors = np.clip(final_colors, 0.0, 1.0)
    mesh.vertex_colors = o3d.utility.Vector3dVector(final_colors)
    return mesh


def main():
    parser = argparse.ArgumentParser()
    parser.add_argument("session_dir", nargs="?", default=None)
    parser.add_argument("--mesh", type=str, default=None)
    parser.add_argument("--frame", type=int, default=None,
                        help="Single frame number (default: all frames)")
    parser.add_argument("--all-meshes", action="store_true",
                        help="Recolor all 4 unity_tsdf_*.ply meshes")
    args = parser.parse_args()

    if args.session_dir is None:
        sessions = sorted(Path("debug_output").glob("session_*"))
        if not sessions:
            print("No sessions found")
            return
        args.session_dir = str(sessions[-1])
        print(f"Auto-selected: {args.session_dir}")

    sd = Path(args.session_dir)

    if args.all_meshes:
        meshes = sorted(sd.glob("unity_tsdf_*.ply"))
        if not meshes:
            print("No unity_tsdf_*.ply found. Run unity_reconstruct.py first.")
            return
        frame = args.frame if args.frame else 30
        for mesh_path in meshes:
            print(f"\n{'='*60}")
            mesh = recolor(args.session_dir, mesh_path, frame_num=frame)
            out = sd / f"unity_recolor_{mesh_path.stem}_f{frame}.ply"
            o3d.io.write_triangle_mesh(str(out), mesh, write_vertex_colors=True)
            print(f"Saved: {out.name}")
    else:
        mesh_path = Path(args.mesh) if args.mesh else sd / "unity_mesh.ply"
        multi = args.frame is None
        mesh = recolor(args.session_dir, mesh_path, frame_num=args.frame, multi=multi)
        suffix = "all" if multi else f"f{args.frame}"
        out = sd / f"unity_recolor_{mesh_path.stem}_{suffix}.ply"
        o3d.io.write_triangle_mesh(str(out), mesh, write_vertex_colors=True)
        print(f"Saved: {out.name}")


if __name__ == "__main__":
    main()
