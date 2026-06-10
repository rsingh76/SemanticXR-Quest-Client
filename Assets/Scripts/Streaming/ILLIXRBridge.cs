// Copyright 2020-2026, The Board of Trustees of the University of Illinois.
// SPDX-License-Identifier: BSL-1.0

using System.Runtime.InteropServices;

/// <summary>
/// P/Invoke bindings for the ILLIXR Unity Android interface library.
/// All illixr_unity_set_env() calls must precede illixr_unity_init().
/// </summary>
public static class ILLIXRBridge
{
    private const string LIB = "unity_android_interface.dbg";

    /// <summary>
    /// Set an environment variable for the ILLIXR runtime.
    /// Must be called before illixr_unity_init() for any variable that
    /// ILLIXR plugins read at startup.
    /// </summary>
    /// <returns>0 on success, -1 on failure.</returns>
    [DllImport(LIB)]
    public static extern int illixr_unity_set_env(string name, string value);

    /// <summary>
    /// Initialize the ILLIXR runtime. Call illixr_unity_set_env() for all
    /// required environment variables before calling this.
    /// </summary>
    /// <returns>0 on success, non-zero on failure.</returns>
    [DllImport(LIB)]
    public static extern int illixr_unity_init();

    /// <summary>
    /// Shut down the ILLIXR runtime. Call from OnApplicationQuit().
    /// </summary>
    [DllImport(LIB)]
    public static extern void illixr_unity_shutdown();

    /// <summary>
    /// Send a semantic frame (encoded RGB + depth + metadata) to the switchboard.
    /// Called per frame from ILLIXRFramesClient.
    /// </summary>
    [DllImport(LIB)]
    public static extern void illixr_unity_send_semantic_frame(
        int     frameNumber,
        int     width,          int     height,
        byte[]  image,          int     imageLen,
        int     depthWidth,     int     depthHeight,
        byte[]  depth,          int     depthLen,
        float   depthNearZ,
        float[] intrinsics,
        float[] depthIntrinsics,
        float[] rgbCameraPose,
        float[] depthPose,
        float   maxDepth
    );

    /// <summary>
    /// Send a voice query (PCM16 mono 12 kHz audio) to the switchboard.
    /// Called from AudioStreamController when the user stops recording.
    /// </summary>
    [DllImport(LIB)]
    public static extern void illixr_unity_send_voice_query(
        ulong  queryId,
        byte[] pcmData,
        int    pcmLen,
        float  similarityThreshold,
        float  minMatchSimilarity
    );

    /// <summary>
    /// Poll the switchboard for a query response.
    /// Called from ILLIXRResponsePoller.Update().
    /// </summary>
    /// <returns>1 if a new response is available, 0 otherwise.</returns>
    [DllImport(LIB)]
    public static extern int illixr_unity_get_query_response(
        out ulong queryId,
        float[]   centroids,
        out int   numClouds,
        float[]   colors,
        int       colorsMax,
        out float serverLatency,
        byte[]    textQueryBuf,
        int       textQueryBufLen
    );
}