using System;
using System.Reflection;
using System.Runtime.InteropServices;
using Meta.XR;
using Meta.XR.EnvironmentDepth;
using UnityEngine;
using UnityEngine.Rendering;

namespace SemanticXR.Diagnostics
{
    /// <summary>
    /// On-device sanity test for projection correctness.
    ///
    /// Runs 3 tests automatically when the passthrough camera is playing:
    ///   Test 1: Meta's WorldToViewportPoint vs manual intrinsics projection
    ///   Test 2: Depth pixel → 3D world → RGB pixel roundtrip
    ///   Test 3: LhToRh server simulation (can the server reconstruct the projection?)
    ///
    /// Logs results via Debug.LogWarning for visibility in adb logcat.
    /// Auto-disables after MaxTestFrames.
    /// </summary>
    public class ProjectionSanityTest : MonoBehaviour
    {
        const int MaxTestFrames = 10;
        const string Tag = "[SanityTest]";

        PassthroughCameraAccess _cam;
        EnvironmentDepthManager _depthMgr;
        FieldInfo _frameDescField;
        int _testCount;
        bool _paramsDumped;

        [DllImport("OVRPlugin", CallingConvention = CallingConvention.Cdecl)]
        static extern OVRPlugin.Result ovrp_GetNodePoseStateAtTime(
            double time, OVRPlugin.Node nodeId, out OVRPlugin.PoseStatef nodePoseState);

        void Start()
        {
            _cam = FindAnyObjectByType<PassthroughCameraAccess>();
            _depthMgr = FindAnyObjectByType<EnvironmentDepthManager>();

            if (_depthMgr != null)
            {
                _frameDescField = typeof(EnvironmentDepthManager).GetField(
                    "frameDescriptors",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);
            }

            if (_cam == null)
            {
                UnityEngine.Debug.LogError($"{Tag} No PassthroughCameraAccess found!");
                enabled = false;
            }
        }

        void LateUpdate()
        {
            if (_cam == null || !_cam.IsPlaying) return;
            if (_testCount >= MaxTestFrames) { enabled = false; return; }

            // Wait a few frames for camera warmup
            if (Time.frameCount < 60) return;

            _testCount++;

            var cameraPose = _cam.GetCameraPose();
            var intr = _cam.Intrinsics;
            var currentRes = _cam.CurrentResolution;

            // Dump all raw parameters on first test frame
            if (!_paramsDumped)
            {
                DumpParameters(cameraPose, intr, currentRes);
                _paramsDumped = true;
            }

            // Test 1: WorldToViewportPoint vs manual projection
            RunTest1(cameraPose, intr, currentRes);

            // Test 3: LhToRh server simulation
            RunTest3(cameraPose, intr, currentRes);

            // Test 2: Depth roundtrip (async — results appear 1-2 frames later)
            if (_testCount <= 3)
                RunTest2(cameraPose, intr, currentRes);
        }

        // ================================================================
        // TEST 1: Meta's WorldToViewportPoint vs Manual Projection
        // ================================================================
        void RunTest1(Pose camPose, PassthroughCameraAccess.CameraIntrinsics intr, Vector2Int res)
        {
            var cam = Camera.main;
            if (cam == null) return;

            Vector3[] testPoints = {
                cam.transform.position + cam.transform.forward * 1.0f,
                cam.transform.position + cam.transform.forward * 2.0f + cam.transform.right * 0.5f,
                cam.transform.position + cam.transform.forward * 1.5f + cam.transform.up * 0.3f,
            };

            // Replicate CalcSensorCropRegion (it's private)
            Vector2 sensorRes = (Vector2)intr.SensorResolution;
            Vector2 curRes = (Vector2)res;
            Vector2 sf = curRes / sensorRes;
            sf /= Mathf.Max(sf.x, sf.y);
            Rect crop = new Rect(
                sensorRes.x * (1f - sf.x) * 0.5f,
                sensorRes.y * (1f - sf.y) * 0.5f,
                sensorRes.x * sf.x,
                sensorRes.y * sf.y);

            for (int i = 0; i < testPoints.Length; i++)
            {
                Vector3 wp = testPoints[i];

                // Meta's ground truth
                Vector2 metaVp = _cam.WorldToViewportPoint(wp, camPose);

                // Manual projection using raw sensor intrinsics
                Vector3 posInCam = Quaternion.Inverse(camPose.rotation) * (wp - camPose.position);

                float sensorU = (posInCam.x / posInCam.z) * intr.FocalLength.x + intr.PrincipalPoint.x;
                float sensorV = (posInCam.y / posInCam.z) * intr.FocalLength.y + intr.PrincipalPoint.y;

                Vector2 manualVp = new Vector2(
                    (sensorU - crop.x) / crop.width,
                    (sensorV - crop.y) / crop.height);

                // Also compute image-pixel coords (what we'd send to server)
                float imgU = sensorU; // No crop adjustment needed since sensor==current
                float imgV = sensorV;

                float errPx = Vector2.Distance(metaVp, manualVp) * res.x;
                string zSign = posInCam.z > 0 ? "POSITIVE" : "NEGATIVE";

                UnityEngine.Debug.LogWarning($"{Tag}1 f={_testCount} pt{i} posInCam=({posInCam.x:F4},{posInCam.y:F4},{posInCam.z:F4}) Z_SIGN={zSign}");
                UnityEngine.Debug.LogWarning($"{Tag}1 f={_testCount} pt{i} meta_vp=({metaVp.x:F5},{metaVp.y:F5}) manual_vp=({manualVp.x:F5},{manualVp.y:F5}) err={errPx:F2}px");
            }
        }

        // ================================================================
        // TEST 2: Depth Pixel → 3D World → RGB Pixel
        // ================================================================
        void RunTest2(Pose rgbPose, PassthroughCameraAccess.CameraIntrinsics rgbIntr, Vector2Int rgbRes)
        {
            var depthTex = Shader.GetGlobalTexture("_PreprocessedEnvironmentDepthTexture") as RenderTexture;
            if (depthTex == null || !depthTex.IsCreated()) return;

            // Read depth intrinsics from EnvironmentDepthManager reflection
            if (!TryGetDepthIntrinsics(depthTex.width, depthTex.height,
                    out float dfx, out float dfy, out float dcx, out float dcy,
                    out Matrix4x4 depthPose))
                return;

            var zbuf = Shader.GetGlobalVector("_EnvironmentDepthZBufferParams");
            float nearZ = -zbuf.x / 2f;

            // Capture rgbPose for use in callback
            Pose capturedRgbPose = rgbPose;
            var capturedRgbIntr = rgbIntr;
            Vector2Int capturedRgbRes = rgbRes;
            float capturedDfx = dfx, capturedDfy = dfy, capturedDcx = dcx, capturedDcy = dcy;
            Matrix4x4 capturedDepthPose = depthPose;
            float capturedNearZ = nearZ;

            AsyncGPUReadback.Request(depthTex, 0, request =>
            {
                if (request.hasError) return;

                var data = request.GetData<byte>();
                int dw = request.width, dh = request.height;

                // Pick center pixel
                int px = dw / 2, py = dh / 2;

                // Depth texture is bottom-up on GPU; flip Y for top-down pixel access
                int flippedPy = dh - 1 - py;

                // R16G16B16A16_SFloat: 8 bytes per pixel, R channel = first 2 bytes (half float)
                int offset = (flippedPy * dw + px) * 8;
                if (offset + 2 > data.Length) return;

                ushort rBits = (ushort)(data[offset] | (data[offset + 1] << 8));
                float rValue = Mathf.HalfToFloat(rBits);
                if (rValue < 1e-4f) return;

                float depthM = capturedNearZ / rValue;
                if (depthM <= 0.05f || depthM > 10f) return;

                // Backproject depth pixel to 3D in depth camera space
                // The depth image on-device has top-down convention after flip
                float xCam = (px - capturedDcx) / capturedDfx * depthM;
                float yCam = (py - capturedDcy) / capturedDfy * depthM;
                float zCam = depthM;
                Vector3 ptDepthCam = new Vector3(xCam, yCam, zCam);

                // Transform to world using depth pose
                Vector3 worldPt = capturedDepthPose.MultiplyPoint3x4(ptDepthCam);

                // Reproject into RGB using Meta's API
                Vector2 metaVp = _cam.WorldToViewportPoint(worldPt, capturedRgbPose);
                float metaPxU = metaVp.x * capturedRgbRes.x;
                float metaPxV = metaVp.y * capturedRgbRes.y;

                // Manual reproject
                Vector3 posInRgbCam = Quaternion.Inverse(capturedRgbPose.rotation) *
                                      (worldPt - capturedRgbPose.position);
                float manU = capturedRgbIntr.FocalLength.x * posInRgbCam.x / posInRgbCam.z +
                             capturedRgbIntr.PrincipalPoint.x;
                float manV = capturedRgbIntr.FocalLength.y * posInRgbCam.y / posInRgbCam.z +
                             capturedRgbIntr.PrincipalPoint.y;

                bool metaInBounds = metaVp.x >= 0 && metaVp.x <= 1 && metaVp.y >= 0 && metaVp.y <= 1;
                bool manInBounds = manU >= 0 && manU < capturedRgbRes.x && manV >= 0 && manV < capturedRgbRes.y;

                float crossErr = Mathf.Sqrt((metaPxU-manU)*(metaPxU-manU)+(metaPxV-manV)*(metaPxV-manV));
                UnityEngine.Debug.LogWarning($"{Tag}2 depthPx=({px},{py}) depth={depthM:F2}m worldPt=({worldPt.x:F3},{worldPt.y:F3},{worldPt.z:F3})");
                UnityEngine.Debug.LogWarning($"{Tag}2 posInRgbCam=({posInRgbCam.x:F3},{posInRgbCam.y:F3},{posInRgbCam.z:F3}) Z_SIGN={((posInRgbCam.z>0)?"POS":"NEG")}");
                UnityEngine.Debug.LogWarning($"{Tag}2 meta_rgb_px=({metaPxU:F1},{metaPxV:F1}) manual_rgb_px=({manU:F1},{manV:F1}) err={crossErr:F2}px bounds={metaInBounds}");
            });
        }

        // ================================================================
        // TEST 3: LhToRh Server Simulation
        // ================================================================
        void RunTest3(Pose camPose, PassthroughCameraAccess.CameraIntrinsics intr, Vector2Int res)
        {
            var cam = Camera.main;
            if (cam == null) return;

            Vector3 testPoint = cam.transform.position + cam.transform.forward * 1.5f;

            // Meta ground truth
            Vector2 metaVp = _cam.WorldToViewportPoint(testPoint, camPose);

            // Build the camera-to-world matrix (same as orchestrator)
            Matrix4x4 c2w_lh = Matrix4x4.TRS(camPose.position, camPose.rotation, Vector3.one);

            // Apply LhToRh (same as TcpProtoClient)
            float[] rh = LhToRh(c2w_lh);

            // Reconstruct 4x4 from flat array (row-major)
            Matrix4x4 c2w_rh = new Matrix4x4();
            c2w_rh.m00 = rh[0];  c2w_rh.m01 = rh[1];  c2w_rh.m02 = rh[2];  c2w_rh.m03 = rh[3];
            c2w_rh.m10 = rh[4];  c2w_rh.m11 = rh[5];  c2w_rh.m12 = rh[6];  c2w_rh.m13 = rh[7];
            c2w_rh.m20 = rh[8];  c2w_rh.m21 = rh[9];  c2w_rh.m22 = rh[10]; c2w_rh.m23 = rh[11];
            c2w_rh.m30 = rh[12]; c2w_rh.m31 = rh[13]; c2w_rh.m32 = rh[14]; c2w_rh.m33 = rh[15];

            // Convert world point to RH (negate Z)
            Vector4 wpRh = new Vector4(testPoint.x, testPoint.y, -testPoint.z, 1f);

            // Server does: p_local = inv(c2w_rh) @ world_point_rh
            Matrix4x4 w2c_rh = c2w_rh.inverse;
            Vector4 pLocal = w2c_rh * wpRh;

            // Try all 4 Z-sign + v-flip combos and report which matches Meta
            float fx = intr.FocalLength.x;
            float fy = intr.FocalLength.y;
            float cx = intr.PrincipalPoint.x;
            float cy = intr.PrincipalPoint.y;

            string bestCombo = "NONE";
            float bestErr = float.MaxValue;

            foreach (bool negZ in new[] { false, true })
            {
                float z = negZ ? -pLocal.z : pLocal.z;
                if (Mathf.Abs(z) < 1e-4f) continue;

                float u = fx * pLocal.x / z + cx;
                float v = fy * pLocal.y / z + cy;

                // Normalize to viewport
                float vpU = u / res.x;
                float vpV = v / res.y;

                // Also try flipped V
                float vpV_flip = 1f - vpV;

                float err_raw = Vector2.Distance(new Vector2(vpU, vpV), metaVp) * res.x;
                float err_flip = Vector2.Distance(new Vector2(vpU, vpV_flip), metaVp) * res.x;

                string label_raw = $"z{(negZ ? "NEG" : "POS")}_vRAW";
                string label_flip = $"z{(negZ ? "NEG" : "POS")}_vFLIP";

                if (err_raw < bestErr) { bestErr = err_raw; bestCombo = label_raw; }
                if (err_flip < bestErr) { bestErr = err_flip; bestCombo = label_flip; }

                UnityEngine.Debug.LogWarning(
                    $"{Tag}3 frame={_testCount} {label_raw}: vp=({vpU:F5},{vpV:F5}) err={err_raw:F2}px | " +
                    $"{label_flip}: vp=({vpU:F5},{vpV_flip:F5}) err={err_flip:F2}px");
            }

            UnityEngine.Debug.LogWarning($"{Tag}3 frame={_testCount} pLocal=({pLocal.x:F4},{pLocal.y:F4},{pLocal.z:F4}) meta_vp=({metaVp.x:F5},{metaVp.y:F5}) BEST={bestCombo} err={bestErr:F2}px");
        }

        // ================================================================
        // Helpers
        // ================================================================

        void DumpParameters(Pose camPose, PassthroughCameraAccess.CameraIntrinsics intr, Vector2Int res)
        {
            var lo = intr.LensOffset;
            Vector3 euler = lo.rotation.eulerAngles;

            Matrix4x4 m = Matrix4x4.TRS(camPose.position, camPose.rotation, Vector3.one);

            UnityEngine.Debug.LogWarning($"{Tag}P sensorRes={intr.SensorResolution} currentRes={res}");
            UnityEngine.Debug.LogWarning($"{Tag}P intrinsics fx={intr.FocalLength.x:F4} fy={intr.FocalLength.y:F4} cx={intr.PrincipalPoint.x:F4} cy={intr.PrincipalPoint.y:F4}");
            UnityEngine.Debug.LogWarning($"{Tag}P lensOffset.pos=({lo.position.x:F5},{lo.position.y:F5},{lo.position.z:F5})");
            UnityEngine.Debug.LogWarning($"{Tag}P lensOffset.euler=({euler.x:F2},{euler.y:F2},{euler.z:F2})");
            UnityEngine.Debug.LogWarning($"{Tag}P camPose.pos=({camPose.position.x:F4},{camPose.position.y:F4},{camPose.position.z:F4})");
        }

        bool TryGetDepthIntrinsics(int dw, int dh,
            out float dfx, out float dfy, out float dcx, out float dcy,
            out Matrix4x4 depthPose)
        {
            dfx = dfy = dcx = dcy = 0;
            depthPose = Matrix4x4.identity;

            if (_depthMgr == null || _frameDescField == null) return false;

            try
            {
                var descs = _frameDescField.GetValue(_depthMgr) as Array;
                if (descs == null || descs.Length == 0) return false;

                var desc = descs.GetValue(0);
                var dt = desc.GetType();

                float tanL = (float)dt.GetField("fovLeftAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                float tanR = (float)dt.GetField("fovRightAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                float tanT = (float)dt.GetField("fovTopAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                float tanD = (float)dt.GetField("fovDownAngleTangent",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);

                Vector3 poseLoc = (Vector3)dt.GetField("createPoseLocation",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);
                Quaternion poseRot = (Quaternion)dt.GetField("createPoseRotation",
                    BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public).GetValue(desc);

                if (tanL <= 0 || tanR <= 0) return false;

                dfx = dw / (tanR + tanL);
                dfy = dh / (tanT + tanD);
                dcx = tanL * dfx;
                dcy = tanT * dfy;

                depthPose = Matrix4x4.TRS(poseLoc, poseRot, Vector3.one);
                return true;
            }
            catch { return false; }
        }

        static float[] LhToRh(Matrix4x4 m) => new[]
        {
             m.m00,  m.m01, -m.m02,  m.m03,
             m.m10,  m.m11, -m.m12,  m.m13,
            -m.m20, -m.m21,  m.m22, -m.m23,
             m.m30,  m.m31, -m.m32,  m.m33,
        };
    }
}
