using UnityEngine;

public class ILLIXRManager : MonoBehaviour {

    void Awake() {
        int result = ILLIXRBridge.illixr_unity_init();
        if (result != 0) {
            Debug.LogError($"[ILLIXR] illixr_unity_init failed with code {result}");
            return;
        }
        Debug.Log("[ILLIXR] Runtime initialized");
        DontDestroyOnLoad(gameObject);
    }

    void OnApplicationQuit() {
        ILLIXRBridge.illixr_unity_shutdown();
        Debug.Log("[ILLIXR] Runtime shutdown");
    }
}