"""
Plot 3D trajectories of depth, RGB, and head camera poses from a debug session.

Usage:
    python plot_trajectories.py [session_dir]
    (defaults to the most recent session in debug_output/)
"""
import re
import sys
from pathlib import Path

import numpy as np
import matplotlib.pyplot as plt
import plotly.graph_objects as go


POSE_BLOCK_RE = re.compile(
    r"^(?P<name>depth_pose|rgb_camera_pose|head_pose)\s*\(4x4\):\s*$"
)
ROW_RE = re.compile(r"\[\s*([-\d.eE+]+)\s+([-\d.eE+]+)\s+([-\d.eE+]+)\s+([-\d.eE+]+)\s*\]")


def parse_meta(path: Path):
    """Return dict {'depth_pose': 4x4 np.array, 'rgb_camera_pose': ..., 'head_pose': ...}
    and frame_number. Missing blocks are omitted."""
    frame_num = None
    poses = {}
    with path.open() as f:
        lines = f.readlines()

    i = 0
    while i < len(lines):
        line = lines[i]
        if line.startswith("frame_number:"):
            frame_num = int(line.split(":", 1)[1].strip())
        m = POSE_BLOCK_RE.match(line)
        if m:
            name = m.group("name")
            rows = []
            for j in range(1, 5):
                rm = ROW_RE.search(lines[i + j])
                if not rm:
                    break
                rows.append([float(x) for x in rm.groups()])
            if len(rows) == 4:
                poses[name] = np.array(rows, dtype=np.float64)
            i += 4
        i += 1

    return frame_num, poses


def translation(m: np.ndarray) -> np.ndarray:
    return m[:3, 3]


def main():
    root = Path(__file__).parent / "debug_output"
    if len(sys.argv) > 1:
        session_dir = Path(sys.argv[1])
    else:
        sessions = sorted(
            [p for p in root.iterdir() if p.is_dir() and p.name.startswith("session_")]
        )
        if not sessions:
            print("No sessions found.", file=sys.stderr)
            sys.exit(1)
        session_dir = sessions[-1]

    print(f"Session: {session_dir}")

    meta_files = sorted(session_dir.glob("meta_*.txt"))
    print(f"Meta files: {len(meta_files)}")

    frames, depth_xyz, rgb_xyz, head_xyz = [], [], [], []

    for mf in meta_files:
        fn, poses = parse_meta(mf)
        if fn is None:
            continue
        d = poses.get("depth_pose")
        r = poses.get("rgb_camera_pose")
        h = poses.get("head_pose")
        # Require all three for a clean comparison; fall back to what's there.
        frames.append(fn)
        depth_xyz.append(translation(d) if d is not None else [np.nan] * 3)
        rgb_xyz.append(translation(r) if r is not None else [np.nan] * 3)
        head_xyz.append(translation(h) if h is not None else [np.nan] * 3)

    frames = np.array(frames)
    depth_xyz = np.array(depth_xyz)
    rgb_xyz = np.array(rgb_xyz)
    head_xyz = np.array(head_xyz)

    # Stats: frame-wise offset between RGB and depth positions
    valid = ~(np.isnan(depth_xyz[:, 0]) | np.isnan(rgb_xyz[:, 0]))
    offsets = np.linalg.norm(rgb_xyz[valid] - depth_xyz[valid], axis=1)
    print(f"\nRGB <-> Depth translation offset (meters):")
    print(f"  count  = {offsets.size}")
    if offsets.size:
        print(f"  mean   = {offsets.mean():.4f}")
        print(f"  median = {np.median(offsets):.4f}")
        print(f"  min    = {offsets.min():.4f}")
        print(f"  max    = {offsets.max():.4f}")

    # 3D plot
    fig = plt.figure(figsize=(12, 9))
    ax = fig.add_subplot(111, projection="3d")

    def plot_traj(xyz, label, color):
        m = ~np.isnan(xyz[:, 0])
        if not m.any():
            return
        ax.plot(xyz[m, 0], xyz[m, 1], xyz[m, 2],
                color=color, linewidth=1.2, alpha=0.85, label=label)
        ax.scatter(xyz[m, 0], xyz[m, 1], xyz[m, 2],
                   color=color, s=8, alpha=0.7)
        ax.scatter(*xyz[m][0], color=color, s=80, marker="o",
                   edgecolors="black", linewidths=1.0)
        ax.scatter(*xyz[m][-1], color=color, s=80, marker="^",
                   edgecolors="black", linewidths=1.0)

    plot_traj(depth_xyz, "Depth camera",    "#d62728")   # red
    plot_traj(rgb_xyz,   "RGB camera",      "#1f77b4")   # blue
    plot_traj(head_xyz,  "Head (Camera.main)", "#2ca02c")  # green

    ax.set_xlabel("X (m)")
    ax.set_ylabel("Y (m)")
    ax.set_zlabel("Z (m)")
    ax.set_title(f"Camera trajectories — {session_dir.name}\n"
                 f"(o = start, ^ = end; Unity world, right-handed on wire)")
    ax.legend(loc="upper left")

    # Equal aspect so offsets look real
    all_pts = np.vstack([p[~np.isnan(p[:, 0])] for p in (depth_xyz, rgb_xyz, head_xyz)
                         if (~np.isnan(p[:, 0])).any()])
    if all_pts.size:
        mins = all_pts.min(axis=0)
        maxs = all_pts.max(axis=0)
        center = (mins + maxs) / 2
        span = (maxs - mins).max() / 2 or 0.5
        ax.set_xlim(center[0] - span, center[0] + span)
        ax.set_ylim(center[1] - span, center[1] + span)
        ax.set_zlim(center[2] - span, center[2] + span)

    out_png = session_dir / "trajectories_3d.png"
    plt.tight_layout()
    plt.savefig(out_png, dpi=140)
    print(f"\nSaved: {out_png}")

    # 2D side views too — useful for spotting tiny RGB/depth offsets
    fig2, axs = plt.subplots(1, 3, figsize=(15, 5))
    for ax2, (ia, ib, lbl) in zip(axs,
                                  [(0, 1, "Top-down XY"),
                                   (0, 2, "Side XZ"),
                                   (2, 1, "Front ZY")]):
        for xyz, name, c in [(depth_xyz, "Depth", "#d62728"),
                             (rgb_xyz,   "RGB",   "#1f77b4"),
                             (head_xyz,  "Head",  "#2ca02c")]:
            m = ~np.isnan(xyz[:, 0])
            if m.any():
                ax2.plot(xyz[m, ia], xyz[m, ib], color=c, label=name, lw=1.1, alpha=0.85)
        ax2.set_title(lbl)
        ax2.set_aspect("equal", adjustable="datalim")
        ax2.grid(True, alpha=0.3)
        ax2.legend(fontsize=8)
    plt.suptitle(f"Trajectory projections — {session_dir.name}")
    out_png2 = session_dir / "trajectories_2d.png"
    plt.tight_layout()
    plt.savefig(out_png2, dpi=140)
    print(f"Saved: {out_png2}")

    # Interactive 3D (plotly) — rotatable, zoomable, with hover showing frame number
    fig3 = go.Figure()

    def add_plotly(xyz, name, color):
        m = ~np.isnan(xyz[:, 0])
        if not m.any():
            return
        fr = frames[m]
        pts = xyz[m]
        hover = [f"{name}<br>frame {f}<br>x={p[0]:.3f}<br>y={p[1]:.3f}<br>z={p[2]:.3f}"
                 for f, p in zip(fr, pts)]
        fig3.add_trace(go.Scatter3d(
            x=pts[:, 0], y=pts[:, 1], z=pts[:, 2],
            mode="lines+markers",
            name=name,
            line=dict(color=color, width=4),
            marker=dict(size=3, color=color),
            text=hover, hoverinfo="text",
        ))
        # start (circle) and end (diamond) emphasised
        fig3.add_trace(go.Scatter3d(
            x=[pts[0, 0]], y=[pts[0, 1]], z=[pts[0, 2]],
            mode="markers", marker=dict(size=8, color=color, symbol="circle",
                                         line=dict(color="black", width=1)),
            name=f"{name} start", showlegend=False,
        ))
        fig3.add_trace(go.Scatter3d(
            x=[pts[-1, 0]], y=[pts[-1, 1]], z=[pts[-1, 2]],
            mode="markers", marker=dict(size=8, color=color, symbol="diamond",
                                         line=dict(color="black", width=1)),
            name=f"{name} end", showlegend=False,
        ))

    add_plotly(depth_xyz, "Depth camera",        "#d62728")
    add_plotly(rgb_xyz,   "RGB camera",          "#1f77b4")
    add_plotly(head_xyz,  "Head (Camera.main)",  "#2ca02c")

    fig3.update_layout(
        title=f"Camera trajectories — {session_dir.name}  "
              f"(Unity world, RH on wire — circle=start, diamond=end)",
        scene=dict(
            xaxis_title="X (m)",
            yaxis_title="Y (m)",
            zaxis_title="Z (m)",
            aspectmode="data",
        ),
        legend=dict(itemsizing="constant"),
        margin=dict(l=0, r=0, t=40, b=0),
    )

    out_html = session_dir / "trajectories_3d.html"
    fig3.write_html(str(out_html), include_plotlyjs="cdn")
    print(f"Saved: {out_html}")


if __name__ == "__main__":
    main()
