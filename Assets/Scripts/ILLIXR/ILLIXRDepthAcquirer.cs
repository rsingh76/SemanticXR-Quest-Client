using System;
using System.Runtime.InteropServices;
using Meta.XR;
using SemanticXR.Streaming;
using UnityEngine;
using UnityEngine.Rendering;

namespace SemanticXR
{
    /// <summary>
    /// Initializes the ILLIXR xr_sensor_capture plugin's Vulkan readback resources
    /// via GL.IssuePluginEvent, then calls illixr_acquire_depth() each LateUpdate()
    /// while the ILLIXR transport is connected.
    ///
    /// GL.IssuePluginEvent fires the native callback on Unity's render thread where
    /// the Vulkan device is current — the only safe context for Vulkan initialization.
    /// This is necessary because xr_sensor_capture is loaded by ILLIXR's runtime via
    /// dlopen rather than by Unity's plugin system, so UnityPluginLoad is never called
    /// automatically on it.
    ///
    /// Event IDs (must match constants in plugin.cpp):
    ///   0 = EVENT_INIT   — acquire IUnityGraphicsVulkan and init Vulkan resources
    ///   1 = EVENT_UNINIT — destroy Vulkan resources
    /// </summary>
    public class ILLIXRDepthAcquirer : MonoBehaviour
    {
        const int EVENT_INIT    = 0;
        const int EVENT_UNINIT  = 1;
        const int EVENT_ACQUIRE = 2;

        [DllImport("unity_android_interface.dbg")]
        static extern IntPtr illixr_get_render_event_callback();
        
        // Cache PassthroughCameraAccess reference
        PassthroughCameraAccess _cam;

        public StreamingOrchestrator Orchestrator { get; set; }
        
        public bool Active { get; set; } = false;

        bool _vulkanInitIssued = false;

        void Start()
        {
            _cam = FindAnyObjectByType<PassthroughCameraAccess>();
        }
        void OnEnable()
        {
            if (!_vulkanInitIssued)
            {
                // Issue EVENT_INIT on the render thread. This fires
                // on_render_event(0) in plugin.cpp which acquires
                // IUnityGraphicsVulkan and calls init_vulkan().
                GL.IssuePluginEvent(illixr_get_render_event_callback(), EVENT_INIT);
                _vulkanInitIssued = true;
                Debug.Log("[ILLIXRDepthAcquirer] Vulkan init event issued");
            }
        }

        // OnDisable intentionally omitted. GL.IssuePluginEvent blocks Unity's
        // main thread until the render thread processes the event — if the
        // render thread is waiting on vkWaitForFences (depth readback) this
        // causes a visible frame freeze. Vulkan resources are destroyed only
        // at application quit via OnDestroy, not on transient disable/enable
        // cycles (e.g. UI state changes during voice recording).
        void OnDestroy()
        {
            if (_vulkanInitIssued)
            {
                GL.IssuePluginEvent(illixr_get_render_event_callback(), EVENT_UNINIT);
                _vulkanInitIssued = false;
                Debug.Log("[ILLIXRDepthAcquirer] Vulkan uninit event issued on destroy");
            }
        }

        // LateUpdate is used instead of Update because Unity's XR frame
        // (xrBeginFrame/xrEndFrame) is guaranteed to be open during LateUpdate.
        // GL.IssuePluginEvent routes the acquire to Unity's render thread
        // where the Vulkan graphics queue is exclusively owned — submitting
        // to vk_queue_ from the main thread causes vkQueueSubmit failures.
        void LateUpdate()
        {
            if (!Active)
                return;

            long   displayTimeNs     = ILLIXRXrHandleProvider.GetPredictedDisplayTimeNs();
            double ovrTimeSec        = OVRPlugin.GetTimeInSeconds();
            double captureOvrTimeSec = ILLIXRBridge.illixr_get_last_capture_ovr_time_sec();
            
            Debug.Log($"[DepthAcquirer] displayTimeNs={displayTimeNs} " +
                      $"captureTimeNs={captureOvrTimeSec} " +
                      $"Orchestrator={Orchestrator != null} " +
                      $"cam={(_cam != null ? _cam.IsPlaying.ToString() : "null")}");

            Matrix4x4 headPose = Camera.main != null
                ? Camera.main.transform.localToWorldMatrix
                : Matrix4x4.identity;

            Matrix4x4 rgbPose = Matrix4x4.identity;
            if (Orchestrator != null && captureOvrTimeSec > 0) {
                bool gotPose = Orchestrator.TryGetRgbCameraPoseAtTime(
                    captureOvrTimeSec, out rgbPose);
                Debug.Log($"[DepthAcquirer] TryGetRgbCameraPoseAtTime: " +
                          $"gotPose={gotPose} " +
                          $"pos=({rgbPose.m03:F3},{rgbPose.m13:F3},{rgbPose.m23:F3})");
            } else {
                Debug.LogWarning($"[DepthAcquirer] skipping pose lookup: " +
                                 $"Orchestrator={Orchestrator != null} " +
                                 $"captureTimeNs={captureOvrTimeSec}");
            }

            float[] headArr = MatrixToArray(headPose);
            float[] rgbArr  = MatrixToArray(rgbPose);

            ILLIXRBridge.illixr_acquire_depth(displayTimeNs, ovrTimeSec, rgbArr, headArr);
            GL.IssuePluginEvent(illixr_get_render_event_callback(), EVENT_ACQUIRE);
            ILLIXRBridge.illixr_release_depth();
        }
        static float[] MatrixToArray(Matrix4x4 m) => new float[] {
            m.m00, m.m10, m.m20, m.m30,
            m.m01, m.m11, m.m21, m.m31,
            m.m02, m.m12, m.m22, m.m32,
            m.m03, m.m13, m.m23, m.m33,
        };
    }
}