r"""
RGBD reconstruction via RGB-aligned-to-depth sampling.

Pipeline (per frame):
  1. Unproject the 320x320 depth image to world XYZ using depth pose + depth intrinsics.
  2. Project those world points into the RGB camera using RGB pose + RGB intrinsics
     -> get a (u, v) in RGB image for each depth pixel.
  3. Sample the full-res RGB JPEG at those (u, v) -> an aligned RGB image at
     depth resolution (320x320).
  4. Feed (aligned_rgb, depth) to Open3D's RGBD TSDF using the DEPTH camera's
     pose and intrinsics.

If this yields a mesh whose colors match its geometry, the depth->world->RGB
unprojection is correct end-to-end. The same unproject-from-RGB math is used
downstream for object detection (detection pixel -> read aligned depth ->
unproject through RGB camera -> world XYZ).

Usage:
    python unity_rgbd_reconstruct.py debug_output/session_XXXX
"""
import os
os.environ.setdefault("OPEN3D_CPU_RENDERING", "true")

import argparse
from pathlib import Path

import numpy as np
import open3d as o3d
from PIL import Image

from reconstruct_tsdf import parse_metadata


# flip = diag(1,-1,-1,1) converts our on-wire RH/OpenGL camera pose
# (X-right, Y-up, Z-back) into OpenCV (X-right, Y-down, Z-forward) as Open3D expects.
_FLIP_RH_TO_CV = np.diag([1.0, -1.0, -1.0, 1.0])


def unproject_depth_to_world(depth_img, d_pose, d_fx, d_fy, d_cx, d_cy):
    """Depth image (top-down, meters) -> world XYZ point array, plus a mask of valid points.

    Camera convention after LhToRh on wire: X-right, Y-up, Z-back (OpenGL).
    A pixel (du, dv) in a top-down image where (du-cx, cy-dv) corresponds to the
    viewport-Y-up offset maps to camera-local direction
        (x, y, z) = ((du-cx)/fx, (cy-dv)/fy, -1)     # -Z forward
    scaled by the depth value (forward distance along -Z).
    """
    dh, dw = depth_img.shape
    du = np.arange(dw)
    dv = np.arange(dh)
    uu, vv = np.meshgrid(du, dv)             # uu[dv, du] = du, vv[dv, du] = dv
    d = depth_img.astype(np.float32)

    valid = d > 0.05
    # Camera-local coords (OpenGL: +X right, +Y up, -Z forward)
    x_l = (uu - d_cx) / d_fx * d
    y_l = (d_cy - vv) / d_fy * d            # top-down image: (cy - dv) puts +Y up
    z_l = -d                                 # -Z is forward

    pts_cam = np.stack([x_l, y_l, z_l, np.ones_like(d)], axis=-1)  # (dh, dw, 4)
    pts_world = (d_pose @ pts_cam.reshape(-1, 4).T).T[:, :3].reshape(dh, dw, 3)

    return pts_world, valid


def project_world_to_rgb(pts_world, rgb_pose, fx, fy, cx, cy, img_w, img_h):
    """World XYZ -> (jpeg_x, jpeg_y, z_fwd_in_RGB_cam). z_fwd > 0 means in front.

    Same math as unity_recolor.project_vertices (confirmed on-device), written
    vectorized over an arbitrary point array.
    """
    R = rgb_pose[:3, :3]
    t = rgb_pose[:3, 3]
    flat = pts_world.reshape(-1, 3)
    p_local = (R.T @ (flat - t).T).T
    z_fwd = -p_local[:, 2]
    safe_z = np.where(z_fwd > 0.2, z_fwd, 1e-6)

    u = fx * p_local[:, 0] / safe_z + cx
    v_sensor = fy * p_local[:, 1] / safe_z + cy
    v_jpeg = img_h - v_sensor                     # viewport Y-up -> stored top-down

    return u.reshape(pts_world.shape[:2]), v_jpeg.reshape(pts_world.shape[:2]), \
           z_fwd.reshape(pts_world.shape[:2])


def build_aligned_rgb_at_depth_res(depth_img, d_pose, d_intr,
                                    rgb_img, rgb_pose, rgb_intr):
    """Returns (aligned_rgb, valid_mask) at depth resolution.

    For each depth pixel with a valid depth reading, unproject to world, project
    into the full-res RGB image, and sample the RGB color there. Pixels whose
    projection lands outside the RGB image (or fails backprojection) are left
    black with valid_mask=False.
    """
    d_fx, d_fy, d_cx, d_cy = d_intr
    fx, fy, cx, cy = rgb_intr
    dh, dw = depth_img.shape
    rh, rw = rgb_img.shape[:2]

    pts_world, d_valid = unproject_depth_to_world(
        depth_img, d_pose, d_fx, d_fy, d_cx, d_cy)
    ju, jv, zf = project_world_to_rgb(pts_world, rgb_pose, fx, fy, cx, cy, rw, rh)

    aligned = np.zeros((dh, dw, 3), dtype=np.uint8)
    valid = d_valid & (zf > 0.2) & (ju >= 0) & (ju < rw) & (jv >= 0) & (jv < rh)
    iu = np.clip(ju, 0, rw - 1).astype(np.int32)
    iv = np.clip(jv, 0, rh - 1).astype(np.int32)
    aligned[valid] = rgb_img[iv[valid], iu[valid]]
    return aligned, valid


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("session_dir", nargs="?", default=None)
    ap.add_argument("--voxel", type=float, default=0.01)
    ap.add_argument("--max-depth", type=float, default=5.0)
    ap.add_argument("--save-sample-aligned", action="store_true",
                    help="dump aligned-rgb PNG for one frame into debug/ subdir")
    ap.add_argument("--save-pointcloud", action="store_true",
                    help="also build a raw colored point cloud (no TSDF averaging) "
                         "into debug/. Per-frame points are kept as-is so we can "
                         "verify the depth->RGB color alignment end-to-end.")
    ap.add_argument("--save-per-frame", type=int, default=0, metavar="N",
                    help="dump per-frame point cloud + RGB + aligned RGB for N evenly-"
                         "spaced frames into debug/per_frame/. Each frame is independent "
                         "so you can verify alignment frame-by-frame without multi-frame "
                         "pose noise.")
    ap.add_argument("--aggregate", action="store_true",
                    help="accumulate every valid (point, color) across all frames into "
                         "a single cloud (no color averaging), write rgbd_cloud.ply; "
                         "also run Poisson surface reconstruction on it for a mesh.")
    ap.add_argument("--frames", type=str, default=None,
                    help="comma-separated frame numbers to include "
                         "(e.g. 2,33,64,95,134). Default: all frames with complete data.")
    ap.add_argument("--no-poisson", action="store_true",
                    help="skip the Poisson reconstruction; only write the raw cloud.")
    ap.add_argument("--incremental-video", action="store_true",
                    help="render the accumulated cloud after every frame to "
                         "debug/incremental/XXXX.png, and (if imageio-ffmpeg is "
                         "available) stitch them into incremental.mp4 — watch to "
                         "pinpoint which frame corrupts the recon.")
    ap.add_argument("--video-every", type=int, default=1,
                    help="render only every Nth frame to keep file count down")
    ap.add_argument("--video-points-per-frame", type=int, default=10000,
                    help="subsample each frame's cloud to this many points before "
                         "adding to the incremental render (keeps render fast)")
    ap.add_argument("--region-center", type=str, default=None,
                    help='"X,Y,Z" world center for --region-dump. '
                         "Default: centroid of accumulated cloud.")
    ap.add_argument("--region-size", type=float, default=1.0,
                    help="edge length (m) of the cube crop region (default 1.0)")
    ap.add_argument("--region-max-frames", type=int, default=40,
                    help="at most this many frames will be kept (evenly subsampled "
                         "from ALL frames that see the region). Default 40. "
                         "Ignored if --frames-range is used (all in range are kept).")
    ap.add_argument("--frames-range", type=str, default=None, metavar="START:END",
                    help="only consider consecutive frames START..END (inclusive) for "
                         "the region dump. Every frame in range is kept (no subsampling).")
    ap.add_argument("--region-dump", action="store_true",
                    help="for each of --region-frames evenly-spaced frames, save "
                         "the single-frame point cloud cropped to the small region "
                         "into debug/region/. Loading several side-by-side reveals "
                         "inter-frame drift at the scale of that region.")
    args = ap.parse_args()

    if args.session_dir is None:
        sessions = sorted(Path("debug_output").glob("session_*"))
        if not sessions:
            print("No sessions found")
            return
        args.session_dir = str(sessions[-1])

    sd = Path(args.session_dir)
    jpg_dir = sd / "decoded_jpg"
    debug_dir = sd / "debug"
    per_frame_dir = debug_dir / "per_frame"
    want_debug = args.save_sample_aligned or args.save_pointcloud or args.save_per_frame > 0
    if want_debug:
        debug_dir.mkdir(exist_ok=True)
    if args.save_per_frame > 0:
        per_frame_dir.mkdir(exist_ok=True)

    depth_files = sorted(sd.glob("depth_*.npy"))
    frames = []
    for df in depth_files:
        num = int(df.stem.split("_")[1])
        if (jpg_dir / f"frame_{num:06d}.jpg").exists() and \
           (sd / f"meta_{num:06d}.txt").exists():
            frames.append(num)
    if args.frames:
        wanted = {int(x) for x in args.frames.split(",")}
        frames = [n for n in frames if n in wanted]
    print(f"Session: {sd.name}   frames: {len(frames)}")

    first_meta = parse_metadata(sd / f"meta_{frames[0]:06d}.txt")
    first_rgb = np.array(Image.open(jpg_dir / f"frame_{frames[0]:06d}.jpg").convert("RGB"))
    rgb_h, rgb_w = first_rgb.shape[:2]

    rgb_fx = first_meta["intr_fx"]
    rgb_fy = first_meta["intr_fy"]
    rgb_cx = first_meta["intr_cx"]
    rgb_cy = first_meta["intr_cy"]              # Y-up viewport (from bottom)
    print(f"RGB: {rgb_w}x{rgb_h}  fx={rgb_fx:.1f} fy={rgb_fy:.1f} "
          f"cx={rgb_cx:.1f} cy(y-up)={rgb_cy:.1f}")

    # Depth resolution + intrinsics (used for TSDF integration — depth pose & intr).
    first_depth = np.load(sd / f"depth_{frames[0]:06d}.npy")
    dh, dw = first_depth.shape
    d_fx = first_meta["depth_intr_fx"]
    d_fy = first_meta["depth_intr_fy"]
    d_cx = first_meta["depth_intr_cx"]
    d_cy = first_meta["depth_intr_cy"]
    print(f"Depth: {dw}x{dh}  fx={d_fx:.1f} fy={d_fy:.1f} cx={d_cx:.1f} cy={d_cy:.1f}")

    # TSDF expects intrinsics for the RGBD images we feed it. We feed at DEPTH
    # resolution with DEPTH intrinsics (proven-correct in unity_reconstruct.py).
    intrinsic = o3d.camera.PinholeCameraIntrinsic(dw, dh, d_fx, d_fy, d_cx, d_cy)
    volume = o3d.pipelines.integration.ScalableTSDFVolume(
        voxel_length=args.voxel,
        sdf_trunc=args.voxel * 5.0,
        color_type=o3d.pipelines.integration.TSDFVolumeColorType.RGB8,
    )

    # Which frames to dump per-frame for?
    if args.save_per_frame > 0:
        step = max(1, len(frames) // args.save_per_frame)
        per_frame_set = set(frames[::step][:args.save_per_frame])
        print(f"Per-frame dumps for frames: {sorted(per_frame_set)}")
    else:
        per_frame_set = set()

    # --region-dump: check EVERY frame for whether it has points in the region,
    # then after-the-fact subsample the kept frames down to --region-max-frames.
    # --frames-range overrides: only check frames in the given range, keep all.
    if args.region_dump:
        if args.frames_range:
            lo, hi = (int(x) for x in args.frames_range.split(":"))
            region_frame_set = {n for n in frames if lo <= n <= hi}
            region_skip_trim = True
            print(f"Region dump over consecutive frames {lo}..{hi} "
                  f"({len(region_frame_set)} found)")
        else:
            region_frame_set = set(frames)
            region_skip_trim = False
            print(f"Checking all {len(frames)} frames for region coverage")
        region_dir = debug_dir / "region"
        region_dir.mkdir(exist_ok=True)
        if args.region_center:
            region_center = np.array([float(x) for x in args.region_center.split(",")])
        else:
            # Pick a world point that the --frames-range (or full session) is
            # actually looking at: median camera position + 1 m along the median
            # forward-direction of those frames.
            src_frames = region_frame_set if region_frame_set else set(frames)
            cam_poss, cam_fwds = [], []
            for num in sorted(src_frames):
                m = parse_metadata(sd / f"meta_{num:06d}.txt")
                p = m.get("depth_pose_matrix")
                if p is not None and abs(np.linalg.det(p[:3, :3])) > 0.5:
                    cam_poss.append(p[:3, 3])
                    cam_fwds.append(-p[:3, 2])     # OpenGL: -Z is forward
            if cam_poss:
                cam_poss = np.array(cam_poss)
                cam_fwds = np.array(cam_fwds)
                med_pos = np.median(cam_poss, axis=0)
                med_fwd = np.median(cam_fwds, axis=0)
                med_fwd /= max(np.linalg.norm(med_fwd), 1e-6)
                region_center = med_pos + med_fwd * 1.0
                print(f"Region center (auto from {len(cam_poss)} frames' cameras) "
                      f"= ({region_center[0]:.2f}, {region_center[1]:.2f}, "
                      f"{region_center[2]:.2f})")
            else:
                region_center = np.zeros(3)
                print("WARNING: no valid camera poses for region center")
    else:
        region_frame_set = set()
        region_dir = None
        region_center = None

    all_points = []
    all_colors = []

    # Incremental-video setup: matplotlib top-down view (reliable on CPU/Windows).
    inc_dir = None
    inc_png_list = []
    cam_positions = []          # depth-camera position per rendered step
    new_frame_points = []        # points added in the LAST rendered step (highlight)
    import matplotlib
    matplotlib.use("Agg")
    import matplotlib.pyplot as plt
    if args.incremental_video:
        inc_dir = debug_dir / "incremental"
        inc_dir.mkdir(exist_ok=True)

    skipped = 0
    for i, num in enumerate(frames):
        meta = parse_metadata(sd / f"meta_{num:06d}.txt")

        rgb_pose = meta.get("rgb_camera_pose_matrix")
        d_pose = meta.get("depth_pose_matrix")
        if rgb_pose is None or d_pose is None:
            skipped += 1
            continue
        if abs(np.linalg.det(rgb_pose[:3, :3])) < 0.5 or \
           abs(np.linalg.det(d_pose[:3, :3])) < 0.5:
            skipped += 1
            continue

        rgb = np.array(Image.open(jpg_dir / f"frame_{num:06d}.jpg").convert("RGB"))
        depth_img = np.load(sd / f"depth_{num:06d}.npy").astype(np.float32)

        d_intr = (meta["depth_intr_fx"], meta["depth_intr_fy"],
                  meta["depth_intr_cx"], meta["depth_intr_cy"])
        rgb_intr = (rgb_fx, rgb_fy, rgb_cx, rgb_cy)

        aligned_rgb, align_valid = build_aligned_rgb_at_depth_res(
            depth_img, d_pose, d_intr, rgb, rgb_pose, rgb_intr)

        # Zero out depth where no RGB sample landed (Open3D will skip depth=0).
        depth_for_tsdf = depth_img.copy()
        depth_for_tsdf[~align_valid] = 0
        depth_for_tsdf[depth_for_tsdf > args.max_depth] = 0

        if args.save_sample_aligned and i == min(30, len(frames) // 2):
            Image.fromarray(aligned_rgb).save(debug_dir / f"aligned_rgb_f{num}.png")
            # Side-by-side: original RGB cropped to match what's actually sampled,
            # and the aligned-rgb. Also the raw depth and the sampled-from RGB mask.
            Image.fromarray(rgb).save(debug_dir / f"orig_rgb_f{num}.png")
            print(f"  saved debug/aligned_rgb_f{num}.png and debug/orig_rgb_f{num}.png  "
                  f"(valid depth+RGB: {align_valid.sum()} / {dw * dh} px)")

        if num in region_frame_set:
            # Single-frame cloud cropped to the small region. Saved as
            # debug/region/frame_XXXXXX.ply so you can load N of them side-by-side
            # to see whether they all put a given feature at the same XYZ.
            pts_all, valid_unp = unproject_depth_to_world(
                depth_img, d_pose,
                meta["depth_intr_fx"], meta["depth_intr_fy"],
                meta["depth_intr_cx"], meta["depth_intr_cy"])
            valid_pts = valid_unp & align_valid
            xyz = pts_all[valid_pts].reshape(-1, 3)
            col = aligned_rgb[valid_pts].reshape(-1, 3).astype(np.float32) / 255.0

            if region_center is not None and xyz.size:
                half = args.region_size / 2.0
                in_box = np.all(np.abs(xyz - region_center) < half, axis=1)
                crop_xyz = xyz[in_box]
                crop_col = col[in_box]
                if len(crop_xyz) > 10:
                    pc = o3d.geometry.PointCloud()
                    pc.points = o3d.utility.Vector3dVector(crop_xyz)
                    pc.colors = o3d.utility.Vector3dVector(crop_col)
                    out = region_dir / f"frame_{num:06d}.ply"
                    o3d.io.write_point_cloud(str(out), pc)
                    print(f"  region frame {num}: {len(crop_xyz)} pts in crop")

        if num in per_frame_set:
            pts, valid_unp = unproject_depth_to_world(
                depth_img, d_pose,
                meta["depth_intr_fx"], meta["depth_intr_fy"],
                meta["depth_intr_cx"], meta["depth_intr_cy"])
            valid_pts = valid_unp & align_valid
            xyz = pts[valid_pts].reshape(-1, 3)
            col = aligned_rgb[valid_pts].reshape(-1, 3).astype(np.float32) / 255.0
            if xyz.size:
                pc = o3d.geometry.PointCloud()
                pc.points = o3d.utility.Vector3dVector(xyz)
                pc.colors = o3d.utility.Vector3dVector(col)
                o3d.io.write_point_cloud(str(per_frame_dir / f"frame_{num:06d}_cloud.ply"), pc)
                Image.fromarray(rgb).save(per_frame_dir / f"frame_{num:06d}_rgb.png")
                Image.fromarray(aligned_rgb).save(per_frame_dir / f"frame_{num:06d}_aligned.png")
                # Visualize depth as grayscale too
                dvis = np.clip(depth_img / args.max_depth, 0, 1) * 255
                Image.fromarray(dvis.astype(np.uint8)).save(
                    per_frame_dir / f"frame_{num:06d}_depth.png")
                # Render the cloud from a couple of angles so you can inspect
                # without opening each .ply individually.
                for suffix, front, up in [
                    ("view_front", [0, 0, -1], [0, -1, 0]),
                    ("view_top",   [0, -1, 0], [0, 0, -1]),
                ]:
                    vis = o3d.visualization.Visualizer()
                    vis.create_window(visible=False, width=800, height=600)
                    vis.add_geometry(pc)
                    ctr = vis.get_view_control()
                    ctr.set_front(front); ctr.set_up(up)
                    ctr.set_lookat(xyz.mean(0).tolist()); ctr.set_zoom(0.7)
                    vis.poll_events(); vis.update_renderer()
                    vis.capture_screen_image(
                        str(per_frame_dir / f"frame_{num:06d}_{suffix}.png"),
                        do_render=True)
                    vis.destroy_window()
                print(f"  per-frame dump: frame {num} ({len(xyz)} pts)")

        if args.save_pointcloud or args.aggregate:
            # Raw colored point cloud: one 3D point per valid depth pixel, color
            # from aligned_rgb at the same pixel. No averaging, no TSDF — this is
            # the ground-truth of what the depth->world->RGB-sample math produces.
            pts, valid_unp = unproject_depth_to_world(
                depth_img, d_pose,
                meta["depth_intr_fx"], meta["depth_intr_fy"],
                meta["depth_intr_cx"], meta["depth_intr_cy"])
            valid_pts = valid_unp & align_valid
            xyz = pts[valid_pts].reshape(-1, 3)
            col = aligned_rgb[valid_pts].reshape(-1, 3).astype(np.float32) / 255.0
            if xyz.size:
                all_points.append(xyz)
                all_colors.append(col)

        # Incremental video: matplotlib top-down view of cumulative cloud.
        if inc_dir is not None and all_points and (i % args.video_every == 0):
            new_xyz = all_points[-1]
            new_col = all_colors[-1]
            if len(new_xyz) > args.video_points_per_frame:
                idx = np.random.choice(len(new_xyz),
                                        size=args.video_points_per_frame,
                                        replace=False)
                all_points[-1] = new_xyz[idx]
                all_colors[-1] = new_col[idx]

            cum_pts = np.concatenate(all_points, axis=0)
            cum_col = np.concatenate(all_colors, axis=0)
            cam_positions.append(d_pose[:3, 3].copy())

            fig, axs = plt.subplots(1, 2, figsize=(14, 6), dpi=100)
            # Top-down (floorplan) view: X horizontal, Z vertical (flipped so +Z is up)
            ax = axs[0]
            ax.scatter(cum_pts[:, 0], cum_pts[:, 2], c=cum_col, s=0.4, marker=".")
            # Highlight newest frame's points in red
            ax.scatter(new_xyz[:, 0], new_xyz[:, 2], c="red", s=2, marker=".",
                       label=f"frame {num}")
            # Camera trail
            cp = np.array(cam_positions)
            ax.plot(cp[:, 0], cp[:, 2], "b-", linewidth=1, alpha=0.6)
            ax.scatter(cp[-1, 0], cp[-1, 2], c="cyan", s=80, marker="^",
                       edgecolors="black", label="cam")
            ax.set_title(f"Top-down (XZ) — session frame {num} (step {len(inc_png_list)+1})")
            ax.set_xlabel("X (m)"); ax.set_ylabel("Z (m)")
            ax.set_aspect("equal", adjustable="box")
            ax.grid(True, alpha=0.3)
            ax.legend(loc="upper left", fontsize=8)

            # Front view (XY)
            ax = axs[1]
            ax.scatter(cum_pts[:, 0], cum_pts[:, 1], c=cum_col, s=0.4, marker=".")
            ax.scatter(new_xyz[:, 0], new_xyz[:, 1], c="red", s=2, marker=".")
            ax.plot(cp[:, 0], cp[:, 1], "b-", linewidth=1, alpha=0.6)
            ax.scatter(cp[-1, 0], cp[-1, 1], c="cyan", s=80, marker="^",
                       edgecolors="black")
            ax.set_title(f"Front (XY)")
            ax.set_xlabel("X (m)"); ax.set_ylabel("Y (m)")
            ax.set_aspect("equal", adjustable="box")
            ax.grid(True, alpha=0.3)

            fig.tight_layout()
            png = inc_dir / f"{i:04d}_f{num:06d}.png"
            fig.savefig(png)
            plt.close(fig)
            inc_png_list.append(png)

        color_o3d = o3d.geometry.Image(np.ascontiguousarray(aligned_rgb))
        depth_o3d = o3d.geometry.Image(np.ascontiguousarray(depth_for_tsdf))
        rgbd = o3d.geometry.RGBDImage.create_from_color_and_depth(
            color_o3d, depth_o3d,
            depth_scale=1.0, depth_trunc=args.max_depth,
            convert_rgb_to_intensity=False)

        extrinsic = _FLIP_RH_TO_CV @ np.linalg.inv(d_pose)
        volume.integrate(rgbd, intrinsic, extrinsic)

        if (i + 1) % 10 == 0 or i == 0 or i == len(frames) - 1:
            print(f"  [{i+1}/{len(frames)}] frame {num}")

    if skipped:
        print(f"Skipped {skipped} frames (missing/degenerate pose)")

    # Combine region-dump frames into ONE ply, but recolor each frame's points
    # on a rainbow spectrum so you can literally SEE which frame each point
    # came from. If early frames (purple/blue) cluster in one spot and late
    # frames (red/orange) cluster 5 cm away, drift is visible in a single file.
    if region_dir is not None:
        all_region_plys = sorted(region_dir.glob("frame_*.ply"))
        # Subsample down to --region-max-frames for viewability, unless user
        # asked for a specific consecutive range (then keep every frame).
        if not region_skip_trim and len(all_region_plys) > args.region_max_frames:
            step = len(all_region_plys) / args.region_max_frames
            keep_idx = {int(k * step) for k in range(args.region_max_frames)}
            for k, p in enumerate(all_region_plys):
                if k not in keep_idx:
                    p.unlink()
            print(f"Trimmed {len(all_region_plys) - args.region_max_frames} "
                  f"region PLYs to keep {args.region_max_frames}")
        region_plys = sorted(region_dir.glob("frame_*.ply"))
        print(f"Region frames kept: {len(region_plys)}")
        if region_plys:
            all_pts, all_cols = [], []
            for k, p in enumerate(region_plys):
                pc = o3d.io.read_point_cloud(str(p))
                pts = np.asarray(pc.points)
                if pts.size == 0:
                    continue
                # Rainbow color: HSV hue proportional to frame order
                import colorsys
                h = k / max(1, len(region_plys) - 1)
                rgb = np.array(colorsys.hsv_to_rgb(h * 0.85, 0.9, 1.0))
                all_pts.append(pts)
                all_cols.append(np.tile(rgb, (len(pts), 1)))
            if all_pts:
                combined = o3d.geometry.PointCloud()
                combined.points = o3d.utility.Vector3dVector(np.concatenate(all_pts))
                combined.colors = o3d.utility.Vector3dVector(np.concatenate(all_cols))
                out = debug_dir / "region_rainbow.ply"
                o3d.io.write_point_cloud(str(out), combined)
                print(f"Region rainbow (early=purple, late=red) -> {out} "
                      f"({len(combined.points)} pts from {len(region_plys)} frames)")

    if inc_dir is not None:
        print(f"Incremental PNGs: {len(inc_png_list)} -> {inc_dir}")
        # Stitch into an animated GIF with Pillow. Frame labels show which
        # session-frame number each step added, so you can pause and read it.
        if inc_png_list:
            frames_gif = []
            for p in inc_png_list:
                im = Image.open(str(p)).convert("RGB")
                # Downscale for GIF size
                im = im.resize((640, 360), Image.LANCZOS)
                # Burn the source frame number into the bottom-left corner
                from PIL import ImageDraw
                draw = ImageDraw.Draw(im)
                label = p.stem  # "NNNN_fXXXXXX"
                draw.rectangle([5, 335, 200, 358], fill="black")
                draw.text((10, 340), label, fill="white")
                frames_gif.append(im)
            gif = debug_dir / "incremental.gif"
            frames_gif[0].save(str(gif), save_all=True,
                               append_images=frames_gif[1:],
                               duration=160, loop=0, optimize=False)
            print(f"GIF -> {gif} ({len(frames_gif)} frames @ ~6 fps)")

    if (args.save_pointcloud or args.aggregate) and all_points:
        pts_all = np.concatenate(all_points, axis=0)
        cols_all = np.concatenate(all_colors, axis=0)
        pc_raw = o3d.geometry.PointCloud()
        pc_raw.points = o3d.utility.Vector3dVector(pts_all)
        pc_raw.colors = o3d.utility.Vector3dVector(cols_all)
        out_pc = (debug_dir if args.save_pointcloud else sd) / "rgbd_cloud.ply"
        o3d.io.write_point_cloud(str(out_pc), pc_raw)
        print(f"Accumulated cloud -> {out_pc}  ({len(pc_raw.points)} points, no averaging)")

        if args.aggregate and not args.no_poisson:
            # Downsample for Poisson (too dense otherwise) — but keep a copy at
            # full resolution for color lookup.
            print("Downsampling for Poisson geometry...")
            pc_down = pc_raw.voxel_down_sample(voxel_size=args.voxel)
            pc_down.estimate_normals(
                search_param=o3d.geometry.KDTreeSearchParamHybrid(
                    radius=args.voxel * 4, max_nn=30))
            pc_down.orient_normals_consistent_tangent_plane(k=20)
            print(f"Poisson reconstruction on {len(pc_down.points)} pts...")
            poisson_mesh, densities = \
                o3d.geometry.TriangleMesh.create_from_point_cloud_poisson(
                    pc_down, depth=9, linear_fit=False)
            # Trim low-density verts (open holes / Poisson extrapolation artifacts)
            densities = np.asarray(densities)
            keep = densities > np.quantile(densities, 0.05)
            poisson_mesh.remove_vertices_by_mask(~keep)

            # Color transfer from original (non-averaged) cloud via nearest neighbor
            print(f"Transferring colors to {len(poisson_mesh.vertices)} mesh verts...")
            tree = o3d.geometry.KDTreeFlann(pc_raw)
            mverts = np.asarray(poisson_mesh.vertices)
            vcols = np.zeros((len(mverts), 3), dtype=np.float64)
            for vi in range(len(mverts)):
                _, idxs, _ = tree.search_knn_vector_3d(mverts[vi], 1)
                vcols[vi] = cols_all[idxs[0]]
            poisson_mesh.vertex_colors = o3d.utility.Vector3dVector(vcols)
            poisson_mesh.compute_vertex_normals()

            out_mesh = sd / "rgbd_poisson_mesh.ply"
            o3d.io.write_triangle_mesh(str(out_mesh), poisson_mesh,
                                        write_vertex_colors=True)
            print(f"Poisson colored mesh -> {out_mesh} "
                  f"({len(poisson_mesh.vertices)} verts, "
                  f"{len(poisson_mesh.triangles)} tris)")

    print("Extracting mesh...")
    mesh = volume.extract_triangle_mesh()
    mesh.compute_vertex_normals()

    if len(mesh.triangles) > 100:
        tc, cn, _ = mesh.cluster_connected_triangles()
        tc, cn = np.asarray(tc), np.asarray(cn)
        keep = cn[tc] >= max(50, int(len(mesh.triangles) * 0.01))
        mesh.remove_triangles_by_mask(~keep)
        mesh.remove_unreferenced_vertices()

    out = sd / "unity_rgbd_mesh.ply"
    o3d.io.write_triangle_mesh(str(out), mesh, write_vertex_colors=True)
    print(f"\n{len(mesh.vertices)} verts, {len(mesh.triangles)} tris -> {out}")


if __name__ == "__main__":
    main()
