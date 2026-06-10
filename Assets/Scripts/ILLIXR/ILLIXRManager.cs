// Copyright 2020-2026, The Board of Trustees of the University of Illinois.
// SPDX-License-Identifier: BSL-1.0

using UnityEngine;

namespace SemanticXR
{
    /// <summary>
    /// Lifecycle manager for the ILLIXR runtime.
    /// Receives network configuration from StreamingOrchestrator,
    /// sets the required environment variables via illixr_unity_set_env(),
    /// then calls illixr_unity_init().
    ///
    /// Must be initialized explicitly via Initialize() before the ILLIXR
    /// transport is used — called by StreamingOrchestrator.Connect() when
    /// FramesTransport.Illixr is selected.
    /// </summary>
    public class ILLIXRManager : MonoBehaviour
    {
        public bool IsInitialized { get; private set; }

        /// <summary>
        /// Set ILLIXR environment variables and initialize the runtime.
        /// Must be called before any ILLIXR switchboard reads or writes.
        /// </summary>
        /// <param name="serverIp">ILLIXR_TCP_SERVER_IP — server the headset sends data to.</param>
        /// <param name="serverPort">ILLIXR_TCP_SERVER_PORT — port on the server.</param>
        /// <param name="clientIp">ILLIXR_TCP_CLIENT_IP — headset's own IP for the return channel.</param>
        /// <param name="clientPort">ILLIXR_TCP_CLIENT_PORT — port on the headset for responses.</param>
        public void Initialize(string serverIp, int serverPort,
                               string clientIp, int clientPort)
        {
            if (IsInitialized)
            {
                Debug.LogWarning("[ILLIXRManager] Already initialized — ignoring.");
                return;
            }

            // Set environment variables before loading ILLIXR libraries.
            // These are read by the tcp_network_backend plugin during init.
            SetEnv("ILLIXR_TCP_SERVER_IP",   serverIp);
            SetEnv("ILLIXR_TCP_SERVER_PORT",  serverPort.ToString());
            SetEnv("ILLIXR_TCP_CLIENT_IP",   clientIp);
            SetEnv("ILLIXR_TCP_CLIENT_PORT",  clientPort.ToString());

            int result = ILLIXRBridge.illixr_unity_init();
            if (result != 0)
            {
                Debug.LogError($"[ILLIXRManager] illixr_unity_init failed with code {result}");
                return;
            }

            IsInitialized = true;
            Debug.Log($"[ILLIXRManager] Runtime initialized. " +
                      $"Server={serverIp}:{serverPort} Client={clientIp}:{clientPort}");

            DontDestroyOnLoad(gameObject);
        }

        void OnApplicationQuit()
        {
            if (!IsInitialized) return;
            ILLIXRBridge.illixr_unity_shutdown();
            IsInitialized = false;
            Debug.Log("[ILLIXRManager] Runtime stopped.");
        }

        static void SetEnv(string name, string value)
        {
            int result = ILLIXRBridge.illixr_unity_set_env(name, value);
            if (result != 0)
                Debug.LogError($"[ILLIXRManager] Failed to set {name}={value}");
            else
                Debug.Log($"[ILLIXRManager] {name}={value}");
        }
    }
}