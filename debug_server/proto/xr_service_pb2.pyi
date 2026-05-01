from google.protobuf.internal import containers as _containers
from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from collections.abc import Iterable as _Iterable, Mapping as _Mapping
from typing import ClassVar as _ClassVar, Optional as _Optional, Union as _Union

DESCRIPTOR: _descriptor.FileDescriptor

class VideoFrame(_message.Message):
    __slots__ = ("data_h265", "frame_number", "timestamp_us")
    DATA_H265_FIELD_NUMBER: _ClassVar[int]
    FRAME_NUMBER_FIELD_NUMBER: _ClassVar[int]
    TIMESTAMP_US_FIELD_NUMBER: _ClassVar[int]
    data_h265: bytes
    frame_number: int
    timestamp_us: int
    def __init__(self, data_h265: _Optional[bytes] = ..., frame_number: _Optional[int] = ..., timestamp_us: _Optional[int] = ...) -> None: ...

class VideoStatus(_message.Message):
    __slots__ = ("success",)
    SUCCESS_FIELD_NUMBER: _ClassVar[int]
    success: bool
    def __init__(self, success: bool = ...) -> None: ...

class PoseData(_message.Message):
    __slots__ = ("pose0_0", "pose0_1", "pose0_2", "pose0_3", "pose1_0", "pose1_1", "pose1_2", "pose1_3", "pose2_0", "pose2_1", "pose2_2", "pose2_3", "pose3_0", "pose3_1", "pose3_2", "pose3_3")
    POSE0_0_FIELD_NUMBER: _ClassVar[int]
    POSE0_1_FIELD_NUMBER: _ClassVar[int]
    POSE0_2_FIELD_NUMBER: _ClassVar[int]
    POSE0_3_FIELD_NUMBER: _ClassVar[int]
    POSE1_0_FIELD_NUMBER: _ClassVar[int]
    POSE1_1_FIELD_NUMBER: _ClassVar[int]
    POSE1_2_FIELD_NUMBER: _ClassVar[int]
    POSE1_3_FIELD_NUMBER: _ClassVar[int]
    POSE2_0_FIELD_NUMBER: _ClassVar[int]
    POSE2_1_FIELD_NUMBER: _ClassVar[int]
    POSE2_2_FIELD_NUMBER: _ClassVar[int]
    POSE2_3_FIELD_NUMBER: _ClassVar[int]
    POSE3_0_FIELD_NUMBER: _ClassVar[int]
    POSE3_1_FIELD_NUMBER: _ClassVar[int]
    POSE3_2_FIELD_NUMBER: _ClassVar[int]
    POSE3_3_FIELD_NUMBER: _ClassVar[int]
    pose0_0: float
    pose0_1: float
    pose0_2: float
    pose0_3: float
    pose1_0: float
    pose1_1: float
    pose1_2: float
    pose1_3: float
    pose2_0: float
    pose2_1: float
    pose2_2: float
    pose2_3: float
    pose3_0: float
    pose3_1: float
    pose3_2: float
    pose3_3: float
    def __init__(self, pose0_0: _Optional[float] = ..., pose0_1: _Optional[float] = ..., pose0_2: _Optional[float] = ..., pose0_3: _Optional[float] = ..., pose1_0: _Optional[float] = ..., pose1_1: _Optional[float] = ..., pose1_2: _Optional[float] = ..., pose1_3: _Optional[float] = ..., pose2_0: _Optional[float] = ..., pose2_1: _Optional[float] = ..., pose2_2: _Optional[float] = ..., pose2_3: _Optional[float] = ..., pose3_0: _Optional[float] = ..., pose3_1: _Optional[float] = ..., pose3_2: _Optional[float] = ..., pose3_3: _Optional[float] = ...) -> None: ...

class UpstreamSyncMessage(_message.Message):
    __slots__ = ("image", "depthArr", "pose", "fps", "max_depth_m", "depth_disabled")
    IMAGE_FIELD_NUMBER: _ClassVar[int]
    DEPTHARR_FIELD_NUMBER: _ClassVar[int]
    POSE_FIELD_NUMBER: _ClassVar[int]
    FPS_FIELD_NUMBER: _ClassVar[int]
    MAX_DEPTH_M_FIELD_NUMBER: _ClassVar[int]
    DEPTH_DISABLED_FIELD_NUMBER: _ClassVar[int]
    image: VideoFrame
    depthArr: _containers.RepeatedScalarFieldContainer[float]
    pose: PoseData
    fps: int
    max_depth_m: float
    depth_disabled: bool
    def __init__(self, image: _Optional[_Union[VideoFrame, _Mapping]] = ..., depthArr: _Optional[_Iterable[float]] = ..., pose: _Optional[_Union[PoseData, _Mapping]] = ..., fps: _Optional[int] = ..., max_depth_m: _Optional[float] = ..., depth_disabled: bool = ...) -> None: ...

class UpstreamSyncMessage_dataset(_message.Message):
    __slots__ = ("image", "depth", "pose", "fps", "scaling_factor", "timestamp_ns", "max_depth_m", "depth_disabled")
    IMAGE_FIELD_NUMBER: _ClassVar[int]
    DEPTH_FIELD_NUMBER: _ClassVar[int]
    POSE_FIELD_NUMBER: _ClassVar[int]
    FPS_FIELD_NUMBER: _ClassVar[int]
    SCALING_FACTOR_FIELD_NUMBER: _ClassVar[int]
    TIMESTAMP_NS_FIELD_NUMBER: _ClassVar[int]
    MAX_DEPTH_M_FIELD_NUMBER: _ClassVar[int]
    DEPTH_DISABLED_FIELD_NUMBER: _ClassVar[int]
    image: VideoFrame
    depth: bytes
    pose: _containers.RepeatedScalarFieldContainer[float]
    fps: int
    scaling_factor: int
    timestamp_ns: int
    max_depth_m: float
    depth_disabled: bool
    def __init__(self, image: _Optional[_Union[VideoFrame, _Mapping]] = ..., depth: _Optional[bytes] = ..., pose: _Optional[_Iterable[float]] = ..., fps: _Optional[int] = ..., scaling_factor: _Optional[int] = ..., timestamp_ns: _Optional[int] = ..., max_depth_m: _Optional[float] = ..., depth_disabled: bool = ...) -> None: ...

class CameraIntrinsics(_message.Message):
    __slots__ = ("fx", "fy", "cx", "cy")
    FX_FIELD_NUMBER: _ClassVar[int]
    FY_FIELD_NUMBER: _ClassVar[int]
    CX_FIELD_NUMBER: _ClassVar[int]
    CY_FIELD_NUMBER: _ClassVar[int]
    fx: float
    fy: float
    cx: float
    cy: float
    def __init__(self, fx: _Optional[float] = ..., fy: _Optional[float] = ..., cx: _Optional[float] = ..., cy: _Optional[float] = ...) -> None: ...

class UpstreamSyncMessage_quest(_message.Message):
    __slots__ = ("image", "depth", "pose", "intrinsics", "image_width", "image_height", "depth_width", "depth_height", "fps", "timestamp_ns", "depth_near_z", "depth_far_z", "depth_intrinsics", "depth_pose", "rgb_timestamp_ns", "depth_timestamp_ns", "head_pose", "rgb_camera_pose", "depth_fov_tangents", "max_depth_m", "depth_disabled")
    IMAGE_FIELD_NUMBER: _ClassVar[int]
    DEPTH_FIELD_NUMBER: _ClassVar[int]
    POSE_FIELD_NUMBER: _ClassVar[int]
    INTRINSICS_FIELD_NUMBER: _ClassVar[int]
    IMAGE_WIDTH_FIELD_NUMBER: _ClassVar[int]
    IMAGE_HEIGHT_FIELD_NUMBER: _ClassVar[int]
    DEPTH_WIDTH_FIELD_NUMBER: _ClassVar[int]
    DEPTH_HEIGHT_FIELD_NUMBER: _ClassVar[int]
    FPS_FIELD_NUMBER: _ClassVar[int]
    TIMESTAMP_NS_FIELD_NUMBER: _ClassVar[int]
    DEPTH_NEAR_Z_FIELD_NUMBER: _ClassVar[int]
    DEPTH_FAR_Z_FIELD_NUMBER: _ClassVar[int]
    DEPTH_INTRINSICS_FIELD_NUMBER: _ClassVar[int]
    DEPTH_POSE_FIELD_NUMBER: _ClassVar[int]
    RGB_TIMESTAMP_NS_FIELD_NUMBER: _ClassVar[int]
    DEPTH_TIMESTAMP_NS_FIELD_NUMBER: _ClassVar[int]
    HEAD_POSE_FIELD_NUMBER: _ClassVar[int]
    RGB_CAMERA_POSE_FIELD_NUMBER: _ClassVar[int]
    DEPTH_FOV_TANGENTS_FIELD_NUMBER: _ClassVar[int]
    MAX_DEPTH_M_FIELD_NUMBER: _ClassVar[int]
    DEPTH_DISABLED_FIELD_NUMBER: _ClassVar[int]
    image: VideoFrame
    depth: bytes
    pose: _containers.RepeatedScalarFieldContainer[float]
    intrinsics: CameraIntrinsics
    image_width: int
    image_height: int
    depth_width: int
    depth_height: int
    fps: int
    timestamp_ns: int
    depth_near_z: float
    depth_far_z: float
    depth_intrinsics: CameraIntrinsics
    depth_pose: _containers.RepeatedScalarFieldContainer[float]
    rgb_timestamp_ns: int
    depth_timestamp_ns: int
    head_pose: _containers.RepeatedScalarFieldContainer[float]
    rgb_camera_pose: _containers.RepeatedScalarFieldContainer[float]
    depth_fov_tangents: _containers.RepeatedScalarFieldContainer[float]
    max_depth_m: float
    depth_disabled: bool
    def __init__(self, image: _Optional[_Union[VideoFrame, _Mapping]] = ..., depth: _Optional[bytes] = ..., pose: _Optional[_Iterable[float]] = ..., intrinsics: _Optional[_Union[CameraIntrinsics, _Mapping]] = ..., image_width: _Optional[int] = ..., image_height: _Optional[int] = ..., depth_width: _Optional[int] = ..., depth_height: _Optional[int] = ..., fps: _Optional[int] = ..., timestamp_ns: _Optional[int] = ..., depth_near_z: _Optional[float] = ..., depth_far_z: _Optional[float] = ..., depth_intrinsics: _Optional[_Union[CameraIntrinsics, _Mapping]] = ..., depth_pose: _Optional[_Iterable[float]] = ..., rgb_timestamp_ns: _Optional[int] = ..., depth_timestamp_ns: _Optional[int] = ..., head_pose: _Optional[_Iterable[float]] = ..., rgb_camera_pose: _Optional[_Iterable[float]] = ..., depth_fov_tangents: _Optional[_Iterable[float]] = ..., max_depth_m: _Optional[float] = ..., depth_disabled: bool = ...) -> None: ...
