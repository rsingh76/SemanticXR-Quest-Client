# SemanticXR

A Unity project for streaming Quest 3/3S passthrough camera data (RGB + depth + pose) to a remote server via gRPC. Built on top of Meta's [Passthrough Camera API Samples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples).

## What This Does

- Captures passthrough camera frames on Meta Quest 3 / 3S
- Encodes frames using hardware H.265
- Streams encoded video, depth maps, camera pose, and intrinsics to a remote gRPC server
- Includes the original Meta sample scenes (CameraViewer, CameraToWorld, BrightnessEstimation, MultiObjectDetection, ShaderSample)

## Prerequisites

### Hardware

- **Meta Quest 3** or **Quest 3S** with **Horizon OS v74** or higher
- A USB-C cable for connecting the headset to your PC (or Wi-Fi ADB)

### Software

Install the following before cloning:

| Tool | Version | Download |
|------|---------|----------|
| **Git** (with Git LFS) | Latest | [git-scm.com](https://git-scm.com/downloads) |
| **Unity Hub** | Latest | [unity.com/download](https://unity.com/download) |
| **Unity Editor** | **6000.4.0f1** or newer | Install via Unity Hub (see below) |
| **Android Build Support** | (Unity module) | Install via Unity Hub (see below) |

#### Platform-Specific Setup

<details>
<summary><b>Windows</b></summary>

1. Install [Git for Windows](https://git-scm.com/download/win) (includes Git LFS).
2. Install [Unity Hub](https://unity.com/download).
3. (Optional) Install [Android Studio](https://developer.android.com/studio) if you want standalone `adb`. Unity bundles its own Android SDK/NDK, so this is not required.

</details>

<details>
<summary><b>macOS</b></summary>

1. Install Xcode Command Line Tools (for Git):
   ```bash
   xcode-select --install
   ```
2. Install Git LFS:
   ```bash
   brew install git-lfs
   git lfs install
   ```
3. Install [Unity Hub](https://unity.com/download).

</details>

<details>
<summary><b>Ubuntu / Linux</b></summary>

1. Install Git and Git LFS:
   ```bash
   sudo apt update
   sudo apt install git git-lfs
   git lfs install
   ```
2. Install Unity Hub. Download the `.deb` from [unity.com/download](https://unity.com/download) or use the official repo:
   ```bash
   # Add Unity's signing key and repo
   wget -qO - https://hub.unity3d.com/linux/keys/public | sudo gpg --dearmor -o /usr/share/keyrings/unity-hub.gpg
   echo "deb [signed-by=/usr/share/keyrings/unity-hub.gpg] https://hub.unity3d.com/linux/repos/deb stable main" | sudo tee /etc/apt/sources.list.d/unityhub.list
   sudo apt update
   sudo apt install unityhub
   ```

> **Note:** Building for Quest requires Android Build Support, which works on Linux. However, some Meta tools (e.g., Meta Quest Developer Hub) are Windows/macOS only.

</details>

## Installing Unity and Android Build Support

1. Open **Unity Hub**
2. Go to **Installs** > **Install Editor**
3. Find **Unity 6000.4.0f1** (or newer 6000.x LTS) and click **Install**
4. In the module selection, check:
   - **Android Build Support**
   - **Android SDK & NDK Tools** (under Android Build Support)
   - **OpenJDK** (under Android Build Support)
5. Complete the installation

## Clone and Open the Project

```bash
git lfs install
git clone https://github.com/rsingh76/SemanticXR.git
```

1. Open **Unity Hub**
2. Click **Open** > **Add project from disk**
3. Select the cloned `SemanticXR` folder
4. Unity will import the project (this takes a few minutes on first open)
5. If prompted about "Safe Mode" due to compile errors, click **Ignore** — the packages will resolve after import

## Quest Developer Setup

Before you can deploy to your headset:

1. **Enable Developer Mode** on your Quest:
   - Install the **Meta Horizon** app on your phone
   - Go to **Devices** > select your headset > **Settings** > **Developer** > enable **Developer Mode**
   - If you don't see the Developer option, register as a developer at [developer.meta.com](https://developer.meta.com/)

2. **Connect your Quest** to your PC via USB-C
   - Put on the headset and **Allow USB debugging** when prompted

3. **Verify the connection**:
   ```bash
   adb devices
   ```
   You should see your device listed. If `adb` is not in your PATH, it's located at:
   - **Windows:** `C:\Program Files\Unity\Hub\Editor\<version>\Editor\Data\PlaybackEngines\AndroidPlayer\SDK\platform-tools\adb.exe`
   - **macOS/Linux:** `~/Unity/Hub/Editor/<version>/Editor/Data/PlaybackEngines/AndroidPlayer/SDK/platform-tools/adb`

## Build and Deploy to Quest

1. In Unity, go to **File** > **Build Settings**
2. Select **Android** as the platform (click **Switch Platform** if needed)
3. Connect your Quest via USB
4. Click **Build and Run**
5. The app will install and launch on your headset
6. On first launch, the app will automatically request camera and scene permissions — **Accept** them

## Project Structure

```
Assets/
  Scripts/
    Proto/              # gRPC protobuf definitions (xr_service.proto)
    Streaming/          # gRPC client, TCP client, streaming orchestrator
    Encoding/           # Hardware H.265 encoder
    UI/                 # Streaming UI and XR interaction setup
  Editor/
    ManifestPostProcessor.cs   # Build-time Android manifest patching
  Plugins/
    Android/            # Custom AndroidManifest.xml
    Grpc/               # gRPC and Protobuf DLLs for Unity
  PassthroughCameraApiSamples/ # Original Meta sample scenes
```

### Key Custom Components

| File | Purpose |
|------|---------|
| `xr_service.proto` | Protobuf schema defining `UpstreamSyncMessage_quest` (RGB + depth + pose + intrinsics) |
| `GrpcStreamingClient.cs` | gRPC client that streams frames to a remote server |
| `StreamingOrchestrator.cs` | Coordinates camera capture, encoding, and streaming |
| `HardwareH265Encoder.cs` | Hardware-accelerated H.265 video encoding on Quest |
| `TcpProtoClient.cs` | Alternative TCP-based protobuf streaming client |
| `ManifestPostProcessor.cs` | Patches Android manifest and app name at build time |

## Depth Capture and 3D Reconstruction

### Depth Pipeline

The app captures depth from Meta's Environment Depth API (`_PreprocessedEnvironmentDepthTexture`) alongside RGB from the Passthrough Camera API. Key challenges solved:

1. **Temporal synchronization**: `AsyncGPUReadback` is asynchronous — depth bytes arrive 1-2 frames after the request. All data (RGB, depth, pose, intrinsics) is captured at readback *request* time and bundled into a `DepthSnapshot` struct, so every field in a frame corresponds to the same moment.

2. **Depth intrinsics**: The depth sensor has a different FOV (~94-98°) than the RGB camera (~73°). Intrinsics are extracted from Meta's internal `EnvironmentDepthFrameDesc` FOV tangent fields via reflection:
   ```
   fx = width / (tanRight + tanLeft)
   fy = height / (tanTop + tanDown)
   cx = tanLeft * fx
   cy = tanTop * fy
   ```

3. **Coordinate conventions**: Unity (left-handed) → right-handed conversion in `TcpProtoClient` via `S @ M @ S` where `S = diag(1,1,-1,1)`. Server-side reconstruction converts from OpenGL camera convention (Y-up, Z-backward) to OpenCV (Y-down, Z-forward).

4. **Depth camera pose**: Separate from head pose — extracted from `EnvironmentDepthFrameDesc.createPoseLocation/Rotation` and sent alongside the head pose. This pose is in **OpenXR tracking space**, not Unity world space — for scenes with no XR origin offset they coincide; otherwise the server must apply the tracking-space transform.

5. **Explicit poses**: Three distinct 4×4 matrices are sent on the wire — `head_pose` (Camera.main eye center, Unity world), `rgb_camera_pose` (`PassthroughCameraAccess.GetCameraPose()`, physical RGB sensor with lens offset, Unity world), and `depth_pose` (depth sensor, OpenXR tracking space). Use `rgb_camera_pose` for projecting RGB pixels and `depth_pose` for unprojecting depth pixels — they are NOT interchangeable.

6. **Sensor timestamps & clock domains**: RGB and depth come from independent Meta subsystems with **different clocks**:
   - `rgb_timestamp_ns` — Android Camera2 `SENSOR_TIMESTAMP`, `CLOCK_BOOTTIME` nanoseconds (read via reflection from `PassthroughCameraAccess._timestampNsMonotonic`).
   - `depth_timestamp_ns` — `EnvironmentDepthFrameDesc.createTime`, OpenXR `XrTime` / `CLOCK_MONOTONIC` nanoseconds.

   These cannot be directly compared without offset-correcting the suspend gap between BOOTTIME and MONOTONIC. The orchestrator does **best-effort co-capture** in the same `Update()` tick (≲40ms apart at 25 FPS) and does NOT currently reject mismatched pairs by timestamp diff.

7. **OVRCameraRig requirement (Meta SDK quirk)**: `DepthFrameDesc.createTime` returns 0 unless an `OVRCameraRig` exists in the scene. `StreamingOrchestrator` auto-spawns a hidden, disabled rig at startup if one is missing, so depth timestamps are always populated without affecting tracking or rendering (which still use OpenXR / `Camera.main`).

8. **Best-effort sync, not atomic**: The internal `CoCapturedFrame` struct bundles RGB + depth + poses + timestamps captured in the same Update() tick. This is approximate — not an atomic hardware-synchronized capture. We do not yet drop frames whose RGB/depth timestamps drift apart; that's a TODO once the BOOTTIME↔MONOTONIC offset correction is implemented.

### Protobuf Fields

The `UpstreamSyncMessage_quest` proto includes:
- `depth_intrinsics` (field 13): Depth camera fx, fy, cx, cy derived from FOV tangents
- `depth_pose` (field 14): 4x4 depth camera-to-world matrix (row-major, right-handed)

### Server-Side Reconstruction

A companion debug server (`debug_server/`) can reconstruct 3D point clouds or TSDF meshes from captured sessions. See `reconstruct.py` (point cloud) and `reconstruct_tsdf.py` (volumetric TSDF fusion).

### Known Limitations

- `_PreprocessedEnvironmentDepthTexture` contains soft occlusion statistics (not raw depth), though the R channel conversion `nearZ / R` yields equivalent linear depth.
- Depth resolution is low (320x320).
- RGB and depth come from separate Meta APIs with no unified synchronized capture — synchronization is best-effort by co-capturing in the same frame.

## gRPC Server

This project streams data **from** the Quest to a gRPC server. The server is not included in this repo. To receive the stream, implement a gRPC server that handles the `XrService.UploadSyncMessage_quest` RPC defined in [`xr_service.proto`](Assets/Scripts/Proto/xr_service.proto).

## Troubleshooting

- **"No permission granted" on headset:** Go to Quest **Settings** > **Apps** > **SemanticXR** > **Permissions** and grant Camera and Scene permissions manually.
- **Build fails with manifest merger error:** Delete `Library/Bee/Android/` in the project folder and rebuild.
- **Unity can't find the headset:** Make sure USB debugging is enabled and you accepted the prompt on the headset. Run `adb devices` to verify.
- **Project won't open / compile errors on import:** Make sure you installed Unity **6000.4.0f1** or newer with Android Build Support. Let the initial import complete fully.

## Upstream

This project is forked from [oculus-samples/Unity-PassthroughCameraApiSamples](https://github.com/oculus-samples/Unity-PassthroughCameraApiSamples). The original Meta sample scenes and documentation are preserved. See the upstream repo for additional details on the Passthrough Camera API.

## License

The [`Oculus License`](./LICENSE.txt) applies to the SDK and supporting material. The [`MIT License`](./Assets/PassthroughCameraApiSamples/LICENSE.txt) applies to certain clearly marked documents. Files in [`MultiObjectDetection/SentisInference/Model`](./Assets/PassthroughCameraApiSamples/MultiObjectDetection/SentisInference/Model) are licensed under [MIT](https://github.com/MultimediaTechLab/YOLO/blob/main/LICENSE).
