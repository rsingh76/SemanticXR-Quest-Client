r"""One-shot: uses the correspondences you collected, solves the correction,
writes rgb_calibration.json (compatible with apply_calibration.py).

Correspondences baked in from the interactive run you did on session
1776547868, frame 2. If you re-collect for a different session/frame,
replace them below.
"""
from pathlib import Path
import json
import numpy as np
from scipy.optimize import least_squares
from scipy.spatial.transform import Rotation as Rscipy

from reconstruct_tsdf import parse_metadata


SESSION = Path("debug_output/session_1776547868")
FRAME = 2

# (world XYZ, RGB pixel) from your interactive calibration run
CORRESPONDENCES = [
    ([ 0.205,       0.825,      -0.28130192], (880.0772, 667.2056)),
    ([ 0.245,       1.04094543, -0.135],      (1005.676, 563.771)),
    ([-0.43988144,  0.745,      -0.215],      (313.9589, 709.6876)),
    ([ 0.275,       1.24974885, -0.285],      (1046.311, 260.8564)),
]


def residuals(params, worlds, targets, fx, fy, cx, cy, img_h):
    rvec = params[:3]
    tvec = params[3:6]
    dR = Rscipy.from_rotvec(rvec).as_matrix()
    res = []
    for w, target in zip(worlds, targets):
        p = dR @ (w - tvec)
        z = -p[2]
        if z < 0.05:
            res.extend([1e3, 1e3])
            continue
        u = fx * p[0] / z + cx
        v_sensor = fy * p[1] / z + cy
        v_jpeg = img_h - v_sensor
        res.append(u - target[0])
        res.append(v_jpeg - target[1])
    return np.asarray(res)


def main():
    meta = parse_metadata(SESSION / f"meta_{FRAME:06d}.txt")
    rgb_pose = meta["rgb_camera_pose_matrix"]
    fx = meta["intr_fx"]; fy = meta["intr_fy"]
    cx = meta["intr_cx"]; cy = meta["intr_cy"]

    # Get image dims from the jpeg
    from PIL import Image
    img_w, img_h = Image.open(SESSION / f"decoded_jpg/frame_{FRAME:06d}.jpg").size

    worlds = np.array([c[0] for c in CORRESPONDENCES], dtype=np.float64)
    targets = np.array([c[1] for c in CORRESPONDENCES], dtype=np.float64)

    r0 = Rscipy.from_matrix(rgb_pose[:3, :3].T).as_rotvec()
    t0 = rgb_pose[:3, 3].astype(np.float64)
    params0 = np.concatenate([r0, t0])

    cost_init = np.linalg.norm(residuals(params0, worlds, targets, fx, fy, cx, cy, img_h))
    print(f"Initial cost (pixels): {cost_init:.1f}")

    sol = least_squares(
        residuals, params0,
        args=(worlds, targets, fx, fy, cx, cy, img_h),
        method="lm", max_nfev=300)
    params_opt = sol.x
    cost_opt = np.linalg.norm(sol.fun)
    print(f"Optimized cost      : {cost_opt:.1f}")

    dR_opt = Rscipy.from_rotvec(params_opt[:3]).as_matrix()  # world-to-cam
    t_opt = params_opt[3:6]
    R_corrected = dR_opt.T                                    # camera-to-world
    t_corrected = t_opt

    delta_t = t_corrected - rgb_pose[:3, 3]
    delta_R = rgb_pose[:3, :3].T @ R_corrected
    delta_deg = np.degrees(np.linalg.norm(Rscipy.from_matrix(delta_R).as_rotvec()))

    print(f"\nCorrection:")
    print(f"  translation delta: [{delta_t[0]*100:+.1f}, {delta_t[1]*100:+.1f}, "
          f"{delta_t[2]*100:+.1f}] cm")
    print(f"  rotation delta   : {delta_deg:.2f} deg")

    # Per-pair residual breakdown
    print("\nPer-pair residuals after correction:")
    for i, (w, t) in enumerate(zip(worlds, targets)):
        p = dR_opt @ (w - t_opt)
        z = -p[2]
        u = fx * p[0] / z + cx
        v_jpeg = img_h - (fy * p[1] / z + cy)
        print(f"  pair {i}: ({u - t[0]:+.1f}, {v_jpeg - t[1]:+.1f}) px")

    corrected_pose_4x4 = np.eye(4)
    corrected_pose_4x4[:3, :3] = R_corrected
    corrected_pose_4x4[:3, 3] = t_corrected

    out = {
        "session": str(SESSION),
        "frame_used": FRAME,
        "image_wh": [img_w, img_h],
        "intrinsics": {"fx": fx, "fy": fy, "cx": cx, "cy": cy},
        "baseline_rgb_pose": rgb_pose.tolist(),
        "corrected_rgb_pose": corrected_pose_4x4.tolist(),
        "correction_rotation_deg": float(delta_deg),
        "correction_translation_m": float(np.linalg.norm(delta_t)),
        "correspondences": [
            {"world": w.tolist(), "pixel": list(t)}
            for w, t in zip(worlds, targets)
        ],
    }
    save_path = SESSION / "rgb_calibration.json"
    with open(save_path, "w") as f:
        json.dump(out, f, indent=2)
    print(f"\nSaved -> {save_path}")


if __name__ == "__main__":
    main()
