using System;
using ExitGames.Client.Photon;
using Photon.Pun;
using Photon.Realtime;
using RepoShim;
using ShimLan = RepoShim.Lan;
using UnityEngine;

namespace RepoIOS.Lan
{
    /// <summary>
    /// Local (same Wi-Fi) multiplayer. HOST GAME starts the built-in Photon server and goes on to the save
    /// picker like the PC game; JOIN GAME opens the list of games found on the network. Both then run the
    /// game's own Steam-less multiplayer path (GameManager.localTest) with Photon pointed at the host.
    /// </summary>
    static class LanSession
    {
        static LanServer s_Server;
        static string s_Address = "127.0.0.1";
        static int s_Port = LanServer.DefaultPort;
        static bool s_WasInRoom;
        static float s_IdleTime, s_Started;
        static bool s_SettingsSaved, s_SavedUseNameServer, s_SavedFallback;
        static string s_SavedServer;
        static int s_SavedPort;
        static ConnectionProtocol s_SavedProtocol;
        static string s_Version = "";

        public static bool Hosting => ShimLan.Active && ShimLan.Hosting;
        public static string HostAddress { get; private set; } = "";
        static string HostUserId => "host-" + ShimLan.DeviceId;

        public static void Init(GameObject host)
        {
            ShimLan.MenuButton = OnMenuButton;
            ShimLan.AfterPhotonSetAppId = ApplyPhotonSettings;
            ShimLan.Invite = () => Popup("INVITE",
                ShimLan.Hosting
                    ? $"Friends on the same Wi-Fi tap <b>JOIN GAME</b> in the main menu. Your game shows up there.\n\nIf it doesn't, they can tap JOIN BY IP ADDRESS and type <b>{HostAddress}</b>"
                    : "Only the host can invite. Friends on the same Wi-Fi tap JOIN GAME in the main menu.");
            host.AddComponent<LanMenu>();
            if (Environment.GetCommandLineArgs() is string[] args && Array.IndexOf(args, "-lan-verbose") >= 0) LanServer.Verbose = true;
        }

        static void OnMenuButton(int which)
        {
            try
            {
                switch (which)
                {
                    case 0: Host(); break;
                    case 1: LanMenu.Open(); break;
                    default:
                        MenuManager.instance.PagePopUp("PLAY RANDOM", Color.red,
                            "Random matchmaking needs Steam, so it is not available on iOS.\n\n" +
                            "Play with people on the <b>same Wi-Fi</b> instead: one taps <b>HOST GAME</b>, the others <b>JOIN GAME</b>.",
                            "OK", true);
                        break;
                }
            }
            catch (Exception e)
            {
                Log.Error("multiplayer menu: " + e);
                Popup("MULTIPLAYER", "Something went wrong:\n" + e.Message);
            }
        }

        static void Popup(string header, string text)
        {
            try { MenuManager.instance.PagePopUp(header, Color.red, text, "OK", true); }
            catch (Exception e) { Log.Error("popup: " + e.Message); }
        }

        // ---- host --------------------------------------------------------------------------------------
        public static void Host()
        {
            if (s_Server == null) s_Server = new LanServer();
            if (!s_Server.Running)
            {
                try
                {
                    s_Server.Start(LanServer.DefaultPort);
                }
                catch (Exception e)
                {
                    Log.Warn($"LAN port {LanServer.DefaultPort} busy ({e.Message}), using any free port");
                    s_Server.Start(0);
                }
            }
            s_Server.HostUserId = HostUserId;
            s_Version = SafeVersion();
            HostAddress = LanDiscovery.LocalAddress();
            ShimLan.Active = true;
            ShimLan.Hosting = true;
            s_Address = "127.0.0.1";
            s_Port = s_Server.Port;
            Begin();
            var server = s_Server;
            string version = s_Version;
            LanDiscovery.BeaconText = () =>
            {
                var info = server.GetInfo();
                return info.HasRoom ? LanDiscovery.Beacon(Platform.PlayerName, server.Port, info.Players, info.MaxPlayers, version) : null;
            };
            LanDiscovery.StartBeacon();
            Log.Info($"LAN hosting at {HostAddress}:{s_Port}; choose a save");
            SemiFunc.MainMenuSetMultiplayer();
            MenuManager.instance.PageCloseAll();
            MenuManager.instance.PageOpen(MenuPageIndex.Saves);
        }

        // ---- join --------------------------------------------------------------------------------------
        public static void Join(string address, int port)
        {
            Log.Info($"LAN joining {address}:{port}");
            ShimLan.Active = true;
            ShimLan.Hosting = false;
            s_Address = address;
            s_Port = port;
            s_Version = SafeVersion();
            Begin();
            MenuManager.instance.PageCloseAll();
            // same steps as the game's own Steam invite / local test join
            GameManager.instance.localTest = true;
            SteamManager.instance.joinLobby = false;
            RunManager.instance.ResetProgress();
            StatsManager.instance.saveFileCurrent = "";
            GameManager.instance.SetConnectRandom(_connectRandom: false);
            GameManager.instance.SetLobbyType(GameManager.LobbyTypes.Private);
            RunManager.instance.waitToChangeScene = true;
            RunManager.instance.ChangeLevel(_completedLevel: true, _levelFailed: false, RunManager.ChangeLevelType.LobbyMenu);
            RunManager.instance.lobbyJoin = true;
        }

        static void Begin()
        {
            s_WasInRoom = false;
            s_IdleTime = 0;
            s_Started = Time.unscaledTime;
        }

        static string SafeVersion()
        {
            try { return BuildManager.instance.version.title; } catch { return ""; }
        }

        // ---- Photon settings (runs right after the game's DataDirector.PhotonSetAppId) ---------------------
        static void ApplyPhotonSettings()
        {
            var s = PhotonNetwork.PhotonServerSettings.AppSettings;
            if (!ShimLan.Active)
            {
                Restore();
                return;
            }
            if (!s_SettingsSaved)
            {
                s_SettingsSaved = true;
                s_SavedUseNameServer = s.UseNameServer;
                s_SavedServer = s.Server;
                s_SavedPort = s.Port;
                s_SavedProtocol = s.Protocol;
                s_SavedFallback = s.EnableProtocolFallback;
            }
            s.UseNameServer = false;
            s.Server = s_Address;
            s.Port = s_Port;
            s.Protocol = ConnectionProtocol.Tcp;
            s.EnableProtocolFallback = false;
            s.AuthMode = AuthModeOption.Auth;
            PhotonNetwork.ServerPortOverrides = default(PhotonPortDefinition);
            PhotonNetwork.AuthValues = new AuthenticationValues(ShimLan.Hosting ? HostUserId : ShimLan.DeviceId);
            if (s_Server != null && ShimLan.Hosting)
            {
                string app = s.AppIdRealtime ?? "";
                s_Server.GameAppId = app.Length > 32 ? app.Substring(0, 32) : app;
            }
            Log.Info($"Photon -> LAN server {s_Address}:{s_Port} ({(ShimLan.Hosting ? "host" : "client")}, name '{Platform.PlayerName}')");
        }

        static void Restore()
        {
            if (!s_SettingsSaved) return;
            s_SettingsSaved = false;
            var s = PhotonNetwork.PhotonServerSettings.AppSettings;
            s.UseNameServer = s_SavedUseNameServer;
            s.Server = s_SavedServer;
            s.Port = s_SavedPort;
            s.Protocol = s_SavedProtocol;
            s.EnableProtocolFallback = s_SavedFallback;
        }

        public static void End(string why)
        {
            if (!ShimLan.Active && (s_Server == null || !s_Server.Running)) return;
            Log.Info("LAN session over: " + why);
            LanDiscovery.StopBeacon();
            LanDiscovery.BeaconText = null;
            s_Server?.Stop();
            ShimLan.Active = false;
            ShimLan.Hosting = false;
            try { if (GameManager.instance != null) GameManager.instance.localTest = false; } catch { }
            Restore();
        }

        public static LanServer.Info ServerInfo => s_Server != null && s_Server.Running ? s_Server.GetInfo() : default;

        /// Ends the session once the player is back in the main menu and disconnected.
        internal static void Tick()
        {
            if (!ShimLan.Active) return;
            if (PhotonNetwork.InRoom) s_WasInRoom = true;
            var st = PhotonNetwork.NetworkClientState;
            bool idle = st == ClientState.Disconnected || st == ClientState.PeerCreated;
            bool menu = false;
            try { menu = RunManager.instance != null && SemiFunc.IsMainMenu(); } catch { }
            // a host without a room is still picking a save; a joiner that never got in has failed
            bool hostGaveUp = false;
            try { hostGaveUp = ShimLan.Hosting && !s_WasInRoom && menu && !SemiFunc.MainMenuIsMultiplayer(); } catch { }
            bool over = idle && menu && (s_WasInRoom || hostGaveUp || (!ShimLan.Hosting && Time.unscaledTime - s_Started > 6f));
            if (!over) { s_IdleTime = 0; return; }
            s_IdleTime += Time.unscaledDeltaTime;
            if (s_IdleTime > 1.5f) End(s_WasInRoom ? "left the game" : "could not join");
        }
    }
}
