"""Generated gRPC / protobuf stubs for the debug server.

The generated ``*_pb2_grpc.py`` files contain top-level imports like
``import xr_service_pb2`` — that's how grpc_tools.protoc emits them and
hand-editing them to use relative imports would just get clobbered on the
next regen. Instead, we put this directory on ``sys.path`` here so those
top-level imports resolve to the sibling file in this package.

Consumers should import via the package: ``from proto import xr_service_pb2``.
The internal cross-imports between generated modules then work transparently.

Regenerate with (run from debug_server/):
    python -m grpc_tools.protoc -I proto --python_out=proto \\
        --grpc_python_out=proto --pyi_out=proto proto/xr_service.proto
"""
import sys
from pathlib import Path

_pkg_dir = str(Path(__file__).resolve().parent)
if _pkg_dir not in sys.path:
    sys.path.insert(0, _pkg_dir)
