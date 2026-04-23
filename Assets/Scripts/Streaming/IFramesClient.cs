using System;

namespace SemanticXR.Streaming
{
    // Common contract for the two frame-transport implementations (raw TCP
    // via TcpProtoClient, or gRPC via GrpcFramesClient). Kept minimal —
    // StreamingOrchestrator doesn't care which transport is behind it.
    public interface IFramesClient : IDisposable
    {
        int    QueuedFrames    { get; }
        bool   IsConnected     { get; }
        int    SentFrames      { get; }
        long   TotalBytesSent  { get; }   // cumulative payload bytes since Start()
        string LastError       { get; }   // non-null once, nulled out on read

        void Start();
        void Enqueue(FrameData frame);
        void Stop();
    }
}
