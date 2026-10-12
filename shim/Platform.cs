using System;
using System.Diagnostics;
using System.IO;
using UnityEngine;
using Debug = UnityEngine.Debug;

namespace RepoShim
{
    /// <summary>Stand-ins for desktop-only calls the patcher redirects here.</summary>
    public static class Platform
    {
        // ---- System.Diagnostics.Process: there are no other processes on iOS ------------------
        public static Process ProcessStart(string file)
        {
            if (file != null && (file.StartsWith("http://") || file.StartsWith("https://")))
            {
                Application.OpenURL(file);
                return null;
            }
            Debug.LogWarning("[REPO-iOS] Process.Start blocked: " + file);
            return null;
        }

        // ---- player identity (the game falls back to Environment.MachineName without Steam) -----
        static string playerName;

        /// Local player name: first line of Documents/PlayerName.txt, else "Player".
        public static string PlayerName
        {
            get
            {
                if (playerName != null) return playerName;
                playerName = "Player";
                try
                {
                    string p = Path.Combine(Application.persistentDataPath, "PlayerName.txt");
                    if (File.Exists(p))
                    {
                        string n = File.ReadAllText(p).Trim();
                        int nl = n.IndexOfAny(new[] { '\r', '\n' });
                        if (nl >= 0) n = n.Substring(0, nl).Trim();
                        if (n.Length > 0) playerName = n.Length > 32 ? n.Substring(0, 32) : n;
                    }
                    else
                    {
                        File.WriteAllText(p, playerName + "\n");
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[REPO-iOS] player name: " + e.Message);
                }
                return playerName;
            }
        }

        // ---- window / resolution: the iPhone screen is always fullscreen ---------------------------
        public static void SetResolution(int width, int height, bool fullscreen)
        {
            // The game re-applies the desktop resolution (Screen.currentResolution) whenever its window mode
            // setting changes; on iOS that would fight the render scale the mod picks.
        }

        // ---- frame pacing ----------------------------------------------------------------------
        // iOS ignores QualitySettings.vSyncCount and runs at 30 fps unless targetFrameRate is set, so the
        // game's VSync / Max FPS options are mapped onto "match the display" capped by the user's choice.
        static int vSyncCount = 1;
        static int requestedFps = -1;

        /// Highest refresh rate the display supports (120 on ProMotion iPhones).
        public static int DisplayMaxFps
        {
            get
            {
                int r = (int)Math.Round(Screen.currentResolution.refreshRateRatio.value);
                return r >= 30 ? r : 60;
            }
        }

        /// Cap set by the iOS mod (0 = no cap), default 60 for stable frame pacing and battery.
        public static int UserFpsCap = 60;

        public static int EffectiveTargetFps
        {
            get
            {
                int max = DisplayMaxFps;
                if (UserFpsCap > 0) max = Math.Min(max, UserFpsCap);
                if (vSyncCount > 0 || requestedFps <= 0) return max;
                return Math.Max(30, Math.Min(requestedFps, max));
            }
        }

        public static void SetVSyncCount(int v)
        {
            vSyncCount = v;
            ApplyFrameRate();
        }

        public static void SetTargetFrameRate(int fps)
        {
            requestedFps = fps;
            ApplyFrameRate();
        }

        public static void ApplyFrameRate()
        {
            QualitySettings.vSyncCount = 0;
            Application.targetFrameRate = EffectiveTargetFps;
        }
    }

    /// <summary>
    /// Cursor.lockState / Cursor.visible mean nothing on iOS, but they are how the game says "a cursor UI
    /// is up" (menus) versus "mouse look" (gameplay). The game's setters are routed through here so the
    /// touch layer can tell which mode it is in.
    /// </summary>
    public static class IosCursor
    {
        public static CursorLockMode LockState = CursorLockMode.None;
        public static bool Visible = true;
        /// Bumped on every set.
        public static int Version;

        public static void SetLockState(CursorLockMode value)
        {
            LockState = value;
            Version++;
            Cursor.lockState = value;
        }

        public static void SetVisible(bool value)
        {
            Visible = value;
            Version++;
            Cursor.visible = value;
        }

        public static CursorLockMode GetLockState() => LockState;
    }

    /// <summary>
    /// UnityEngine.Microphone replacement: voice chat is disabled on iOS, so there is never a microphone.
    /// This keeps the iOS permission prompt and AVAudioSession record mode from ever being triggered.
    /// </summary>
    public static class Mic
    {
        public static string[] devices => new string[0];
        public static AudioClip Start(string deviceName, bool loop, int lengthSec, int frequency) => null;
        public static void End(string deviceName) { }
        public static bool IsRecording(string deviceName) => false;
        public static int GetPosition(string deviceName) => 0;
        public static void GetDeviceCaps(string deviceName, out int minFreq, out int maxFreq) { minFreq = 0; maxFreq = 0; }
    }
}
