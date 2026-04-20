r"""
SemanticXR TSDF Volumetric Reconstruction from Quest Camera Data

Uses Open3D's ScalableTSDFVolume to fuse noisy depth into a clean mesh.
TSDF (Truncated Signed Distance Function) averages multiple depth observations
per voxel, naturally filtering sensor noise and producing smooth surfaces.

Output: a colored triangle mesh (.ply) viewable in MeshLab, Open3D, etc.

Usage:
    cmd /c "conda activate semanticxr && cd C:\Users\rahul\Documents\debug_server && python reconstruct_tsdf.py" 
    python reconstruct_tsdf.py [session_dir] [--every N] [--voxel V] [--max-depth D]

Examples:
    python reconstruct_tsdf.py debug_output/session_1775028101
    python reconstruct_tsdf.py debug_output/session_1775028101 --every 2 --voxel 0.01
    python reconstruct_tsdf.py debug_output/session_1775028101 --voxel 0.005 --max-depth 3.0
"""

import os
# Prevent Open3D from initializing a GPU/OpenGL rendering backend on import.
# We only use Open3D for TSDF integration and PLY I/O — visualization is done
# in MeshLab. Without this, `import open3d` can hang indefinitely if no
# display is available or a previous GL context wasn't cleaned up.
os.environ.setdefault("OPEN3D_CPU_RENDERING", "true")

import argparse
import re
from pathlib import Path

import numpy as np
import open3d as o3d
from PIL import Image


# ---------------------------------------------------------------------------
# Metadata parsing (shared with reconstruct.py)
# ---------------------------------------------------------------------------

def parse_metadata(meta_path):
    """Parse a meta_XXXXXX.txt file and return a dict of values.

    Recognized 4x4 matrix blocks (order doesn't matter):
      - "depth_pose (4x4):"       -> meta["depth_pose_matrix"]
      - "rgb_camera_pose (4x4):"  -> meta["rgb_camera_pose_matrix"]
      - "head_pose (4x4):"        -> meta["head_pose_matrix"]
      - "pose (4x4):"             -> meta["pose_matrix"]   (legacy, == head_pose)
    """
    meta = {}
    # Accumulators for each matrix block we recognize
    matrix_blocks = {
        "depth_pose": [],
        "rgb_camera_pose": [],
        "head_pose": [],
        "pose": [],
    }
    current_matrix = None

    with open(meta_path, "r") as f:
        for line in f:
            line = line.strip()

            # Detect matrix headers
            matched_header = False
            for name in matrix_blocks:
                if line.startswith(f"{name} (4x4)"):
                    current_matrix = name
                    matched_header = True
                    break
            if matched_header:
                continue

            if current_matrix:
                nums = re.findall(r"[-+]?\d*\.?\d+", line)
                if nums and len(nums) >= 4:
                    row = [float(x) for x in nums[:4]]
                    matrix_blocks[current_matrix].append(row)
                    if len(matrix_blocks[current_matrix]) == 4:
                        current_matrix = None
                    continue

            if ": " in line:
                key, val = line.split(": ", 1)
                meta[key] = val

    # Convert accumulated rows to numpy matrices
    for name, rows in matrix_blocks.items():
        if len(rows) == 4:
            meta[f"{name}_matrix"] = np.array(rows, dtype=np.float64)

    # Ensure legacy "pose_matrix" always exists (identity fallback)
    if "pose_matrix" not in meta:
        meta["pose_matrix"] = np.eye(4)

    intr_str = meta.get("intrinsics", "")
    for k, v in re.findall(r"(\w+)=([\d.]+)", intr_str):
        meta[f"intr_{k}"] = float(v)

    dintr_str = meta.get("depth_intrinsics", "")
    for k, v in re.findall(r"(\w+)=([\d.]+)", dintr_str):
        meta[f"depth_intr_{k}"] = float(v)

    return meta


def estimate_depth_intrinsics(depth_w, depth_h, fov_deg=90.0):
    """Estimate pinhole intrinsics for Quest's environment depth texture."""
    fov_rad = np.radians(fov_deg)
    fx = (depth_w / 2.0) / np.tan(fov_rad / 2.0)
    fy = (depth_h / 2.0) / np.tan(fov_rad / 2.0)
    return fx, fy, depth_w / 2.0, depth_h / 2.0


# ---------------------------------------------------------------------------
# Coordinate conversion
# ---------------------------------------------------------------------------

def get_extrinsic_for_open3d(pose_c2w_rh):
    """
    Convert stored camera-to-world pose (RH convention) to Open3D extrinsic.

    Stored pose convention (after TcpProtoClient LH->RH conversion):
        Camera axes: X-right, Y-up, Z-backward (OpenGL-like)
    Open3D TSDF expects OpenCV convention:
        Camera axes: X-right, Y-down, Z-forward

    Relationship: p_opencv = flip @ p_rh  where flip = diag(1, -1, -1, 1)

    Full chain:  p_world -> inv(pose_c2w) -> p_camera_rh -> flip -> p_camera_opencv
    So: extrinsic = flip @ inv(pose_c2w)
    """
    flip = np.diag([1.0, -1.0, -1.0, 1.0])
    pose_w2c_rh = np.linalg.inv(pose_c2w_rh)
    extrinsic = flip @ pose_w2c_rh
    return extrinsic


# ---------------------------------------------------------------------------
# TSDF reconstruction
# ---------------------------------------------------------------------------

def reconstruct_tsdf(session_dir, every=1, voxel_length=0.01, sdf_trunc_factor=5.0,
                     max_depth=5.0, depth_fov=90.0, use_rgb_intrinsics=True,
                     output_file=None, extract_pcd=False, brighten=1.0):
    """
    Fuse all frames in a session into a TSDF volume, then extract a mesh.

    Args:
        session_dir: Path to session directory
        every: Process every Nth frame
        voxel_length: TSDF voxel size in meters (smaller = finer detail but more memory)
            - 0.005: very fine (5mm), high memory, best detail
            - 0.01:  good balance (1cm)
            - 0.02:  coarser, faster, lower memory
        sdf_trunc_factor: Truncation distance = voxel_length * this factor
        max_depth: Maximum depth to integrate (meters)
        depth_fov: Fallback FOV estimate if no intrinsics available
        use_rgb_intrinsics: Use RGB intrinsics scaled to depth resolution (fallback)
        output_file: Output path (default: session_dir/tsdf_mesh.ply)
        extract_pcd: Also extract a point cloud from the TSDF volume
    """
    session_dir = Path(session_dir)
    jpg_dir = session_dir / "decoded_jpg"

    # Find complete frames
    depth_files = sorted(session_dir.glob("depth_*.npy"))
    frame_nums = []
    for df in depth_files:
        num = int(df.stem.split("_")[1])
        rgb_path = jpg_dir / f"frame_{num:06d}.jpg"
        meta_path = session_dir / f"meta_{num:06d}.txt"
        if rgb_path.exists() and meta_path.exists():
            frame_nums.append(num)

    if not frame_nums:
        print("No complete frames found!")
        return None

    print(f"Found {len(frame_nums)} complete frames (RGB + depth + meta)")

    # Check first frame for intrinsics info
    first_meta = parse_metadata(session_dir / f"meta_{frame_nums[0]:06d}.txt")
    first_depth = np.load(session_dir / f"depth_{frame_nums[0]:06d}.npy")
    dh, dw = first_depth.shape

    has_depth_intr = "depth_intr_fx" in first_meta and first_meta["depth_intr_fx"] > 0
    if has_depth_intr:
        fx = first_meta["depth_intr_fx"]
        fy = first_meta["depth_intr_fy"]
        cx = first_meta["depth_intr_cx"]
        cy = first_meta["depth_intr_cy"]
        print(f"Intrinsics: DEPTH camera (fx={fx:.1f} fy={fy:.1f} cx={cx:.1f} cy={cy:.1f})")
    elif use_rgb_intrinsics and "intr_fx" in first_meta:
        img_w = float(first_meta.get("image_size", "1280x1280").split("x")[0])
        scale = dw / img_w
        fx = first_meta["intr_fx"] * scale
        fy = first_meta["intr_fy"] * scale
        cx = first_meta["intr_cx"] * scale
        cy = first_meta["intr_cy"] * scale
        print(f"Intrinsics: RGB scaled (fx={fx:.1f} fy={fy:.1f} cx={cx:.1f} cy={cy:.1f})")
    else:
        fx, fy, cx, cy = estimate_depth_intrinsics(dw, dh, fov_deg=depth_fov)
        print(f"Intrinsics: estimated from FOV={depth_fov}deg (fx={fx:.1f} fy={fy:.1f})")

    sdf_trunc = voxel_length * sdf_trunc_factor
    print(f"Depth image: {dw}x{dh}")
    print(f"TSDF params: voxel={voxel_length}m, trunc={sdf_trunc:.4f}m, max_depth={max_depth}m")

    # Report which poses & timestamps are available in the capture
    has_depth_pose = "depth_pose_matrix" in first_meta
    has_rgb_pose = "rgb_camera_pose_matrix" in first_meta
    has_head_pose = "head_pose_matrix" in first_meta
    rgb_ts = first_meta.get("rgb_timestamp_ns", "0")
    depth_ts = first_meta.get("depth_timestamp_ns", "0")
    print(f"Poses available: depth={has_depth_pose} rgb_cam={has_rgb_pose} head={has_head_pose}")
    print(f"Timestamps (first frame): rgb_ns={rgb_ts} depth_ns={depth_ts}")
    if has_depth_pose:
        print("Using DEPTH camera pose for TSDF integration (geometry source of truth)")
    elif has_head_pose:
        print("WARNING: depth_pose not available, falling back to head_pose")
    else:
        print("WARNING: only legacy 'pose' available, falling back")

    # Create Open3D intrinsic object
    intrinsic = o3d.camera.PinholeCameraIntrinsic(dw, dh, fx, fy, cx, cy)

    # Create TSDF volume
    volume = o3d.pipelines.integration.ScalableTSDFVolume(
        voxel_length=voxel_length,
        sdf_trunc=sdf_trunc,
        color_type=o3d.pipelines.integration.TSDFVolumeColorType.RGB8,
    )

    selected = frame_nums[::every]
    print(f"Integrating {len(selected)} frames ...\n")

    skipped = 0
    for i, num in enumerate(selected):
        rgb_path = jpg_dir / f"frame_{num:06d}.jpg"
        depth_path = session_dir / f"depth_{num:06d}.npy"
        meta_path = session_dir / f"meta_{num:06d}.txt"

        meta = parse_metadata(meta_path)

        # Load depth (metric meters, float32)
        depth_metric = np.load(depth_path).astype(np.float32)

        # Per-frame intrinsics if available (they can vary slightly)
        if has_depth_intr and "depth_intr_fx" in meta and meta["depth_intr_fx"] > 0:
            frame_fx = meta["depth_intr_fx"]
            frame_fy = meta["depth_intr_fy"]
            frame_cx = meta["depth_intr_cx"]
            frame_cy = meta["depth_intr_cy"]
            frame_intrinsic = o3d.camera.PinholeCameraIntrinsic(
                dw, dh, frame_fx, frame_fy, frame_cx, frame_cy
            )
        else:
            frame_intrinsic = intrinsic

        # Choose pose for TSDF integration:
        #   1. depth_pose  — from EnvironmentDepthFrameDesc (best for geometry)
        #   2. head_pose   — Camera.main eye center (fallback)
        #   3. pose        — legacy alias of head_pose
        #
        # NOTE: Open3D TSDF integration uses a single extrinsic for both depth
        # and color. Since RGB comes from a physically different sensor
        # (PassthroughCameraAccess, ~1cm offset), vertex colors will have a
        # small parallax error. For better color, run recolor_mesh.py which
        # re-projects RGB using rgb_camera_pose independently.
        if "depth_pose_matrix" in meta:
            pose_c2w = meta["depth_pose_matrix"]
        elif "head_pose_matrix" in meta:
            pose_c2w = meta["head_pose_matrix"]
        else:
            pose_c2w = meta["pose_matrix"]

        # Validate pose (skip degenerate frames)
        det = np.linalg.det(pose_c2w[:3, :3])
        if abs(det) < 0.5 or abs(det) > 2.0:
            skipped += 1
            continue

        # Convert to Open3D extrinsic (world-to-camera, OpenCV convention)
        extrinsic = get_extrinsic_for_open3d(pose_c2w)

        # Clamp depth: zero out anything beyond max_depth or below minimum
        depth_metric[depth_metric <= 0.05] = 0.0
        depth_metric[depth_metric > max_depth] = 0.0

        # Load and resize RGB to match depth resolution
        rgb_img = Image.open(rgb_path).convert("RGB").resize((dw, dh), Image.LANCZOS)
        rgb_np = np.ascontiguousarray(np.asarray(rgb_img, dtype=np.uint8))

        # Color correction for Quest passthrough camera (very dark, warm-tinted)
        if brighten > 1.0:
            rgb_float = rgb_np.astype(np.float32)
            # Per-channel white balance: normalize each channel to use full range
            for ch in range(3):
                ch_data = rgb_float[:, :, ch]
                p_low = np.percentile(ch_data[ch_data > 0], 2) if (ch_data > 0).any() else 0
                p_high = np.percentile(ch_data[ch_data > 0], 98) if (ch_data > 0).any() else 255
                if p_high > p_low:
                    rgb_float[:, :, ch] = (ch_data - p_low) / (p_high - p_low) * 255.0
            rgb_np = np.clip(rgb_float, 0, 255).astype(np.uint8)

        # Create Open3D images
        depth_o3d = o3d.geometry.Image(np.ascontiguousarray(depth_metric))
        color_o3d = o3d.geometry.Image(rgb_np)
        rgbd = o3d.geometry.RGBDImage.create_from_color_and_depth(
            color_o3d, depth_o3d,
            depth_scale=1.0,        # already in meters
            depth_trunc=max_depth,
            convert_rgb_to_intensity=False,
        )

        # Integrate into TSDF volume
        volume.integrate(rgbd, frame_intrinsic, extrinsic)

        if (i + 1) % 10 == 0 or i == 0 or i == len(selected) - 1:
            print(f"  [{i+1}/{len(selected)}] frame {num} integrated")

    if skipped > 0:
        print(f"\nSkipped {skipped} frames with degenerate poses")

    # Extract mesh
    print("\nExtracting mesh from TSDF volume ...")
    mesh = volume.extract_triangle_mesh()
    mesh.compute_vertex_normals()

    n_verts = len(mesh.vertices)
    n_tris = len(mesh.triangles)
    print(f"Mesh: {n_verts} vertices, {n_tris} triangles")

    # Optional: clean up small disconnected components
    if n_tris > 100:
        print("Removing small disconnected components ...")
        triangle_clusters, cluster_n_triangles, _ = mesh.cluster_connected_triangles()
        triangle_clusters = np.asarray(triangle_clusters)
        cluster_n_triangles = np.asarray(cluster_n_triangles)

        # Keep clusters with at least 1% of total triangles (or minimum 50)
        min_cluster = max(50, int(n_tris * 0.01))
        keep_mask = cluster_n_triangles[triangle_clusters] >= min_cluster
        mesh.remove_triangles_by_mask(~keep_mask)
        mesh.remove_unreferenced_vertices()

        print(f"  {n_verts} -> {len(mesh.vertices)} vertices, "
              f"{n_tris} -> {len(mesh.triangles)} triangles "
              f"(kept clusters >= {min_cluster} tris)")

    # Save mesh
    mesh_path = Path(output_file) if output_file else session_dir / "tsdf_mesh.ply"
    o3d.io.write_triangle_mesh(str(mesh_path), mesh, write_vertex_colors=True)
    print(f"\nSaved mesh: {mesh_path}")
    if mesh.has_vertex_colors():
        colors = np.asarray(mesh.vertex_colors)
        print(f"Vertex colors: mean={colors.mean():.3f} (range {colors.min():.3f}-{colors.max():.3f})")
    print("NOTE: In MeshLab, enable color via: Render -> Color -> Per Vertex")

    # Optionally also extract point cloud from TSDF
    if extract_pcd:
        pcd = volume.extract_point_cloud()
        pcd_path = mesh_path.with_name("tsdf_pointcloud.ply")
        o3d.io.write_point_cloud(str(pcd_path), pcd)
        print(f"Saved point cloud: {pcd_path}")

    print(f"\nOpen in MeshLab:  meshlab \"{mesh_path}\"")
    return mesh


def main():
    parser = argparse.ArgumentParser(
        description="TSDF volumetric reconstruction from SemanticXR session data"
    )
    parser.add_argument("session_dir", nargs="?", default=None,
                        help="Path to session directory (auto-detects latest if omitted)")
    parser.add_argument("--every", type=int, default=1,
                        help="Process every Nth frame (default: 1)")
    parser.add_argument("--voxel", type=float, default=0.01,
                        help="TSDF voxel size in meters (default: 0.01 = 1cm). "
                             "Smaller = finer but more memory. Try 0.005 for fine detail.")
    parser.add_argument("--trunc-factor", type=float, default=5.0,
                        help="SDF truncation = voxel * this (default: 5.0). "
                             "Larger = more noise tolerance but less sharp edges.")
    parser.add_argument("--max-depth", type=float, default=5.0,
                        help="Max depth in meters (default: 5.0). "
                             "Lower = less noise from far surfaces.")
    parser.add_argument("--depth-fov", type=float, default=90.0,
                        help="Estimated depth FOV in degrees (fallback, default: 90)")
    parser.add_argument("--no-rgb-intrinsics", action="store_true",
                        help="Use estimated depth FOV intrinsics instead of RGB camera intrinsics")
    parser.add_argument("-o", "--output", type=str, default=None,
                        help="Output PLY file path (default: session_dir/tsdf_mesh.ply)")
    parser.add_argument("--brighten", type=float, default=2.0,
                        help="Brighten RGB by this factor (default: 2.0). "
                             "Quest passthrough camera is quite dark. Set 1.0 for original.")
    parser.add_argument("--pcd", action="store_true",
                        help="Also extract a point cloud from the TSDF volume")
    parser.add_argument("--visualize", action="store_true",
                        help="Show interactive Open3D viewer after reconstruction")
    args = parser.parse_args()

    if args.session_dir is None:
        output_dir = Path("debug_output")
        sessions = sorted(output_dir.glob("session_*"))
        if not sessions:
            print("No sessions found in debug_output/")
            return
        args.session_dir = str(sessions[-1])
        print(f"Auto-selected latest session: {args.session_dir}")

    use_rgb = not args.no_rgb_intrinsics
    mesh = reconstruct_tsdf(
        args.session_dir,
        every=args.every,
        voxel_length=args.voxel,
        sdf_trunc_factor=args.trunc_factor,
        max_depth=args.max_depth,
        depth_fov=args.depth_fov,
        use_rgb_intrinsics=use_rgb,
        output_file=args.output,
        extract_pcd=args.pcd,
        brighten=args.brighten,
    )

    if args.visualize and mesh is not None and len(mesh.vertices) > 0:
        print("\nOpening viewer ...")
        o3d.visualization.draw_geometries(
            [mesh], window_name="SemanticXR TSDF Reconstruction",
            width=1280, height=720, mesh_show_back_face=True,
        )


if __name__ == "__main__":
    main()
