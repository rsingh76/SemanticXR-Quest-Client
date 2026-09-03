using System;
using System.Runtime.InteropServices;
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
        
        public bool Active { get; set; } = false;

        bool _vulkanInitIssued = false;

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

            // 1. Acquire depth image on main thread (required by OpenXR).
            //    This calls acquire_depth_unity_thread() which calls
            //    xrAcquireEnvironmentDepthImageMETA and stores the pending readback.
            long displayTimeNs = ILLIXRXrHandleProvider.GetPredictedDisplayTimeNs();
            ILLIXRBridge.illixr_acquire_depth(displayTimeNs);

            // 2. Submit the Vulkan copy on the render thread via GL.IssuePluginEvent.
            //    This call blocks until the render thread finishes submit_depth_readback(),
            //    including vkWaitForFences, so the GPU copy is complete when it returns.
            GL.IssuePluginEvent(illixr_get_render_event_callback(), EVENT_ACQUIRE);

            // 3. Release the depth image back to the OpenXR runtime on the main thread,
            //    before xrEndFrame closes the frame.
            ILLIXRBridge.illixr_release_depth();
        }
    }
}