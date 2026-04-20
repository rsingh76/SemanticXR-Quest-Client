# Meta Quest Depth Conversion: How We Get Metric Depth

## The Problem

The Meta Quest 3S depth sensor provides depth through OpenXR's `XR_META_environment_depth` extension. By the time we read it in Unity, it has been through several transformations. We needed to figure out what the raw byte values mean and how to convert them to metric depth in meters.

## What We Tried, In Order

### Attempt 1: Assume it's already metric depth (WRONG)

Our first assumption was that `_PreprocessedEnvironmentDepthTexture` stores metric depth directly. The R channel values ranged from 0.01 to 1.0 — far too small for room-scale distances (should be 0.2m to 10m+). This couldn't be linear metric depth.

### Attempt 2: Read `_EnvironmentDepthZBufferParams` and use Unity's standard formula (WRONG)

We read the shader global `_EnvironmentDepthZBufferParams` and got `(-0.2, -1, 0, 0)`.

We initially tried to use **Unity's standard `_ZBufferParams` convention**, which is:
- `x = 1 - far/near`, `y = far/near`, `z = x/far`, `w = y/far`
- Conversion: `linear = 1.0 / (z * depth + w)`

Reference: [Unity _ZBufferParams discussion](https://discussions.unity.com/t/_zbufferparams-values/405695), [Unity Depth Textures docs](https://docs.unity3d.com/2020.1/Documentation/Manual/SL-DepthTextures.html)

But `z=0` and `w=0` made this formula divide by zero. That's because **Meta's `_EnvironmentDepthZBufferParams` uses a completely different convention** than Unity's built-in `_ZBufferParams`.

### Attempt 3: Fall back to Camera.main clip planes (COINCIDENTALLY CORRECT)

When ZBufferParams failed, we fell back to `Camera.main.nearClipPlane = 0.1` and tried `metric = 0.1 / zbuf_value`. This produced plausible results (median ~1m for a bedroom), but we couldn't explain WHY it worked.

### Attempt 4: Read Meta's actual source code (CORRECT)

We found the answer by reading the actual Meta XR Core SDK source files installed in the Unity project at:
```
Library/PackageCache/com.meta.xr.sdk.core@f8b4cfb2789f/
```

## The Full Pipeline (What Actually Happens)

### Step 1: OpenXR provides raw depth + nearZ/farZ

The `XR_META_environment_depth` OpenXR extension provides depth textures along with `nearZ` and `farZ` values. These are "the near and far planes defined in an OpenGL projection matrix, and are needed to convert the depth map's pixel values into metric distances."

When `farZ = infinity`, the conversion uses an infinite projection matrix.

Reference: [OpenXR Depth API Overview (Native)](https://developers.meta.com/horizon/documentation/native/android/mobile-depth/)

### Step 2: EnvironmentDepthManager computes ZBufferParams

**File**: `Scripts/EnvironmentDepth/EnvironmentDepthUtils.cs` (lines 29-47 in Meta XR Core SDK v85)

```csharp
internal static Vector4 ComputeNdcToLinearDepthParameters(float near, float far)
{
    float invDepthFactor;
    float depthOffset;

    if (far < near || float.IsInfinity(far))
    {
        // Infinite far plane case:
        invDepthFactor = -2.0f * near;
        depthOffset = -1.0f;
    }
    else
    {
        // Finite far plane case:
        invDepthFactor = -2.0f * far * near / (far - near);
        depthOffset = -(far + near) / (far - near);
    }

    return new Vector4(invDepthFactor, depthOffset, 0, 0);
}
```

This is called in `EnvironmentDepthManager.cs` (line 323) using the left eye's depth frame descriptor:
```csharp
var leftEyeData = frameDescriptors[0];
var depthZBufferParams = EnvironmentDepthUtils.ComputeNdcToLinearDepthParameters(
    leftEyeData.nearZ, leftEyeData.farZ);
Shader.SetGlobalVector(ZBufferParamsID, depthZBufferParams);
```

**Our values**: `(-0.2, -1, 0, 0)` means infinite far plane with `near = 0.1m`:
- `x = -2 * 0.1 = -0.2` (invDepthFactor)
- `y = -1` (depthOffset, infinite far case)
- `z = 0`, `w = 0` (always unused)

### Step 3: Raw depth texture → `_EnvironmentDepthTexture`

The raw depth from OpenXR is stored in `_EnvironmentDepthTexture` (a native OpenXR texture with `graphicsFormat = None`, which is why `AsyncGPUReadback` fails on it directly).

### Step 4: Preprocessing shader → `_PreprocessedEnvironmentDepthTexture`

**File**: `Shaders/EnvironmentDepth/Resources/DepthPreprocessing.shader`

This shader runs when `OcclusionShadersMode == SoftOcclusion` and writes to `_PreprocessedEnvironmentDepthTexture` (format: `R16G16B16A16_SFloat`).

The fragment shader's output (line 115):
```hlsl
return float4(1.0f - minAvg, 1.0f - maxAvg, avg - minAvg, maxAvg - minAvg);
```

**The R channel stores `1.0 - depth_ndc_01`** (inverted depth).

### Step 5: The occlusion shaders convert to linear depth

**File**: `Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusion.cginc` (lines 31-44)

```hlsl
float SampleEnvironmentDepthLinear(float2 uv)
{
    const float inputDepthEye = SampleEnvironmentDepth(uv);  // Returns [0, 1]
    const float inputDepthNdc = inputDepthEye * 2.0 - 1.0;   // Convert to [-1, 1]

    if (inputDepthNdc == 1.0f) {
        return 10000;  // Far plane handling
    }

    // Linear depth = invDepthFactor / (ndc + depthOffset)
    const float linearDepth = (1.0f / (inputDepthNdc + _EnvironmentDepthZBufferParams.y))
                              * _EnvironmentDepthZBufferParams.x;

    return linearDepth;
}
```

Reference: The occlusion shader include files are documented at [Occlusions Advanced Usage](https://developers.meta.com/horizon/documentation/unity/unity-depthapi-occlusions-advanced-usage/) and [Get started with Occlusions](https://developers.meta.com/horizon/documentation/unity/unity-depthapi-occlusions-get-started/).

## Our Conversion (What We Do)

We read the `_PreprocessedEnvironmentDepthTexture` R channel, which stores `1.0 - depth_ndc_01`. To get metric depth:

```
Step 1: Undo preprocessing inversion
    raw_ndc_01 = 1.0 - R_channel

Step 2: Convert [0,1] to [-1,1] NDC
    ndc = raw_ndc_01 * 2.0 - 1.0
        = (1.0 - R) * 2.0 - 1.0
        = 1.0 - 2*R

Step 3: Apply Meta's formula
    linear = invDepthFactor / (ndc + depthOffset)
           = (-2 * near) / (1 - 2*R + (-1))
           = (-2 * near) / (-2 * R)
           = near / R

With near = 0.1m:
    metric_depth = 0.1 / R_channel_value
```

**Simplified: `metric_depth_meters = near / R`** where `near = -invDepthFactor / 2`.

## Validation

| R channel value | Metric depth | Physical meaning |
|:---------------:|:------------:|:----------------:|
| 1.000           | 0.10 m       | At near plane    |
| 0.500           | 0.20 m       | Very close       |
| 0.200           | 0.50 m       | Arm's length     |
| 0.100           | 1.00 m       | Typical desk     |
| 0.050           | 2.00 m       | Across room      |
| 0.020           | 5.00 m       | Far wall         |
| 0.010           | 10.0 m       | Large room       |

From a real capture (bedroom scene, frame 30):
- 25th percentile: 0.63m (nearby furniture)
- Median: 0.94m (floor/walls)
- 75th percentile: 1.62m (far side of room)
- 99th percentile: 5.86m (distant walls)

These values match expected room dimensions.

## Source Files Referenced (Local to Unity Project)

All in `Library/PackageCache/com.meta.xr.sdk.core@f8b4cfb2789f/`:

| File | What it tells us |
|------|-----------------|
| `Scripts/EnvironmentDepth/EnvironmentDepthManager.cs` | Sets `_EnvironmentDepthZBufferParams` shader global from OpenXR frame data |
| `Scripts/EnvironmentDepth/EnvironmentDepthUtils.cs` | `ComputeNdcToLinearDepthParameters()` — the ZBufferParams formula |
| `Scripts/EnvironmentDepth/DepthProvider.cs` | Gets `nearZ`/`farZ` from Oculus native SDK |
| `Scripts/EnvironmentDepth/DepthProviderOpenXR.cs` | Gets `nearZ`/`farZ` from OpenXR `XROcclusionSubsystem` |
| `Shaders/EnvironmentDepth/Resources/DepthPreprocessing.shader` | Writes `1.0 - depth` to R channel |
| `Shaders/EnvironmentDepth/BiRP/EnvironmentOcclusion.cginc` | `SampleEnvironmentDepthLinear()` — the conversion formula |
| `Shaders/EnvironmentDepth/URP/EnvironmentOcclusionURP.hlsl` | Same conversion for URP |

## Online Documentation

- [Depth API Overview](https://developers.meta.com/horizon/documentation/unity/unity-depthapi-overview/) — High-level overview of Meta's Depth API
- [OpenXR Depth API (Native)](https://developers.meta.com/horizon/documentation/native/android/mobile-depth/) — Documents `nearZ`/`farZ` meaning, infinite far plane, projection matrix construction
- [Depth API in Unity's XR.Oculus](https://developers.meta.com/horizon/documentation/unity/unity-depthapi-xr-oculus/) — Unity-specific access to depth textures and `EnvironmentDepthFrameDesc`
- [Occlusions Advanced Usage](https://developers.meta.com/horizon/documentation/unity/unity-depthapi-occlusions-advanced-usage/) — Custom shader integration with depth occlusion
- [Get started with Occlusions](https://developers.meta.com/horizon/documentation/unity/unity-depthapi-occlusions-get-started/) — Basic setup and shader include paths
- [Unity-DepthAPI GitHub](https://github.com/oculus-samples/Unity-DepthAPI) — Sample projects with shader code
- [EnvironmentDepthManager Class Reference (v74)](https://developers.meta.com/horizon/reference/unity/v74/class_meta_x_r_environment_depth_environment_depth_manager) — API reference
- [XR_META_environment_depth OpenXR Extension (Khronos Forum)](https://community.khronos.org/t/quest-3-depth-api-with-xr-meta-environment-depth/110808) — OpenXR extension discussion
- [Unity _ZBufferParams](https://discussions.unity.com/t/_zbufferparams-values/405695) — Unity's standard ZBufferParams (different from Meta's!)
- [Unity LinearEyeDepth](https://forum.unity.com/threads/solved-what-is-lineareyedepth-doing-exactly.539791/) — Unity's standard depth conversion (not applicable to Meta's depth)
- [Cyanilux Depth Tutorial](https://www.cyanilux.com/tutorials/depth/) — Comprehensive Unity depth buffer reference

## Key Insight

Meta's `_EnvironmentDepthZBufferParams` is **NOT** the same as Unity's built-in `_ZBufferParams`. They have different conventions:

| Parameter | Unity `_ZBufferParams` | Meta `_EnvironmentDepthZBufferParams` |
|-----------|----------------------|--------------------------------------|
| x         | `1 - far/near`       | `-2 * far * near / (far - near)` or `-2 * near` |
| y         | `far/near`           | `-(far + near) / (far - near)` or `-1` |
| z         | `x/far`              | `0` (unused) |
| w         | `y/far`              | `0` (unused) |
| Formula   | `1.0 / (z*d + w)`    | `x / (d*2-1 + y)` |

Confusing these two conventions was our initial mistake.
