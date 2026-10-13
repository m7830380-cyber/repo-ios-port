using System;
using System.IO;
using RepoShim;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace RepoIOS
{
    // Entry point of the iOS additions.
    public static class Bootstrap
    {
        public static string Documents { get; private set; }
        public static bool OnDevice => Application.platform == RuntimePlatform.IPhonePlayer;

        /// Touch controls run on the device, or on the PC when Documents/touchsim exists (testing).
        public static bool TouchEnabled { get; private set; }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.SubsystemRegistration)]
        static void EarlyInit()
        {
            Documents = Platform.DocumentsDir;
            Log.Init(Documents);
            Log.Info($"R.E.P.O. iOS starting. documents={Documents}");
            Log.Info($"device={SystemInfo.deviceModel} os={SystemInfo.operatingSystem} gpu={SystemInfo.graphicsDeviceName} " +
                     $"api={SystemInfo.graphicsDeviceType} mem={SystemInfo.systemMemorySize}MB gfxmem={SystemInfo.graphicsMemorySize}MB " +
                     $"cpu={SystemInfo.processorCount}x screen={Screen.width}x{Screen.height}@{Screen.currentResolution.refreshRateRatio.value:0}Hz " +
                     $"dpi={Screen.dpi} safe={Screen.safeArea} unity={Application.unityVersion} " +
                     $"astc={SystemInfo.SupportsTextureFormat(TextureFormat.ASTC_4x4)} dxt={SystemInfo.SupportsTextureFormat(TextureFormat.DXT5)} " +
                     $"compute={SystemInfo.supportsComputeShaders}");
        }

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.BeforeSceneLoad)]
        static void Init()
        {
            try
            {
                TouchEnabled = OnDevice || File.Exists(Path.Combine(Documents, "touchsim"));
                if (!OnDevice && Array.IndexOf(Environment.GetCommandLineArgs(), "-repo-docs") >= 0)
                    Application.runInBackground = true;   // several copies on one PC (multiplayer tests)
                Input.simulateMouseWithTouches = false;
                Input.multiTouchEnabled = true;
                Screen.sleepTimeout = SleepTimeout.NeverSleep;
                Platform.ApplyFrameRate();

                var go = new GameObject("REPO-iOS");
                UnityEngine.Object.DontDestroyOnLoad(go);
                go.AddComponent<DebugOverlay>();
                if (TouchEnabled)
                {
                    VInput.Virtual = true;
                    VirtualInput.Init();
                    go.AddComponent<TouchHud>();
                }
                go.AddComponent<Lifecycle>();
                Lan.LanSession.Init(go);
                if (TouchEnabled) Autotest.StartIfRequested(go);
                Log.Info($"touch controls={TouchEnabled} targetFrameRate={Application.targetFrameRate}");

                SceneManager.sceneLoaded += (s, mode) =>
                {
                    Log.Info($"scene loaded: '{s.name}' ({mode}) roots={s.rootCount} " +
                             $"mem={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20}MB " +
                             $"mono={UnityEngine.Profiling.Profiler.GetMonoUsedSizeLong() >> 20}MB");
                    Platform.ApplyFrameRate();
                };
                Application.lowMemory += () => Log.Warn($"iOS low memory warning (allocated {UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() >> 20}MB)");
            }
            catch (Exception e)
            {
                Log.Error("REPO-iOS init failed: " + e);
            }
        }

        sealed class Lifecycle : MonoBehaviour
        {
            void OnApplicationPause(bool paused) => Log.Info(paused ? "app paused (background)" : "app resumed");
            void OnApplicationQuit() => Log.Info("app quitting");
        }
    }
}
