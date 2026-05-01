"""
Plot 3D trajectories of depth, RGB, and head camera poses from a debug session.

Session I/O goes through ``session_io.Session`` — see that module's docstring
for the on-disk layout and pose conventions.

Usage:
    python plot_trajectories.py [session_dir]
    (defaults to the most recent session in debug_output/)
"""
import sys
from pathlib import Path

import numpy as np
import matplotlib.pyplot as plt
import plotly.graph_objects as go

from session_io import Session


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

    session = Session(session_dir)
    print(f"Session: {session.root}")

    frame_nums = session.frames_with_meta()
    print(f"Meta files: {len(frame_nums)}")

    frames, depth_xyz, rgb_xyz, head_xyz = [], [], [], []

    for fn in frame_nums:
        meta = session.load_meta(fn)
        d = meta.get("depth_pose_matrix")
        r = meta.get("rgb_camera_pose_matrix")
        h = meta.get("head_pose_matrix")
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
    ax.set_title(f"Camera trajectories — {session.root.name}\n"
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

    out_png = session.root / "trajectories_3d.png"
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
    plt.suptitle(f"Trajectory projections — {session.root.name}")
    out_png2 = session.root / "trajectories_2d.png"
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
        title=f"Camera trajectories — {session.root.name}  "
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

    out_html = session.root / "trajectories_3d.html"
    fig3.write_html(str(out_html), include_plotlyjs="cdn")
    print(f"Saved: {out_html}")


if __name__ == "__main__":
    main()
