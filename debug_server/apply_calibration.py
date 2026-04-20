r"""
Apply the calibration saved by _interactive_calibrate.py and visualize the
effect. For a given frame, replaces the baseline rgb_pose with the corrected
one from rgb_calibration.json, then:

  1. Projects every mesh vertex onto the RGB and overlays a green dot at each
     projection (so you can see whether mesh features now align with RGB
     features).
  2. Also draws red dots for the baseline (uncorrected) projection of the same
     vertices, so you can directly compare.

Supports "partial corrections" — you can apply the full correction, or only
the vertical (Y) translation component, or only the rotation, etc.

Usage:
    python apply_calibration.py debug_output/session_XXXX            # full correction
    python apply_calibration.py debug_output/session_XXXX --y-only   # just vertical translation
    python apply_calibration.py debug_output/session_XXXX --rot-only # just rotation
    python apply_calibration.py debug_output/session_XXXX --trans-only  # just translation
    python apply_calibration.py debug_output/session_XXXX --frame 30 # different frame
"""
import argparse
import json
from pathlib import Path

import numpy as np
from PIL import Image, ImageDraw
from reconstruct_tsdf import parse_metadata


def project(world_pts, pose, fx, fy, cx, cy, img_w, img_h):
    """Vectorised world->RGB pixel projection (same formula as the pipeline)."""
    R = pose[:3, :3]; t = pose[:3, 3]
    p_local = (R.T @ (world_pts - t).T).T
    z_fwd = -p_local[:, 2]
    safe_z = np.where(z_fwd > 0.05, z_fwd, 1e-6)
    u = fx * p_local[:, 0] / safe_z + cx
    v_sensor = fy * p_local[:, 1] / safe_z + cy
    v_jpeg = img_h - v_sensor
    valid = (z_fwd > 0.2) & (u >= 0) & (u < img_w) & (v_jpeg >= 0) & (v_jpeg < img_h)
    return u, v_jpeg, valid


def build_corrected_pose(baseline, corrected, mode):
    """Return a pose that applies the chosen subset of the correction."""
    baseline = np.asarray(baseline, dtype=np.float64)
    corrected = np.asarray(corrected, dtype=np.float64)
    result = baseline.copy()

    if mode == "full":
        return corrected.copy()
    if mode == "rot-only":
        result[:3, :3] = corrected[:3, :3]
        return result
    if mode == "trans-only":
        result[:3, 3] = corrected[:3, 3]
        return result
    if mode == "y-only":
        result[1, 3] = corrected[1, 3]
        return result
    if mode == "trans-xy":
        result[0, 3] = corrected[0, 3]
        result[1, 3] = corrected[1, 3]
        return result
    if mode == "baseline":
        return result
    raise ValueError(f"Unknown mode: {mode}")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("session_dir")
    ap.add_argument("--frame", type=int, default=None,
                    help="frame number to visualize (defaults to the one used "
                         "for calibration)")
    ap.add_argument("--mode", choices=["full", "rot-only", "trans-only",
                                        "y-only", "trans-xy", "baseline"],
                    default="full")
    ap.add_argument("--y-only", action="store_const", const="y-only", dest="mode")
    ap.add_argument("--rot-only", action="store_const", const="rot-only", dest="mode")
    ap.add_argument("--trans-only", action="store_const", const="trans-only", dest="mode")
    args = ap.parse_args()

    sd = Path(args.session_dir)
    calib_path = sd / "rgb_calibration.json"
    if not calib_path.exists():
        print(f"ERROR: {calib_path} not found. Run _interactive_calibrate.py first.")
        return

    with open(calib_path) as f:
        calib = json.load(f)
    frame = args.frame if args.frame is not None else calib["frame_used"]
    baseline = np.array(calib["baseline_rgb_pose"])
    corrected = np.array(calib["corrected_rgb_pose"])

    # Use the chosen subset of the correction
    effective = build_corrected_pose(baseline, corrected, args.mode)

    delta_t = effective[:3, 3] - baseline[:3, 3]
    delta_R = baseline[:3, :3].T @ effective[:3, :3]
    from scipy.spatial.transform import Rotation as Rscipy
    delta_rvec = Rscipy.from_matrix(delta_R).as_rotvec()
    delta_deg = np.degrees(np.linalg.norm(delta_rvec))

    print(f"Mode: {args.mode}")
    print(f"  translation delta: [{delta_t[0]*100:+.1f}, "
          f"{delta_t[1]*100:+.1f}, {delta_t[2]*100:+.1f}] cm")
    print(f"  rotation delta   : {delta_deg:.2f} deg")

    # Load frame data
    meta = parse_metadata(sd / f"meta_{frame:06d}.txt")
    rgb = Image.open(sd / f"decoded_jpg/frame_{frame:06d}.jpg").convert("RGB")
    img_w, img_h = rgb.size
    fx = meta["intr_fx"]; fy = meta["intr_fy"]
    cx = meta["intr_cx"]; cy = meta["intr_cy"]

    import open3d as o3d
    mesh = o3d.io.read_triangle_mesh(str(sd / "unity_mesh.ply"))
    verts = np.asarray(mesh.vertices)
    # Subsample for viewability
    if len(verts) > 40000:
        idx = np.random.default_rng(0).choice(len(verts), 40000, replace=False)
        verts = verts[idx]

    u_b, v_b, valid_b = project(verts, baseline, fx, fy, cx, cy, img_w, img_h)
    u_c, v_c, valid_c = project(verts, effective, fx, fy, cx, cy, img_w, img_h)

    out = rgb.copy().convert("RGBA")
    overlay = Image.new("RGBA", out.size, (0, 0, 0, 0))
    draw = ImageDraw.Draw(overlay)
    # Baseline in red (small)
    for i in np.where(valid_b)[0]:
        ui, vi = int(u_b[i]), int(v_b[i])
        draw.ellipse([ui-1, vi-1, ui+1, vi+1], fill=(255, 40, 40, 160))
    # Corrected in green (a bit larger so it draws on top)
    for i in np.where(valid_c)[0]:
        ui, vi = int(u_c[i]), int(v_c[i])
        draw.ellipse([ui-2, vi-2, ui+2, vi+2], fill=(0, 255, 0, 200))

    # Also overlay the calibration correspondences
    for c in calib["correspondences"]:
        w = np.array(c["world"])
        tu, tv = c["pixel"]
        # target (user click) = cyan cross
        draw.line([tu-12, tv, tu+12, tv], fill=(0, 255, 255, 255), width=3)
        draw.line([tu, tv-12, tu, tv+12], fill=(0, 255, 255, 255), width=3)

    final = Image.alpha_composite(out, overlay).convert("RGB")
    suffix = args.mode
    out_path = sd / f"debug/calib_overlay_{suffix}_f{frame:06d}.png"
    out_path.parent.mkdir(exist_ok=True)
    final.save(out_path)
    print(f"\n=> {out_path}")
    print("  red dots   = baseline (uncorrected) projection of mesh vertices")
    print("  green dots = corrected projection")
    print("  cyan X     = your clicked correspondences")


if __name__ == "__main__":
    main()
