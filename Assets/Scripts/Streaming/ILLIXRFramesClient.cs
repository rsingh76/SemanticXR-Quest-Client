using UnityEngine;

namespace SemanticXR.Streaming
{
    /// <summary>
    /// IFramesClient implementation for the ILLIXR transport.
    ///
    /// All RGB capture, depth capture, encoding, and pose acquisition are handled
    /// by the xr_sensor_capture C++ plugin. Unity does not perform any capture
    /// work on this transport path — Enqueue() is a no-op.
    ///
    /// The XrInstance and XrSession handles are passed to the ILLIXR runtime
    /// by ILLIXRXrHandleProvider at OpenXR loader init time (well before this
    /// Start() is called), so illixr_unity_init() finds them already set.
    /// </summary>
    public class ILLIXRFramesClient : IFramesClient
    {
        public int    QueuedFrames   => 0;
        public bool   IsConnected    { get; private set; }
        public int    SentFrames     { get; private set; }
        public long   TotalBytesSent { get; private set; }
        public string LastError      { get; private set; }

        public void Start()
        {
            // illixr_unity_init() was already called by ILLIXRManager.Initialize()
            // in ConnectIllixrCoroutine, which also set ILLIXR_XR_INSTANCE and
            // ILLIXR_XR_SESSION before calling it. Nothing to do here except mark
            // the client as connected.
            IsConnected = true;
            Debug.LogWarning("[ILLIXR] ILLIXRFramesClient connected");
        }

        /// <summary>
        /// No-op — capture is handled entirely by the xr_sensor_capture C++ plugin.
        /// </summary>
        public void Enqueue(FrameData f) { }

        public void Stop()
        {
            ILLIXRBridge.illixr_unity_shutdown();
            IsConnected = false;
            Debug.LogWarning("[ILLIXR] Runtime stopped");
        }

        public void Dispose() => Stop();
    }
}