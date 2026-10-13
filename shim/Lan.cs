using System;
using System.IO;
using UnityEngine;

namespace RepoShim
{
    /// <summary>
    /// Hooks for local (same Wi-Fi) multiplayer. The patched game calls these; the iOS mod (RepoIOS.Lan)
    /// fills in the delegates and runs the actual host server / discovery.
    /// </summary>
    public static class Lan
    {
        /// Main menu HOST / JOIN / PLAY RANDOM (0 / 1 / 2): set by the mod.
        public static Action<int> MenuButton;
        /// Runs after DataDirector.PhotonSetAppId: the mod points Photon at the LAN host while a LAN game is on.
        public static Action AfterPhotonSetAppId;

        /// True while this device hosts or joins a LAN game. Set by the mod.
        public static bool Active;
        /// True on the device that runs the server (it keeps the Steam-less player ID "0", like singleplayer).
        public static bool Hosting;

        public static void OnMenuButton(int which)
        {
            if (MenuButton != null) MenuButton(which);
            else Debug.LogWarning("[REPO-iOS] multiplayer menu: no handler");
        }

        /// Lobby INVITE button (Steam overlay on PC): set by the mod.
        public static Action Invite;

        public static void OnInvite()
        {
            if (Invite != null) Invite();
        }

        public static void OnPhotonSetAppId()
        {
            if (AfterPhotonSetAppId != null) AfterPhotonSetAppId();
        }

        /// GameManager.localTest after SemiFunc.MenuActionHostGame: the game's Steam-less multiplayer path.
        public static bool HostLocalTest => Active;

        /// Player ID the game uses instead of the Steam ID. "0" offline and for the host (so the upgrades in its
        /// singleplayer saves still apply); a stable per-device number for players joining someone else's game.
        public static string SteamIdFallback => Active && !Hosting ? DeviceId : "0";

        static string s_DeviceId;

        /// Random 17-digit number, created once and kept in Documents/PlayerId.txt.
        public static string DeviceId
        {
            get
            {
                if (s_DeviceId != null) return s_DeviceId;
                string p = Path.Combine(Platform.DocumentsDir, "PlayerId.txt");
                try
                {
                    if (File.Exists(p))
                    {
                        string s = File.ReadAllText(p).Trim();
                        if (s.Length > 0 && ulong.TryParse(s, out _)) return s_DeviceId = s;
                    }
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[REPO-iOS] player id: " + e.Message);
                }
                var r = new System.Random(Guid.NewGuid().GetHashCode());
                ulong v = 10000000000000000UL + (ulong)(r.NextDouble() * 89999999999999999.0);
                s_DeviceId = v.ToString();
                try { File.WriteAllText(p, s_DeviceId + "\n"); }
                catch (Exception e) { Debug.LogWarning("[REPO-iOS] player id: " + e.Message); }
                return s_DeviceId;
            }
        }
    }
}
