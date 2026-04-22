"""
SemanticXR Debug TCP Server

Receives length-prefixed protobuf messages from a Meta Quest 3S over plain TCP.
Protocol: "QUEST_STREAM\n" header, then repeated [4-byte big-endian length][protobuf].

Per-frame data received (UpstreamSyncMessage_quest):
  - H.265 encoded RGB frame (1280x960, left camera)
  - Depth map (320x320, R16_SFloat, left eye — R channel stripped on client)
  - Head pose (4x4 matrix, OpenXR right-handed convention)
  - Camera intrinsics (fx, fy, cx, cy)
  - Meta's depth ZBufferParams (invDepthFactor, depthOffset) for metric conversion

Output per session:
  frame_XXXXXX.h265   — Raw H.265 NAL units
  decoded_jpg/        — Decoded RGB as JPEG
  depth_XXXXXX.raw    — Raw depth bytes (R16G16B16A16_SFloat)
  depth_XXXXXX.npy    — Metric depth in meters (float32)
  depth_png/          — 16-bit PNG depth in millimeters
  meta_XXXXXX.txt     — Per-frame metadata (pose, intrinsics, depth params)

Depth conversion (see DEPTH_CONVERSION.md for full derivation):
  The preprocessed depth texture R channel stores an inverted NDC value.
  Metric conversion: depth_meters = sensor_near_z / R_channel_value
  where sensor_near_z = -invDepthFactor / 2 (extracted from Meta's ZBufferParams).
"""
import argparse
import time
import struct
import socket
import logging
import threading
from pathlib import Path

import av
import numpy as np
from PIL import Image

import xr_service_pb2

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("debug_server")

# Sessions live next to this script, not relative to cwd.
OUTPUT_DIR = Path(__file__).resolve().parent / "debug_output"

MAX_SESSION_NUMBER = 100


def create_next_session_dir(output_dir: Path):
    """Create and return the next session dir, never overwriting an existing one.
    Tries session_0..session_99 in order; returns None if all are taken.
    Epoch-named sessions from the old server are ignored for numbering purposes."""
    for n in range(MAX_SESSION_NUMBER):
        candidate = output_dir / f"session_{n}"
        try:
            candidate.mkdir(parents=True, exist_ok=False)
            return candidate
        except FileExistsError:
            continue
    return None

# Depth wire format: R16_SFloat = 1 half-float channel, 2 bytes per pixel.
# The client reads Meta's R16G16B16A16_SFloat _PreprocessedEnvironmentDepthTexture
# but strips to just the R channel before sending (G/B/A are unused — right-eye
# and soft-occlusion data we don't consume). See StreamingOrchestrator.cs depth
# readback callback. 4x bandwidth reduction vs shipping all 4 channels.
DEPTH_BYTES_PER_PIXEL = 2


class H265Decoder:
    """Stateful H.265 decoder using PyAV (libavcodec)."""

    def __init__(self):
        self._codec = av.CodecContext.create("hevc", "r")

    def decode_frame(self, h265_bytes):
        """Decode an H.265 NAL unit. Returns PIL Image or None."""
        if not h265_bytes:
            return None

        data = bytes(h265_bytes)
        if not data.startswith(b'\x00\x00\x00\x01') and not data.startswith(b'\x00\x00\x01'):
            data = b'\x00\x00\x00\x01' + data

        try:
            for frame in self._codec.decode(av.Packet(data)):
                return Image.fromarray(frame.to_ndarray(format="rgb24"))
        except (av.error.InvalidDataError, av.error.ValueError):
            pass
        return None


def decode_depth(depth_data, width, height, inv_depth_factor, depth_offset):
    """
    Decode raw depth bytes to metric depth in meters.

    Args:
        depth_data: Raw bytes, R16_SFloat (R channel stripped from Meta's
                    R16G16B16A16_SFloat texture on the client). 2 bytes/pixel.
        width, height: Depth image dimensions
        inv_depth_factor: Meta's ZBufferParams.x = -2 * sensor_near_z (infinite far)
                          or -2 * far * near / (far - near) (finite far)
        depth_offset: Meta's ZBufferParams.y = -1 (infinite far)
                      or -(far + near) / (far - near) (finite far)

    Returns:
        (depth_metric, success) where depth_metric is float32 array in meters,
        or (None, False) on failure.

    Conversion derivation (see DEPTH_CONVERSION.md):
        The R channel stores (1.0 - ndc_depth) from DepthPreprocessing.shader.
        Undoing the inversion and applying Meta's NDC-to-linear formula:
            raw_ndc = 1.0 - R
            ndc = raw_ndc * 2.0 - 1.0 = 1 - 2*R
            linear = invDepthFactor / (ndc + depthOffset)
                   = (-2*near) / (1 - 2*R + (-1))    [infinite far case]
                   = (-2*near) / (-2*R)
                   = near / R
        So: metric_depth = sensor_near_z / R_channel_value
    """
    expected_size = width * height * DEPTH_BYTES_PER_PIXEL
    if len(depth_data) != expected_size:
        return None, False

    # Parse as single-channel half-float. The client already flipped to top-down
    # and stripped to R only before sending, so this is a direct reshape.
    r_channel = np.frombuffer(depth_data, dtype=np.float16).reshape(height, width).astype(np.float32)

    if inv_depth_factor == 0:
        return r_channel, True  # No conversion params, return raw

    # Extract sensor near plane: invDepthFactor = -2 * near_z
    sensor_near_z = -inv_depth_factor / 2.0

    # Convert: metric_depth = sensor_near_z / R_channel_value
    safe_r = np.where(r_channel > 1e-4, r_channel, 1e-4)
    depth_metric = np.where(r_channel > 1e-4, sensor_near_z / safe_r, 0.0)
    depth_metric = np.clip(depth_metric, 0, 100.0)  # 100m max to avoid inf from tiny R values

    return depth_metric, True


def handle_client(conn, addr, session_dir, raw_only=True):
    """Handle a single Quest client connection.

    If raw_only=True (default), skips H.265 decode + JPG save and depth decode
    + NPY/PNG save. Only writes raw .h265, raw .raw, and meta.txt. Use
    decode_session.py to produce JPG/NPY/PNG outputs offline.
    """
    log.info(f"Client connected: {addr} (raw_only={raw_only})")

    # Read newline-terminated header
    header = b""
    while not header.endswith(b"\n"):
        chunk = conn.recv(1)
        if not chunk:
            log.warning("Client disconnected before header")
            return
        header += chunk

    header_str = header.decode().strip()
    if header_str != "QUEST_STREAM":
        log.error(f"Unknown protocol header: {header_str}")
        return

    log.info(f"Protocol: {header_str}")

    frame_count = 0
    start = time.time()
    decoder = None if raw_only else H265Decoder()
    jpg_dir = session_dir / "decoded_jpg"
    depth_dir = session_dir / "depth_png"
    if not raw_only:
        jpg_dir.mkdir(exist_ok=True)
        depth_dir.mkdir(exist_ok=True)

    # Per-stage timing accumulators. Averaged and logged every STATS_WINDOW frames
    # so the impact on the hot path is a few float adds per stage.
    STATS_WINDOW = 30
    t_acc = {"recv": 0.0, "parse": 0.0, "rawio": 0.0, "rgb": 0.0, "depth": 0.0, "meta": 0.0}
    n_since_stats = 0

    try:
        while True:
            # Read 4-byte big-endian message length
            t_recv_start = time.perf_counter()
            len_bytes = recv_exact(conn, 4)
            if len_bytes is None:
                break

            msg_len = struct.unpack(">I", len_bytes)[0]
            if msg_len > 50 * 1024 * 1024:
                log.error(f"Message too large: {msg_len} bytes")
                break

            data = recv_exact(conn, msg_len)
            if data is None:
                break
            t_recv_ms = (time.perf_counter() - t_recv_start) * 1000.0

            # Parse protobuf
            t0 = time.perf_counter()
            msg = xr_service_pb2.UpstreamSyncMessage_quest()
            msg.ParseFromString(data)
            t_parse_ms = (time.perf_counter() - t0) * 1000.0
            frame_count += 1

            img_msg = msg.image
            frame_num = img_msg.frame_number
            h265_data = img_msg.data_h265 if img_msg.HasField("data_h265") else b""
            depth_data = msg.depth
            intr = msg.intrinsics
            dw, dh = msg.depth_width, msg.depth_height

            # Meta's _EnvironmentDepthZBufferParams, sent via proto depth_near_z / depth_far_z fields.
            # These are NOT literal near/far distances — they are the raw ZBufferParams components.
            # See DEPTH_CONVERSION.md for the full explanation.
            inv_depth_factor = msg.depth_near_z  # ZBufferParams.x: -2*near (infinite far) or -2*f*n/(f-n)
            depth_offset = msg.depth_far_z       # ZBufferParams.y: -1 (infinite far) or -(f+n)/(f-n)

            # Derive the actual sensor near plane for logging
            sensor_near_z = -inv_depth_factor / 2.0 if inv_depth_factor != 0 else 0

            # --- Save raw H.265 + raw depth ---
            t0 = time.perf_counter()
            if h265_data:
                (session_dir / f"frame_{frame_num:06d}.h265").write_bytes(h265_data)
            if depth_data:
                (session_dir / f"depth_{frame_num:06d}.raw").write_bytes(depth_data)
            t_rawio_ms = (time.perf_counter() - t0) * 1000.0

            # --- Decode RGB (skipped in raw-only mode) ---
            t0 = time.perf_counter()
            decoded_img = None
            if not raw_only:
                decoded_img = decoder.decode_frame(h265_data)
                if decoded_img:
                    decoded_img.save(str(jpg_dir / f"frame_{frame_num:06d}.jpg"), quality=90)
            t_rgb_ms = (time.perf_counter() - t0) * 1000.0

            # --- Decode depth to metric meters (skipped in raw-only mode) ---
            t0 = time.perf_counter()
            depth_decoded = False
            if not raw_only and depth_data and dw > 0 and dh > 0:
                depth_metric, depth_decoded = decode_depth(
                    depth_data, dw, dh, inv_depth_factor, depth_offset
                )

                if depth_decoded and depth_metric is not None:
                    # Log stats periodically
                    if frame_count <= 3 or frame_count % 30 == 0:
                        valid = depth_metric[depth_metric > 0]
                        if len(valid) > 0:
                            log.info(
                                f"  Depth: min={valid.min():.2f}m max={valid.max():.2f}m "
                                f"median={np.median(valid):.2f}m "
                                f"sensor_near={sensor_near_z:.3f}m"
                            )

                    # Save metric depth as float32 .npy (exact values)
                    np.save(str(session_dir / f"depth_{frame_num:06d}.npy"), depth_metric)

                    # Save as 16-bit PNG in millimeters (max representable: 65.535m)
                    depth_mm = (depth_metric * 1000.0).clip(0, 65535).astype(np.uint16)
                    Image.fromarray(depth_mm, mode='I;16').save(
                        str(depth_dir / f"depth_{frame_num:06d}.png")
                    )
                elif frame_count <= 3:
                    log.warning(
                        f"  Depth size mismatch: got {len(depth_data)} B, "
                        f"expected {dw * dh * DEPTH_BYTES_PER_PIXEL} for {dw}x{dh}"
                    )
            t_depth_ms = (time.perf_counter() - t0) * 1000.0

            # --- Save metadata ---
            t0 = time.perf_counter()
            pose = list(msg.pose)
            with open(session_dir / f"meta_{frame_num:06d}.txt", "w") as f:
                f.write(f"frame_number: {frame_num}\n")
                f.write(f"timestamp_us: {img_msg.timestamp_us}\n")
                f.write(f"timestamp_ns: {msg.timestamp_ns}\n")
                f.write(f"image_size: {msg.image_width}x{msg.image_height}\n")
                f.write(f"depth_size: {dw}x{dh}\n")
                f.write(f"depth_format: R16_SFloat\n")
                f.write(f"depth_inv_depth_factor: {inv_depth_factor:.6f}\n")
                f.write(f"depth_offset: {depth_offset:.6f}\n")
                f.write(f"sensor_near_z: {sensor_near_z:.6f}\n")
                f.write(f"h265_bytes: {len(h265_data)}\n")
                f.write(f"depth_bytes: {len(depth_data)}\n")
                f.write(f"depth_decoded: {depth_decoded}\n")
                f.write(f"rgb_decoded: {decoded_img is not None}\n")
                f.write(f"fps_setting: {msg.fps}\n")
                f.write(f"intrinsics: fx={intr.fx:.4f} fy={intr.fy:.4f} cx={intr.cx:.4f} cy={intr.cy:.4f}\n")

                # Depth camera intrinsics (derived from FOV tangents, if available)
                dintr = msg.depth_intrinsics
                if dintr and (dintr.fx > 0 or dintr.fy > 0):
                    f.write(f"depth_intrinsics: fx={dintr.fx:.4f} fy={dintr.fy:.4f} cx={dintr.cx:.4f} cy={dintr.cy:.4f}\n")

                # Depth FOV tangents (source of truth from Meta SDK)
                fov_tan = list(msg.depth_fov_tangents)
                if len(fov_tan) >= 4:
                    f.write(f"depth_fov_tangents: L={fov_tan[0]:.6f} R={fov_tan[1]:.6f} T={fov_tan[2]:.6f} D={fov_tan[3]:.6f}\n")

                # Sensor timestamps (different clock domains!)
                f.write(f"rgb_timestamp_ns: {msg.rgb_timestamp_ns}\n")
                f.write(f"depth_timestamp_ns: {msg.depth_timestamp_ns}\n")

                # --- Poses (all RH after LH->RH conversion in TcpProtoClient) ---

                # Depth camera pose (from EnvironmentDepthFrameDesc, OpenXR tracking space)
                depth_pose = list(msg.depth_pose)
                if len(depth_pose) >= 16:
                    f.write(f"depth_pose (4x4):\n")
                    for row in range(4):
                        vals = depth_pose[row * 4:(row + 1) * 4]
                        f.write(f"  [{vals[0]:10.6f} {vals[1]:10.6f} {vals[2]:10.6f} {vals[3]:10.6f}]\n")

                # RGB camera pose (from PassthroughCameraAccess.GetCameraPose(), Unity world)
                rgb_cam_pose = list(msg.rgb_camera_pose)
                if len(rgb_cam_pose) >= 16:
                    f.write(f"rgb_camera_pose (4x4):\n")
                    for row in range(4):
                        vals = rgb_cam_pose[row * 4:(row + 1) * 4]
                        f.write(f"  [{vals[0]:10.6f} {vals[1]:10.6f} {vals[2]:10.6f} {vals[3]:10.6f}]\n")

                # Head pose (Camera.main eye center, Unity world) — also written as legacy 'pose'
                head_pose = list(msg.head_pose)
                if len(head_pose) < 16:
                    head_pose = pose  # fall back to legacy 'pose' field
                f.write(f"head_pose (4x4):\n")
                for row in range(4):
                    vals = head_pose[row * 4:(row + 1) * 4] if len(head_pose) >= 16 else [0] * 4
                    f.write(f"  [{vals[0]:10.6f} {vals[1]:10.6f} {vals[2]:10.6f} {vals[3]:10.6f}]\n")

                # Legacy 'pose' field (== head_pose, kept for backward compat)
                f.write(f"pose (4x4):\n")
                for row in range(4):
                    vals = pose[row * 4:(row + 1) * 4] if len(pose) >= 16 else [0] * 4
                    f.write(f"  [{vals[0]:10.6f} {vals[1]:10.6f} {vals[2]:10.6f} {vals[3]:10.6f}]\n")

            t_meta_ms = (time.perf_counter() - t0) * 1000.0

            # Work done after recv finished (i.e. how long the server blocks TCP
            # drain for this frame). If proc_ms > 1000/target_fps, TCP backpressure
            # will throttle the client — this is the server's FPS cap.
            proc_ms = t_parse_ms + t_rawio_ms + t_rgb_ms + t_depth_ms + t_meta_ms

            # Accumulate per-stage timings
            t_acc["recv"] += t_recv_ms
            t_acc["parse"] += t_parse_ms
            t_acc["rawio"] += t_rawio_ms
            t_acc["rgb"] += t_rgb_ms
            t_acc["depth"] += t_depth_ms
            t_acc["meta"] += t_meta_ms
            n_since_stats += 1

            # Periodic summary with per-stage averages and derived FPS cap
            if n_since_stats >= STATS_WINDOW:
                n = n_since_stats
                proc_avg = (t_acc["parse"] + t_acc["rawio"] + t_acc["rgb"]
                            + t_acc["depth"] + t_acc["meta"]) / n
                fps_cap = 1000.0 / proc_avg if proc_avg > 0 else 0
                log.info(
                    f"[Timing avg/{n}] recv={t_acc['recv']/n:5.1f}  "
                    f"parse={t_acc['parse']/n:5.1f}  rawIO={t_acc['rawio']/n:5.1f}  "
                    f"rgb={t_acc['rgb']/n:5.1f}  depth={t_acc['depth']/n:5.1f}  "
                    f"meta={t_acc['meta']/n:5.1f}  "
                    f"| proc={proc_avg:5.1f}ms fps_cap={fps_cap:4.1f}"
                )
                for k in t_acc:
                    t_acc[k] = 0.0
                n_since_stats = 0

            # --- Per-frame log line ---
            elapsed = time.time() - start
            fps_actual = frame_count / elapsed if elapsed > 0 else 0
            rgb_str = "raw" if raw_only else ("RGB" if decoded_img else "---")
            depth_str = "d"  if raw_only else ("D" if depth_decoded else "-")

            log.info(
                f"#{frame_num:4d} | "
                f"H265:{len(h265_data):7d}B | "
                f"Depth:{dw}x{dh} near={sensor_near_z:.2f}m | "
                f"RGB:{msg.image_width}x{msg.image_height} | "
                f"fx={intr.fx:.1f} | "
                f"{rgb_str}+{depth_str} | "
                f"proc={proc_ms:4.0f}ms | "
                f"{fps_actual:.1f}fps"
            )

    except Exception as ex:
        log.error(f"Error: {ex}", exc_info=True)
    finally:
        conn.close()
        elapsed = time.time() - start
        log.info(f"Disconnected. {frame_count} frames in {elapsed:.1f}s ({frame_count/max(elapsed,1):.1f} avg fps)")
        log.info(f"Session: {session_dir}")


def recv_exact(sock, n):
    """Read exactly n bytes from socket. Returns None on disconnect."""
    data = bytearray()
    while len(data) < n:
        chunk = sock.recv(n - len(data))
        if not chunk:
            return None
        data.extend(chunk)
    return bytes(data)


def serve(port=50055, raw_only=True):
    """Start the TCP server and listen for Quest connections."""
    OUTPUT_DIR.mkdir(exist_ok=True)

    hostname = socket.gethostname()
    try:
        local_ip = socket.gethostbyname(hostname)
    except Exception:
        local_ip = "unknown"

    server = socket.socket(socket.AF_INET, socket.SOCK_STREAM)
    server.setsockopt(socket.SOL_SOCKET, socket.SO_REUSEADDR, 1)
    server.settimeout(1.0)
    server.bind(("0.0.0.0", port))
    server.listen(5)

    log.info("========================================")
    log.info("  SemanticXR Debug Server")
    log.info(f"  Listening on 0.0.0.0:{port}")
    log.info(f"  Local IP: {local_ip}")
    log.info(f"  Output: {OUTPUT_DIR.absolute()}")
    log.info(f"  Mode: {'raw-only (fast)' if raw_only else 'decode (JPG/NPY/PNG)'}")
    log.info("========================================")

    try:
        while True:
            try:
                conn, addr = server.accept()
                session_dir = create_next_session_dir(OUTPUT_DIR)
                if session_dir is None:
                    log.error("========================================")
                    log.error("  All session_0..session_99 slots taken — OUT OF SLOTS!")
                    log.error("  Clear some space in %s before accepting new sessions.", OUTPUT_DIR.absolute())
                    log.error("========================================")
                    conn.close()
                    continue
                log.info(f"New session: {session_dir}")
                t = threading.Thread(target=handle_client, args=(conn, addr, session_dir, raw_only), daemon=True)
                t.start()
            except socket.timeout:
                continue
    except KeyboardInterrupt:
        log.info("\nShutting down...")
        server.close()


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="SemanticXR debug TCP server")
    ap.add_argument("port", nargs="?", type=int, default=50055,
                    help="TCP port to listen on (default 50055)")
    ap.add_argument("--decode", action="store_true",
                    help="Decode H.265 to JPG and depth to NPY/PNG on the fly. "
                         "Default is raw-only (much faster). Use decode_session.py "
                         "to produce decoded outputs from a raw session later.")
    args = ap.parse_args()
    serve(args.port, raw_only=not args.decode)
