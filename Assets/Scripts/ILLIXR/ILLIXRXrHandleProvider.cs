using UnityEngine;
using UnityEngine.XR.OpenXR.Features;
#if UNITY_EDITOR
using UnityEditor.XR.OpenXR.Features;
#endif

namespace SemanticXR
{
    /// <summary>
    /// Minimal OpenXRFeature that captures the XrInstance and XrSession handles
    /// Unity creates at startup and exposes them statically for use by ILLIXR
    /// native plugins. These handles are passed to the C++ runtime via
    /// illixr_unity_set_env() before illixr_unity_init() so the xr_sensor_capture
    /// plugin can reuse Unity's existing OpenXR session rather than creating a
    /// second one (which the Quest 3 loader does not support).
    ///
    /// To activate: Project Settings → XR Plug-in Management → OpenXR →
    ///              Android tab → enable "ILLIXR XR Handle Provider".
    /// </summary>
#if UNITY_EDITOR
    [OpenXRFeature(
        UiName            = "ILLIXR XR Handle Provider",
        BuildTargetGroups = new[] { UnityEditor.BuildTargetGroup.Android },
        Company           = "ILLIXR",
        Desc              = "Exposes XrInstance and XrSession handles for ILLIXR native plugins.",
        Version           = "1.0.0",
        FeatureId         = FeatureId)]
#endif
    public class ILLIXRXrHandleProvider : OpenXRFeature
    {
        public const string FeatureId = "com.illixr.xrhandleprovider";

        /// <summary>The XrInstance handle Unity created at startup.</summary>
        public static ulong XrInstance { get; private set; }

        /// <summary>The XrSession handle Unity created at startup.</summary>
        public static ulong XrSession  { get; private set; }

        /// <summary>True once both handles have been populated.</summary>
        public static bool HasHandles => XrInstance != 0 && XrSession != 0;

        protected override bool OnInstanceCreate(ulong xrInstance)
        {
            XrInstance = xrInstance;
            Debug.Log($"[ILLIXRXrHandleProvider] XrInstance = 0x{xrInstance:X}");
            return true;
        }

        protected override void OnSessionCreate(ulong xrSession)
        {
            XrSession = xrSession;
            Debug.Log($"[ILLIXRXrHandleProvider] XrSession = 0x{xrSession:X}");
        }

        protected override void OnInstanceDestroy(ulong xrInstance)
        {
            XrInstance = 0;
        }

        protected override void OnSessionDestroy(ulong xrSession)
        {
            XrSession = 0;
        }
    }
}
