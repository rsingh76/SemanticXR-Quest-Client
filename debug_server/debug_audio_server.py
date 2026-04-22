"""Debug gRPC server for audio streaming from the Quest app.

Implements the `VisualizerServer.clientTextQuery` RPC from vis.proto. Received
audio chunks are accumulated, wrapped in a proper WAV header (12 kHz / mono /
16-bit PCM, matching what `slam/services/visualization_service.py` expects),
and written to disk under debug_output/audio/. The response is an empty
`allPointClouds` — no Whisper, no CLIP, no SLAM. This is just a bytes-arrived
smoke test.

Run alongside the frames servers (unity_grpc_server.py on 50051 or unity_server.py on 50055):

    python debug_audio_server.py          # listens on 50054

Regenerate the Python stubs if vis.proto ever changes:

    python -m grpc_tools.protoc -I. --python_out=. --grpc_python_out=. vis.proto
"""

import argparse
import logging
import struct
import sys
import time
from concurrent import futures
from pathlib import Path

import grpc

import vis_pb2
import vis_pb2_grpc

# Matches slam/services/visualization_service.py::WriteWavHeader defaults.
SAMPLE_RATE    = 12000
NUM_CHANNELS   = 1
BITS_PER_SAMPLE = 16

log = logging.getLogger("debug_audio")


def write_wav(path: Path, pcm_bytes: bytes):
    """Wrap raw PCM bytes in a WAV header and write to disk.

    The server's existing handler does the same thing in struct-packed form.
    Duplicating it here keeps the test tool fully standalone.
    """
    path.parent.mkdir(parents=True, exist_ok=True)
    byte_rate   = SAMPLE_RATE * NUM_CHANNELS * BITS_PER_SAMPLE // 8
    block_align = NUM_CHANNELS * BITS_PER_SAMPLE // 8
    data_size   = len(pcm_bytes)
    file_size   = 36 + data_size

    with open(path, "wb") as f:
        f.write(b"RIFF")
        f.write(struct.pack("<I", file_size))
        f.write(b"WAVE")
        f.write(b"fmt ")
        f.write(struct.pack("<I", 16))                  # PCM header size
        f.write(struct.pack("<H", 1))                   # PCM format
        f.write(struct.pack("<H", NUM_CHANNELS))
        f.write(struct.pack("<I", SAMPLE_RATE))
        f.write(struct.pack("<I", byte_rate))
        f.write(struct.pack("<H", block_align))
        f.write(struct.pack("<H", BITS_PER_SAMPLE))
        f.write(b"data")
        f.write(struct.pack("<I", data_size))
        f.write(pcm_bytes)


class AudioDumpServicer(vis_pb2_grpc.VisualizerServerServicer):
    def __init__(self, output_dir: Path):
        self.output_dir = output_dir

    def clientTextQuery(self, request_iterator, context):
        chunks = []
        text_queries = []
        for msg in request_iterator:
            if msg.chunk_data:
                chunks.append(msg.chunk_data)
            if msg.textQuery:
                text_queries.append(msg.textQuery)

        pcm = b"".join(chunks)
        duration_sec = len(pcm) / (SAMPLE_RATE * NUM_CHANNELS * BITS_PER_SAMPLE / 8)
        timestamp = time.strftime("%Y%m%d_%H%M%S")
        wav_path = self.output_dir / f"audio_{timestamp}.wav"

        if pcm:
            write_wav(wav_path, pcm)
            log.info("Received %d bytes (%.2fs) -> %s", len(pcm), duration_sec, wav_path)
        else:
            log.info("Received empty audio stream (text_query=%r)", text_queries)

        if text_queries:
            log.info("Text query fields: %r", text_queries)

        # Empty response — this debug server doesn't run the SLAM pipeline.
        return vis_pb2.allPointClouds(numPointClouds=0)

    # Required by the servicer contract even though we don't exercise them here.
    def updateMap(self, request, context):
        return vis_pb2.Status(message=True)

    def updateDeviceMap(self, request_iterator, context):
        for _ in request_iterator:
            pass
        return vis_pb2.Status(message=True)


def serve(port: int, output_dir: Path):
    server = grpc.server(futures.ThreadPoolExecutor(max_workers=2))
    vis_pb2_grpc.add_VisualizerServerServicer_to_server(
        AudioDumpServicer(output_dir), server)
    server.add_insecure_port(f"[::]:{port}")
    server.start()
    log.info("Listening on port %d, dumping to %s", port, output_dir.resolve())
    try:
        server.wait_for_termination()
    except KeyboardInterrupt:
        log.info("Shutting down.")
        server.stop(grace=1.0)


if __name__ == "__main__":
    logging.basicConfig(level=logging.INFO, format="[%(asctime)s] %(message)s")
    ap = argparse.ArgumentParser()
    ap.add_argument("--port", type=int, default=50054)
    ap.add_argument("--out", type=Path, default=Path(__file__).parent / "debug_output" / "audio")
    args = ap.parse_args()
    serve(args.port, args.out)
