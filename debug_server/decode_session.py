"""Decode raw .h265 and .raw files in a raw-only session into .jpg and .npy.

Use this after capturing with unity_server.py (which defaults to raw-only mode).
Produces the same decoded_jpg/ and depth_{XXX}.npy files the legacy --decode
server mode would have produced, but offline so the capture stays fast.

Auto-detects depth format from file size:
  - 2 bytes/pixel = R16_SFloat       (post-R-only-strip client)
  - 8 bytes/pixel = R16G16B16A16_SFloat (legacy client)

Usage:
    python decode_session.py [session_dir]           # one session
    python decode_session.py --all                   # every session under debug_output/
"""
import argparse
import re
from pathlib import Path

import av
import numpy as np
from PIL import Image


def parse_meta(path):
    d = {}
    for line in path.read_text().splitlines():
        if ":" not in line:
            continue
        k, _, v = line.partition(":")
        d[k.strip()] = v.strip()
    return d


def decode_depth_raw(raw_bytes, w, h):
    """Parse raw depth bytes. Returns the R-channel as float32 (inverted NDC depth)."""
    pixels = w * h
    size = len(raw_bytes)
    if size == pixels * 2:
        return np.frombuffer(raw_bytes, dtype=np.float16).reshape(h, w).astype(np.float32)
    if size == pixels * 8:
        raw = np.frombuffer(raw_bytes, dtype=np.float16).reshape(h, w, 4)
        return raw[:, :, 0].astype(np.float32)
    raise ValueError(f"Unknown depth format: {size} bytes for {w}x{h}")


def metric_depth(r_channel, sensor_near_z):
    """R channel (inverted NDC) -> metric depth in meters."""
    if sensor_near_z <= 0:
        return r_channel
    safe_r = np.where(r_channel > 1e-4, r_channel, 1e-4)
    d = np.where(r_channel > 1e-4, sensor_near_z / safe_r, 0.0)
    return np.clip(d, 0, 100.0)


def decode_one_session(session: Path, verbose=True):
    if not session.is_dir():
        print(f"  skip (not a dir): {session}")
        return

    h265_files = sorted(session.glob("frame_*.h265"))
    raw_files = sorted(session.glob("depth_*.raw"))
    if not h265_files and not raw_files:
        print(f"  skip (no raw files): {session.name}")
        return

    print(f"Decoding {session.name}: {len(h265_files)} h265, {len(raw_files)} depth")
    jpg_dir = session / "decoded_jpg"
    png_dir = session / "depth_png"
    jpg_dir.mkdir(exist_ok=True)
    png_dir.mkdir(exist_ok=True)

    # --- H.265 -> JPG ---
    #
    # H.265 decoders are stateful. Every packet must be fed to the decoder
    # IN ORDER — skipping a packet (even one whose JPG already exists) breaks
    # the reference chain and subsequent frames fail to decode.
    #
    # A decode call returns 0, 1, or many frames:
    #   0 = packet buffered / parameter-set only / reference not yet available
    #   N = late frames unblocked by this packet
    # Whatever frame emerges corresponds to the Nth input packet in decode
    # order. We label outputs by input-packet frame_number taken in order.
    #
    # At end of stream, flush with decode(None) to drain buffered frames.
    codec = av.CodecContext.create("hevc", "r")
    rgb_done = rgb_fail = 0
    packet_order = []   # queue of frame numbers pending output
    for h in h265_files:
        m = re.search(r"frame_(\d+)\.h265", h.name)
        if not m:
            continue
        num = m.group(1)
        packet_order.append(num)
        data = h.read_bytes()
        if not data.startswith(b"\x00\x00\x00\x01") and not data.startswith(b"\x00\x00\x01"):
            data = b"\x00\x00\x00\x01" + data
        try:
            for frame in codec.decode(av.Packet(data)):
                if not packet_order:
                    break
                label = packet_order.pop(0)
                out = jpg_dir / f"frame_{label}.jpg"
                if not out.exists():
                    Image.fromarray(frame.to_ndarray(format="rgb24")).save(str(out), quality=90)
                rgb_done += 1
        except (av.error.InvalidDataError, av.error.ValueError):
            rgb_fail += 1
    # Flush buffered frames
    try:
        for frame in codec.decode(None):
            if not packet_order:
                break
            label = packet_order.pop(0)
            out = jpg_dir / f"frame_{label}.jpg"
            if not out.exists():
                Image.fromarray(frame.to_ndarray(format="rgb24")).save(str(out), quality=90)
            rgb_done += 1
    except Exception:
        pass
    rgb_pending = len(packet_order)   # packets that never emerged (true failures)

    # --- depth .raw -> .npy + .png ---
    depth_done = depth_skip = depth_fail = 0
    for r in raw_files:
        m = re.search(r"depth_(\d+)\.raw", r.name)
        if not m:
            continue
        num = m.group(1)
        out_npy = session / f"depth_{num}.npy"
        if out_npy.exists():
            depth_skip += 1
            continue
        meta_path = session / f"meta_{num}.txt"
        if not meta_path.exists():
            depth_fail += 1
            continue
        meta = parse_meta(meta_path)
        try:
            dw, dh = [int(x) for x in meta["depth_size"].split("x")]
        except Exception:
            depth_fail += 1
            continue
        try:
            sensor_near = float(meta.get("sensor_near_z", 0))
        except ValueError:
            sensor_near = 0.0
        try:
            r_channel = decode_depth_raw(r.read_bytes(), dw, dh)
        except ValueError as e:
            if verbose:
                print(f"  depth {num} parse failed: {e}")
            depth_fail += 1
            continue
        depth = metric_depth(r_channel, sensor_near)
        np.save(str(out_npy), depth)
        if sensor_near > 0:
            mm = (depth * 1000.0).clip(0, 65535).astype(np.uint16)
            Image.fromarray(mm, mode="I;16").save(str(png_dir / f"depth_{num}.png"))
        depth_done += 1

    print(f"  RGB: {rgb_done} decoded, {rgb_pending} buffered (never emerged), {rgb_fail} packet errors")
    print(f"  Depth: {depth_done} decoded, {depth_skip} already done, {depth_fail} failed")


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument("session_dir", nargs="?", default=None)
    ap.add_argument("--all", action="store_true",
                    help="Decode every session under debug_output/")
    args = ap.parse_args()

    base = Path(__file__).resolve().parent / "debug_output"

    if args.all:
        sessions = sorted(
            (p for p in base.glob("session_*") if p.is_dir()),
            key=lambda p: p.name,
        )
        if not sessions:
            print("No sessions in", base)
            return
        for s in sessions:
            decode_one_session(s)
        return

    if args.session_dir:
        session = Path(args.session_dir)
    else:
        sessions = sorted(base.glob("session_*"), key=lambda p: p.stat().st_mtime)
        if not sessions:
            print("No sessions in", base)
            return
        session = sessions[-1]
        print(f"Auto-selected: {session}")

    decode_one_session(session)


if __name__ == "__main__":
    main()
