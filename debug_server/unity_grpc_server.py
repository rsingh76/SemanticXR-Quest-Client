"""
SemanticXR Debug gRPC Server — gRPC sibling of unity_server.py.

Receives UpstreamSyncMessage_quest over gRPC (XrService.UploadSyncMessage_quest
client-streaming RPC) from a Meta Quest 3S. All per-frame output — files
written, meta.txt layout, decode behavior, log format — matches unity_server.py
so decode_session.py and downstream tooling work on either transport.

Per-frame data received (UpstreamSyncMessage_quest):
  - H.265 encoded RGB frame
  - Depth map (R16_SFloat)
  - Head / RGB camera / depth camera poses (4x4, already LH->RH converted on client)
  - Camera intrinsics
  - Meta's depth ZBufferParams

Output per session — identical to unity_server.py:
  frame_XXXXXX.h265   — Raw H.265 NAL units
  decoded_jpg/        — Decoded RGB as JPEG (when --decode)
  depth_XXXXXX.raw    — Raw depth bytes
  depth_XXXXXX.npy    — Metric depth in meters (when --decode)
  depth_png/          — 16-bit PNG depth in millimeters (when --decode)
  meta_XXXXXX.txt     — Per-frame metadata
"""
import argparse
import logging
import socket
import struct
import time
from concurrent import futures
from pathlib import Path

import av
import grpc
import numpy as np
from PIL import Image

import xr_service_pb2
import xr_service_pb2_grpc

logging.basicConfig(level=logging.INFO, format="%(asctime)s [%(levelname)s] %(message)s")
log = logging.getLogger("debug_grpc_server")

OUTPUT_DIR = Path(__file__).resolve().parent / "debug_output"
MAX_SESSION_NUMBER = 100
DEPTH_BYTES_PER_PIXEL = 2
STATS_WINDOW = 30


def create_next_session_dir(output_dir: Path):
    for n in range(MAX_SESSION_NUMBER):
        candidate = output_dir / f"session_{n}"
        try:
            candidate.mkdir(parents=True, exist_ok=False)
            return candidate
        except FileExistsError:
            continue
    return None


class H265Decoder:
    """Stateful H.265 decoder using PyAV (libavcodec)."""
    def __init__(self):
        self._codec = av.CodecContext.create("hevc", "r")

    def decode_frame(self, h265_bytes):
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
    """Decode raw R16_SFloat depth to metric depth in meters. See unity_server.py for math."""
    expected = width * height * DEPTH_BYTES_PER_PIXEL
    if len(depth_data) != expected:
        return None, False
    r = np.frombuffer(depth_data, dtype=np.float16).astype(np.float32).reshape(height, width)
    if inv_depth_factor == 0:
        return None, False
    sensor_near_z = -inv_depth_factor / 2.0
    with np.errstate(divide='ignore', invalid='ignore'):
        depth_metric = np.where(r > 1e-6, sensor_near_z / r, 0.0).astype(np.float32)
    return depth_metric, True


def write_meta(session_dir: Path, msg, h265_data, depth_data, sensor_near_z,
               inv_depth_factor, depth_offset, depth_decoded, rgb_decoded):
    """Matches unity_server.py meta.txt layout byte-for-byte."""
    img = msg.image
    frame_num = img.frame_number
    intr = msg.intrinsics
    pose = list(msg.pose)
    dw, dh = msg.depth_width, msg.depth_height

    with open(session_dir / f"meta_{frame_num:06d}.txt", "w") as f:
        f.write(f"frame_number: {frame_num}\n")
        f.write(f"timestamp_us: {img.timestamp_us}\n")
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
        f.write(f"rgb_decoded: {rgb_decoded}\n")
        f.write(f"fps_setting: {msg.fps}\n")
        f.write(f"intrinsics: fx={intr.fx:.4f} fy={intr.fy:.4f} cx={intr.cx:.4f} cy={intr.cy:.4f}\n")

        dintr = msg.depth_intrinsics
        if dintr and (dintr.fx > 0 or dintr.fy > 0):
            f.write(f"depth_intrinsics: fx={dintr.fx:.4f} fy={dintr.fy:.4f} cx={dintr.cx:.4f} cy={dintr.cy:.4f}\n")

        fov_tan = list(msg.depth_fov_tangents)
        if len(fov_tan) >= 4:
            f.write(f"depth_fov_tangents: L={fov_tan[0]:.6f} R={fov_tan[1]:.6f} T={fov_tan[2]:.6f} D={fov_tan[3]:.6f}\n")

        f.write(f"rgb_timestamp_ns: {msg.rgb_timestamp_ns}\n")
        f.write(f"depth_timestamp_ns: {msg.depth_timestamp_ns}\n")

        depth_pose = list(msg.depth_pose)
        if len(depth_pose) >= 16:
            f.write(f"depth_pose (4x4):\n")
            for row in range(4):
                v = depth_pose[row * 4:(row + 1) * 4]
                f.write(f"  [{v[0]:10.6f} {v[1]:10.6f} {v[2]:10.6f} {v[3]:10.6f}]\n")

        rgb_cam_pose = list(msg.rgb_camera_pose)
        if len(rgb_cam_pose) >= 16:
            f.write(f"rgb_camera_pose (4x4):\n")
            for row in range(4):
                v = rgb_cam_pose[row * 4:(row + 1) * 4]
                f.write(f"  [{v[0]:10.6f} {v[1]:10.6f} {v[2]:10.6f} {v[3]:10.6f}]\n")

        head_pose = list(msg.head_pose)
        if len(head_pose) < 16:
            head_pose = pose
        f.write(f"head_pose (4x4):\n")
        for row in range(4):
            v = head_pose[row * 4:(row + 1) * 4] if len(head_pose) >= 16 else [0] * 4
            f.write(f"  [{v[0]:10.6f} {v[1]:10.6f} {v[2]:10.6f} {v[3]:10.6f}]\n")

        f.write(f"pose (4x4):\n")
        for row in range(4):
            v = pose[row * 4:(row + 1) * 4] if len(pose) >= 16 else [0] * 4
            f.write(f"  [{v[0]:10.6f} {v[1]:10.6f} {v[2]:10.6f} {v[3]:10.6f}]\n")


class FramesServicer(xr_service_pb2_grpc.XrServiceServicer):
    """Mirrors unity_server.handle_client. raw_only controlled at construction."""

    def __init__(self, raw_only: bool):
        self._raw_only = raw_only

    def UploadSyncMessage_quest(self, request_iterator, context):
        session_dir = create_next_session_dir(OUTPUT_DIR)
        if session_dir is None:
            log.error("========================================")
            log.error("  All session_0..session_99 slots taken — OUT OF SLOTS!")
            log.error("  Clear some space in %s before accepting new sessions.", OUTPUT_DIR.absolute())
            log.error("========================================")
            context.abort(grpc.StatusCode.RESOURCE_EXHAUSTED, "No session slot available")

        peer = context.peer()
        log.info(f"Client connected: {peer} (raw_only={self._raw_only})")
        log.info(f"New session: {session_dir}")

        decoder = None if self._raw_only else H265Decoder()
        jpg_dir = session_dir / "decoded_jpg"
        depth_dir = session_dir / "depth_png"
        if not self._raw_only:
            jpg_dir.mkdir(exist_ok=True)
            depth_dir.mkdir(exist_ok=True)

        frame_count = 0
        start = time.time()
        t_acc = {"recv": 0.0, "parse": 0.0, "rawio": 0.0, "rgb": 0.0, "depth": 0.0, "meta": 0.0}
        n_since_stats = 0

        # Time spent waiting for the next request in the iterator counts as "recv".
        # gRPC parses the protobuf inline, so "parse" stays ~0 here (the field is
        # kept to preserve the same log columns as unity_server.py).
        try:
            it = iter(request_iterator)
            t_prev = time.perf_counter()
            for msg in it:
                t_recv_ms = (time.perf_counter() - t_prev) * 1000.0
                t_parse_ms = 0.0

                frame_count += 1
                img_msg = msg.image
                frame_num = img_msg.frame_number
                h265_data = img_msg.data_h265 if img_msg.HasField("data_h265") else b""
                depth_data = msg.depth
                intr = msg.intrinsics
                dw, dh = msg.depth_width, msg.depth_height

                inv_depth_factor = msg.depth_near_z
                depth_offset = msg.depth_far_z
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
                if not self._raw_only:
                    decoded_img = decoder.decode_frame(h265_data)
                    if decoded_img:
                        decoded_img.save(str(jpg_dir / f"frame_{frame_num:06d}.jpg"), quality=90)
                t_rgb_ms = (time.perf_counter() - t0) * 1000.0

                # --- Decode depth (skipped in raw-only mode) ---
                t0 = time.perf_counter()
                depth_decoded = False
                if not self._raw_only and depth_data and dw > 0 and dh > 0:
                    depth_metric, depth_decoded = decode_depth(
                        depth_data, dw, dh, inv_depth_factor, depth_offset
                    )
                    if depth_decoded and depth_metric is not None:
                        if frame_count <= 3 or frame_count % 30 == 0:
                            valid = depth_metric[depth_metric > 0]
                            if len(valid) > 0:
                                log.info(
                                    f"  Depth: min={valid.min():.2f}m max={valid.max():.2f}m "
                                    f"median={np.median(valid):.2f}m "
                                    f"sensor_near={sensor_near_z:.3f}m"
                                )
                        np.save(str(session_dir / f"depth_{frame_num:06d}.npy"), depth_metric)
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
                write_meta(
                    session_dir, msg, h265_data, depth_data, sensor_near_z,
                    inv_depth_factor, depth_offset,
                    depth_decoded=depth_decoded,
                    rgb_decoded=(decoded_img is not None),
                )
                t_meta_ms = (time.perf_counter() - t0) * 1000.0

                proc_ms = t_parse_ms + t_rawio_ms + t_rgb_ms + t_depth_ms + t_meta_ms

                t_acc["recv"]  += t_recv_ms
                t_acc["parse"] += t_parse_ms
                t_acc["rawio"] += t_rawio_ms
                t_acc["rgb"]   += t_rgb_ms
                t_acc["depth"] += t_depth_ms
                t_acc["meta"]  += t_meta_ms
                n_since_stats += 1

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

                elapsed = time.time() - start
                fps_actual = frame_count / elapsed if elapsed > 0 else 0
                rgb_str = "raw" if self._raw_only else ("RGB" if decoded_img else "---")
                depth_str = "d" if self._raw_only else ("D" if depth_decoded else "-")
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

                t_prev = time.perf_counter()

        except Exception as ex:
            log.error(f"Error: {ex}", exc_info=True)
        finally:
            elapsed = time.time() - start
            log.info(f"Disconnected. {frame_count} frames in {elapsed:.1f}s ({frame_count/max(elapsed,1):.1f} avg fps)")
            log.info(f"Session: {session_dir}")

        return xr_service_pb2.VideoStatus(success=True)

    # Unused RPCs stubbed to avoid UNIMPLEMENTED if someone else connects.
    def UploadSyncMessage(self, request_iterator, context):
        for _ in request_iterator:
            pass
        return xr_service_pb2.VideoStatus(success=True)

    def UploadSyncMessage_dataset(self, request_iterator, context):
        for _ in request_iterator:
            pass
        return xr_service_pb2.VideoStatus(success=True)


def serve(port: int, raw_only: bool):
    OUTPUT_DIR.mkdir(exist_ok=True)

    hostname = socket.gethostname()
    try:
        local_ip = socket.gethostbyname(hostname)
    except Exception:
        local_ip = "unknown"

    server = grpc.server(
        futures.ThreadPoolExecutor(max_workers=4),
        options=[
            ("grpc.max_receive_message_length", 100 * 1024 * 1024),
            ("grpc.max_send_message_length",    100 * 1024 * 1024),
        ],
    )
    xr_service_pb2_grpc.add_XrServiceServicer_to_server(FramesServicer(raw_only), server)
    server.add_insecure_port(f"[::]:{port}")

    log.info("========================================")
    log.info("  SemanticXR Debug gRPC Server")
    log.info(f"  Listening on 0.0.0.0:{port}")
    log.info(f"  Local IP: {local_ip}")
    log.info(f"  Output: {OUTPUT_DIR.absolute()}")
    log.info(f"  Mode: {'raw-only (fast)' if raw_only else 'decode (JPG/NPY/PNG)'}")
    log.info("========================================")

    server.start()
    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        log.info("\nShutting down...")
        server.stop(grace=1.0)


if __name__ == "__main__":
    ap = argparse.ArgumentParser(description="SemanticXR debug gRPC server")
    ap.add_argument("port", nargs="?", type=int, default=50051,
                    help="gRPC port to listen on (default 50051)")
    ap.add_argument("--decode", action="store_true",
                    help="Decode H.265 to JPG and depth to NPY/PNG on the fly. "
                         "Default is raw-only (much faster). Use decode_session.py "
                         "to produce decoded outputs from a raw session later.")
    args = ap.parse_args()
    serve(args.port, raw_only=not args.decode)
