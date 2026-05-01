r"""
Visualize a TSDF mesh with RGB + depth camera trajectories drawn as frustum
pyramids at each captured pose.

- Red  = depth camera
- Blue = RGB camera
- Pyramid apex = sensor origin, base = image plane (pointing along camera forward)

Usage:
    python visualize_with_trajectory.py debug_output/session_XXXX [--stride 5]
                                        [--size 0.08] [--rebuild]

By default uses the existing unity_mesh.ply in the session dir; pass --rebuild
to regenerate it via unity_reconstruct.build_tsdf.
"""
import argparse
import os
from pathlib import Path

os.environ.setdefault("OPEN3D_CPU_RENDERING", "true")

import numpy as np
import open3d as o3d

from session_io import Session
from unity_reconstruct import build_tsdf


DEPTH_COLOR = np.array([0.84, 0.15, 0.16])   # red
RGB_COLOR   = np.array([0.12, 0.47, 0.71])   # blue


def camera_frustum(pose_c2w: np.ndarray,
                   size: float = 0.08,
                   aspect: float = 1.0,
                   color: np.ndarray = np.array([1.0, 0.0, 0.0])) -> o3d.geometry.LineSet:
    """
    Build a small frustum pyramid at the camera pose.

    Camera convention (matches what's on the wire after TcpProtoClient.LhToRh):
        Right-handed, +X right, +Y up, -Z forward (OpenGL-style).

    The pyramid has apex at the camera origin and its base 'size' meters in
    front of the camera (along -Z local). `aspect` = width/height.
    """
    hh = size * 0.5
    hw = hh * aspect
    d = size

    # 5 points in camera local frame (apex + 4 base corners)
    pts_local = np.array([
        [0.0,  0.0,  0.0],   # 0 apex
        [ hw,   hh,  -d],    # 1 top-right
        [-hw,   hh,  -d],    # 2 top-left
        [-hw,  -hh,  -d],    # 3 bottom-left
        [ hw,  -hh,  -d],    # 4 bottom-right
        [0.0,   hh * 1.5, -d],  # 5 "up" tick above top edge to indicate orientation
    ])
    pts_h = np.concatenate([pts_local, np.ones((pts_local.shape[0], 1))], axis=1)
    pts_world = (pose_c2w @ pts_h.T).T[:, :3]

    lines = [
        [0, 1], [0, 2], [0, 3], [0, 4],   # apex -> 4 corners
        [1, 2], [2, 3], [3, 4], [4, 1],   # base rectangle
        [1, 5], [2, 5],                   # little "up" triangle on top
    ]
    ls = o3d.geometry.LineSet(
        points=o3d.utility.Vector3dVector(pts_world),
        lines=o3d.utility.Vector2iVector(lines),
    )
    ls.colors = o3d.utility.Vector3dVector(np.tile(color, (len(lines), 1)))
    return ls


def trajectory_polyline(points: np.ndarray, color: np.ndarray) -> o3d.geometry.LineSet:
    n = len(points)
    if n < 2:
        return o3d.geometry.LineSet()
    lines = [[i, i + 1] for i in range(n - 1)]
    ls = o3d.geometry.LineSet(
        points=o3d.utility.Vector3dVector(points),
        lines=o3d.utility.Vector2iVector(lines),
    )
    ls.colors = o3d.utility.Vector3dVector(np.tile(color, (len(lines), 1)))
    return ls


def collect_poses(session_dir: Path):
    """Return (frame_nums, depth_poses [Nx4x4], rgb_poses [Nx4x4]).
    A missing pose for a given camera becomes None at that index.
    """
    session = Session(session_dir)
    frame_nums, depth_poses, rgb_poses = [], [], []
    for num in session.frames_with_meta():
        meta = session.load_meta(num)
        frame_nums.append(num)
        depth_poses.append(meta.get("depth_pose_matrix"))
        rgb_poses.append(meta.get("rgb_camera_pose_matrix"))
    return frame_nums, depth_poses, rgb_poses


def valid_pose(p) -> bool:
    if p is None:
        return False
    det = np.linalg.det(p[:3, :3])
    return 0.5 < abs(det) < 2.0


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("session_dir", nargs="?", default=None)
    ap.add_argument("--stride", type=int, default=5,
                    help="draw a frustum every Nth frame (default 5)")
    ap.add_argument("--size", type=float, default=0.08,
                    help="frustum forward length in meters (default 0.08)")
    ap.add_argument("--rebuild", action="store_true",
                    help="rebuild the TSDF mesh instead of reusing unity_mesh.ply")
    ap.add_argument("--voxel", type=float, default=0.01)
    ap.add_argument("--max-depth", type=float, default=5.0)
    ap.add_argument("--no-window", action="store_true",
                    help="skip interactive viewer, only save combined PLY")
    args = ap.parse_args()

    if args.session_dir is None:
        sessions = sorted(Path("debug_output").glob("session_*"))
        if not sessions:
            print("No sessions found")
            return
        args.session_dir = str(sessions[-1])

    session_dir = Path(args.session_dir)
    mesh_path = session_dir / "unity_mesh.ply"

    if args.rebuild or not mesh_path.exists():
        print("Building TSDF mesh...")
        build_tsdf(session_dir, voxel=args.voxel, max_depth=args.max_depth)

    print(f"Loading mesh: {mesh_path}")
    mesh = o3d.io.read_triangle_mesh(str(mesh_path))
    mesh.compute_vertex_normals()
    if not mesh.has_vertex_colors():
        mesh.paint_uniform_color([0.7, 0.7, 0.72])

    frame_nums, depth_poses, rgb_poses = collect_poses(session_dir)
    print(f"Frames found: {len(frame_nums)}")

    # Trajectory polylines — full resolution (every frame)
    depth_pts = np.array([p[:3, 3] for p in depth_poses if valid_pose(p)])
    rgb_pts   = np.array([p[:3, 3] for p in rgb_poses   if valid_pose(p)])

    geoms = [mesh]

    if depth_pts.size:
        geoms.append(trajectory_polyline(depth_pts, DEPTH_COLOR))
    if rgb_pts.size:
        geoms.append(trajectory_polyline(rgb_pts,   RGB_COLOR))

    # Frustums at decimated frames
    n_depth_frust = 0
    n_rgb_frust = 0
    for i, num in enumerate(frame_nums):
        if i % args.stride != 0:
            continue
        dp = depth_poses[i]
        rp = rgb_poses[i]
        if valid_pose(dp):
            geoms.append(camera_frustum(dp, size=args.size, color=DEPTH_COLOR))
            n_depth_frust += 1
        if valid_pose(rp):
            geoms.append(camera_frustum(rp, size=args.size, color=RGB_COLOR))
            n_rgb_frust += 1

    print(f"Frustums drawn: depth={n_depth_frust}, rgb={n_rgb_frust} "
          f"(stride={args.stride}, size={args.size} m)")

    # Save combined debug output as one PLY of the line segments (mesh already saved)
    # Combine all LineSet geoms into a single LineSet for easy re-loading
    all_pts, all_lines, all_colors = [], [], []
    offset = 0
    for g in geoms:
        if isinstance(g, o3d.geometry.LineSet):
            pts = np.asarray(g.points)
            lines = np.asarray(g.lines) + offset
            colors = np.asarray(g.colors)
            all_pts.append(pts)
            all_lines.append(lines)
            all_colors.append(colors)
            offset += len(pts)
    if all_pts:
        combined = o3d.geometry.LineSet(
            points=o3d.utility.Vector3dVector(np.vstack(all_pts)),
            lines=o3d.utility.Vector2iVector(np.vstack(all_lines)),
        )
        combined.colors = o3d.utility.Vector3dVector(np.vstack(all_colors))
        lines_out = session_dir / "trajectory_frustums.ply"
        o3d.io.write_line_set(str(lines_out), combined)
        print(f"Saved lines: {lines_out}")

    if args.no_window:
        print("--no-window given, skipping viewer")
        return

    print("\nOpening viewer. Legend: RED=depth, BLUE=RGB. Press Q to quit.")
    o3d.visualization.draw_geometries(
        geoms,
        window_name=f"{session_dir.name} — mesh + camera trajectories",
        width=1400, height=900,
    )


if __name__ == "__main__":
    main()
