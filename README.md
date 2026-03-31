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
