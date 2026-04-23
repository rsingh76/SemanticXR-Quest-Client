from google.protobuf.internal import containers as _containers
from google.protobuf import descriptor as _descriptor
from google.protobuf import message as _message
from collections.abc import Iterable as _Iterable, Mapping as _Mapping
from typing import ClassVar as _ClassVar, Optional as _Optional, Union as _Union

DESCRIPTOR: _descriptor.FileDescriptor

class PointCloud(_message.Message):
    __slots__ = ("points", "num_points", "centroid")
    POINTS_FIELD_NUMBER: _ClassVar[int]
    NUM_POINTS_FIELD_NUMBER: _ClassVar[int]
    CENTROID_FIELD_NUMBER: _ClassVar[int]
    points: _containers.RepeatedScalarFieldContainer[float]
    num_points: int
    centroid: _containers.RepeatedScalarFieldContainer[float]
    def __init__(self, points: _Optional[_Iterable[float]] = ..., num_points: _Optional[int] = ..., centroid: _Optional[_Iterable[float]] = ...) -> None: ...

class allPointClouds(_message.Message):
    __slots__ = ("pointClouds", "colors", "numPointClouds", "serverQueryProcessing", "textQuery")
    POINTCLOUDS_FIELD_NUMBER: _ClassVar[int]
    COLORS_FIELD_NUMBER: _ClassVar[int]
    NUMPOINTCLOUDS_FIELD_NUMBER: _ClassVar[int]
    SERVERQUERYPROCESSING_FIELD_NUMBER: _ClassVar[int]
    TEXTQUERY_FIELD_NUMBER: _ClassVar[int]
    pointClouds: _containers.RepeatedCompositeFieldContainer[PointCloud]
    colors: _containers.RepeatedScalarFieldContainer[float]
    numPointClouds: int
    serverQueryProcessing: float
    textQuery: str
    def __init__(self, pointClouds: _Optional[_Iterable[_Union[PointCloud, _Mapping]]] = ..., colors: _Optional[_Iterable[float]] = ..., numPointClouds: _Optional[int] = ..., serverQueryProcessing: _Optional[float] = ..., textQuery: _Optional[str] = ...) -> None: ...

class Objects(_message.Message):
    __slots__ = ("pointCloud", "BBox", "clip_embeddings", "num_points", "centroid", "index")
    POINTCLOUD_FIELD_NUMBER: _ClassVar[int]
    BBOX_FIELD_NUMBER: _ClassVar[int]
    CLIP_EMBEDDINGS_FIELD_NUMBER: _ClassVar[int]
    NUM_POINTS_FIELD_NUMBER: _ClassVar[int]
    CENTROID_FIELD_NUMBER: _ClassVar[int]
    INDEX_FIELD_NUMBER: _ClassVar[int]
    pointCloud: _containers.RepeatedScalarFieldContainer[float]
    BBox: _containers.RepeatedScalarFieldContainer[float]
    clip_embeddings: _containers.RepeatedScalarFieldContainer[float]
    num_points: int
    centroid: _containers.RepeatedScalarFieldContainer[float]
    index: int
    def __init__(self, pointCloud: _Optional[_Iterable[float]] = ..., BBox: _Optional[_Iterable[float]] = ..., clip_embeddings: _Optional[_Iterable[float]] = ..., num_points: _Optional[int] = ..., centroid: _Optional[_Iterable[float]] = ..., index: _Optional[int] = ...) -> None: ...

class objectUpdate(_message.Message):
    __slots__ = ("objects", "num_objects", "remove_indices", "serverProcessLatency", "frameNumber", "clientTimeStamp")
    OBJECTS_FIELD_NUMBER: _ClassVar[int]
    NUM_OBJECTS_FIELD_NUMBER: _ClassVar[int]
    REMOVE_INDICES_FIELD_NUMBER: _ClassVar[int]
    SERVERPROCESSLATENCY_FIELD_NUMBER: _ClassVar[int]
    FRAMENUMBER_FIELD_NUMBER: _ClassVar[int]
    CLIENTTIMESTAMP_FIELD_NUMBER: _ClassVar[int]
    objects: _containers.RepeatedCompositeFieldContainer[Objects]
    num_objects: int
    remove_indices: _containers.RepeatedScalarFieldContainer[int]
    serverProcessLatency: float
    frameNumber: _containers.RepeatedScalarFieldContainer[int]
    clientTimeStamp: int
    def __init__(self, objects: _Optional[_Iterable[_Union[Objects, _Mapping]]] = ..., num_objects: _Optional[int] = ..., remove_indices: _Optional[_Iterable[int]] = ..., serverProcessLatency: _Optional[float] = ..., frameNumber: _Optional[_Iterable[int]] = ..., clientTimeStamp: _Optional[int] = ...) -> None: ...

class Status(_message.Message):
    __slots__ = ("message",)
    MESSAGE_FIELD_NUMBER: _ClassVar[int]
    message: bool
    def __init__(self, message: bool = ...) -> None: ...

class AudioFile(_message.Message):
    __slots__ = ("chunk_data", "textQuery")
    CHUNK_DATA_FIELD_NUMBER: _ClassVar[int]
    TEXTQUERY_FIELD_NUMBER: _ClassVar[int]
    chunk_data: bytes
    textQuery: str
    def __init__(self, chunk_data: _Optional[bytes] = ..., textQuery: _Optional[str] = ...) -> None: ...
