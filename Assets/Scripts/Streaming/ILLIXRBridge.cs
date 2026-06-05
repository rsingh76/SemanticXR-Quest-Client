using System.Runtime.InteropServices;

public static class ILLIXRBridge {
    private const string LIB = "unity_android_interface";

    [DllImport(LIB)]
    public static extern int illixr_unity_init();

    [DllImport(LIB)]
    public static extern void illixr_unity_shutdown();

    [DllImport(LIB)]
    public static extern void illixr_unity_send_semantic_frame(
        int    frameNumber,
        int    width,
        int    height,
        byte[] image,
        int    imageLen,
        int    depthWidth,
        int    depthHeight,
        byte[] depth,
        int    depthLen,
        float  depthNearZ,
        float[] intrinsics,        // length 4
        float[] depthIntrinsics,   // length 4
        float[] rgbCameraPose,     // length 16
        float[] depthPose,         // length 16
        float  maxDepth
    );
    
    [DllImport(LIB)]
    public static extern void illixr_unity_send_voice_query(
        ulong  queryId,
        byte[] pcmData,
        int    pcmLen,
        float  similarityThreshold,
        float  minMatchSimilarity);

    [DllImport(LIB)]
    public static extern int illixr_unity_get_query_response(
        out ulong  queryId,
        float[]    centroids,          // caller allocates float[numClouds * 3]
        out int    numClouds,
        out float  serverLatency,
        byte[]     textQueryBuf,       // caller allocates, UTF-8
        int        textQueryBufLen);

}