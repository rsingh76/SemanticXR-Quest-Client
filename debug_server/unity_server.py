"""
SemanticXR Debug TCP Server

Receives length-prefixed protobuf messages from a Meta Quest 3S over plain TCP.
Protocol: "QUEST_STREAM\n" header, then repeated [4-byte big-endian length][protobuf].

Per-frame data received (UpstreamSyncMessage_quest):
  - H.265 encoded RGB frame (1280x960, left camera)
  - Depth map (320x320, R16G16B16A16_SFloat, left eye)
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
import sys
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

OUTPUT_DIR = Path("debug_output")

MAX_SESSION_NUMBER = 100


def next_session_number(output_dir: Path):
    """Return the next session number: max existing session_N (0 <= N < 100) + 1,
    or 0 if no sessions exist. Returns None if session_100 exists (out of slots)."""
    if (output_dir / f"session_{MAX_SESSION_NUMBER}").exists():
        return None
    highest = -1
    for entry in output_dir.iterdir() if output_dir.exists() else []:
        if not entry.is_dir() or not entry.name.startswith("session_"):
            continue
        suffix = entry.name[len("session_"):]
        if not suffix.isdigit():
            continue
        n = int(suffix)
        if n >= MAX_SESSION_NUMBER:
            continue
        if n > highest:
            highest = n
    return highest + 1

# Depth texture format: R16G16B16A16_SFloat = 4 half-float channels, 8 bytes per pixel.
# R channel = inverted NDC depth (from Meta's DepthPreprocessing.shader)
# G channel = same for right eye (unused here)
# B, A channels = edge softness data for soft occlusion (unused here)
DEPTH_BYTES_PER_PIXEL = 8


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
        depth_data: Raw bytes from _PreprocessedEnvironmentDepthTexture (R16G16B16A16_SFloat)
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

    # Parse as 4-channel half-float. The client already flips depth to top-down
    # before sending (see StreamingOrchestrator.cs), so no flip needed here.
    raw = np.frombuffer(depth_data, dtype=np.float16).reshape(height, width, 4)
    r_channel = raw[:, :, 0].astype(np.float32)

    if inv_depth_factor == 0:
        return r_channel, True  # No conversion params, return raw

    # Extract sensor near plane: invDepthFactor = -2 * near_z
    sensor_near_z = -inv_depth_factor / 2.0

    # Convert: metric_depth = sensor_near_z / R_channel_value
    safe_r = np.where(r_channel > 1e-4, r_channel, 1e-4)
    depth_metric = np.where(r_channel > 1e-4, sensor_near_z / safe_r, 0.0)
    depth_metric = np.clip(depth_metric, 0, 100.0)  # 100m max to avoid inf from tiny R values

    return depth_metric, True


def handle_client(conn, addr, session_dir):
    """Handle a single Quest client connection."""
    log.info(f"Client connected: {addr}")

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
    decoder = H265Decoder()
    jpg_dir = session_dir / "decoded_jpg"
    depth_dir = session_dir / "depth_png"
    jpg_dir.mkdir(exist_ok=True)
    depth_dir.mkdir(exist_ok=True)

    try:
        while True:
            # Read 4-byte big-endian message length
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

            # Parse protobuf
            msg = xr_service_pb2.UpstreamSyncMessage_quest()
            msg.ParseFromString(data)
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

            # --- Save raw H.265 ---
            if h265_data:
                (session_dir / f"frame_{frame_num:06d}.h265").write_bytes(h265_data)

            # --- Save raw depth ---
            if depth_data:
                (session_dir / f"depth_{frame_num:06d}.raw").write_bytes(depth_data)

            # --- Decode RGB ---
            decoded_img = decoder.decode_frame(h265_data)
            if decoded_img:
                decoded_img.save(str(jpg_dir / f"frame_{frame_num:06d}.jpg"), quality=90)

            # --- Decode depth to metric meters ---
            depth_decoded = False
            if depth_data and dw > 0 and dh > 0:
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

            # --- Save metadata ---
            pose = list(msg.pose)
            with open(session_dir / f"meta_{frame_num:06d}.txt", "w") as f:
                f.write(f"frame_number: {frame_num}\n")
                f.write(f"timestamp_us: {img_msg.timestamp_us}\n")
                f.write(f"timestamp_ns: {msg.timestamp_ns}\n")
                f.write(f"image_size: {msg.image_width}x{msg.image_height}\n")
                f.write(f"depth_size: {dw}x{dh}\n")
                f.write(f"depth_format: R16G16B16A16_SFloat\n")
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

            # --- Per-frame log line ---
            elapsed = time.time() - start
            fps_actual = frame_count / elapsed if elapsed > 0 else 0
            rgb_str = "RGB" if decoded_img else "---"
            depth_str = "D" if depth_decoded else "-"

            log.info(
                f"#{frame_num:4d} | "
                f"H265:{len(h265_data):7d}B | "
                f"Depth:{dw}x{dh} near={sensor_near_z:.2f}m | "
                f"RGB:{msg.image_width}x{msg.image_height} | "
                f"fx={intr.fx:.1f} | "
                f"{rgb_str}+{depth_str} | "
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


def serve(port=50051):
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
    log.info("========================================")

    try:
        while True:
            try:
                conn, addr = server.accept()
                session_num = next_session_number(OUTPUT_DIR)
                if session_num is None:
                    log.error("========================================")
                    log.error("  session_100 exists — OUT OF SESSION SLOTS!")
                    log.error("  Clear some space in %s before accepting new sessions.", OUTPUT_DIR.absolute())
                    log.error("========================================")
                    conn.close()
                    continue
                session_dir = OUTPUT_DIR / f"session_{session_num}"
                session_dir.mkdir(parents=True, exist_ok=True)
                t = threading.Thread(target=handle_client, args=(conn, addr, session_dir), daemon=True)
                t.start()
            except socket.timeout:
                continue
    except KeyboardInterrupt:
        log.info("\nShutting down...")
        server.close()


if __name__ == "__main__":
    port = int(sys.argv[1]) if len(sys.argv) > 1 else 50051
    serve(port)
