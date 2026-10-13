using System;
using System.Collections;
using System.IO;
using UnityEngine;

namespace RepoIOS
{
    /// <summary>
    /// Test autopilot, active only when Documents/autotest exists: starts a new singleplayer game from the
    /// main menu, walks/looks/jumps with the virtual input in the truck, then jumps to a run level and to the
    /// shop, logging state and saving screenshots (Documents/autotest-NN-*.png) along the way.
    /// </summary>
    public class Autotest : MonoBehaviour
    {
        public static bool Active { get; private set; }
        static Vector2 s_Stick, s_Look;
        static bool s_Jump, s_Grab;
        int m_Shot;

        public static void StartIfRequested(GameObject host)
        {
            string flag = Path.Combine(Bootstrap.Documents, "autotest");
            if (!File.Exists(flag)) return;
            Active = true;
            host.AddComponent<Autotest>();
            VirtualInput.Tick += () =>
            {
                VirtualInput.Stick += s_Stick;
                VirtualInput.PointerDelta += s_Look;
                if (s_Jump) VirtualInput.Hold(InputKey.Jump);
                if (s_Grab) VirtualInput.Hold(InputKey.Grab);
            };
            Log.Info("AUTOTEST active");
        }

        IEnumerator Start()
        {
            string mode = "";
            try { mode = File.ReadAllText(Path.Combine(Bootstrap.Documents, "autotest")).Trim(); } catch { }
            if (mode.StartsWith("lan-host")) return LanHost();
            if (mode.StartsWith("lan-join")) return LanJoin(mode.Substring(8).Trim());
            return Singleplayer();
        }

        // ---- local multiplayer: one copy hosts, another (second window on the same PC) joins ------------------
        IEnumerator LanHost()
        {
            yield return WaitFor("main menu", () => FindObjectOfType<MenuPageMain>() != null, 60);
            yield return new WaitForSeconds(3);
            Log.Info("AUTOTEST LAN: host game");
            Lan.LanSession.Host();
            yield return new WaitForSeconds(1.5f);
            Shot("host-saves");
            SemiFunc.MenuActionHostGame();
            yield return WaitFor("host in room", () => Photon.Pun.PhotonNetwork.InRoom, 60);
            yield return WaitFor("lobby", () => SemiFunc.RunIsLobbyMenu() && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated, 120);
            yield return new WaitForSeconds(2);
            Shot("host-lobby");
            yield return WaitFor("second player", () => Photon.Pun.PhotonNetwork.CurrentRoom.PlayerCount >= 2, 240);
            yield return new WaitForSeconds(12);
            LogPlayers("host lobby");
            Shot("host-lobby-2p");
            Log.Info("AUTOTEST LAN: start");
            FindObjectOfType<MenuPageLobby>().ButtonStart();
            yield return new WaitForSeconds(3);
            yield return WaitFor("host level", () => InGameplay() && LevelGenerator.Instance.Generated && !SemiFunc.RunIsLobbyMenu(), 240);
            yield return Walkabout("host-level");
            yield return new WaitForSeconds(15);
            LogPlayers("host level");
            Shot("host-level-later");
            Log.Info("AUTOTEST LAN: next level");
            RunManager.instance.ChangeLevel(_completedLevel: true, _levelFailed: false, RunManager.ChangeLevelType.RunLevel);
            yield return new WaitForSeconds(3);
            yield return WaitFor("host level 2", () => InGameplay() && LevelGenerator.Instance.Generated, 240);
            yield return new WaitForSeconds(10);
            LogPlayers("host level 2");
            Shot("host-level2");
            Log.Info($"AUTOTEST DONE errors={Log.ErrorCount}");
        }

        IEnumerator LanJoin(string address)
        {
            yield return WaitFor("main menu", () => FindObjectOfType<MenuPageMain>() != null, 60);
            yield return new WaitForSeconds(3);
            Lan.LanMenu.Open();
            yield return new WaitForSeconds(4);
            Shot("join-panel");
            yield return WaitFor("a game on the network", () => Lan.LanDiscovery.Games.Count > 0, 120);
            Shot("join-list");
            var games = Lan.LanDiscovery.Games;
            if (games.Count > 0) address = games[0].Address + ":" + games[0].Port;
            Log.Info("AUTOTEST LAN: join " + address);
            Lan.LanMenu.JoinAddress(address);
            yield return WaitFor("joiner in room", () => Photon.Pun.PhotonNetwork.InRoom, 60);
            yield return WaitFor("lobby", () => SemiFunc.RunIsLobbyMenu() && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated, 120);
            yield return new WaitForSeconds(4);
            LogPlayers("join lobby");
            Shot("join-lobby");
            yield return WaitFor("joiner level", () => InGameplay() && LevelGenerator.Instance.Generated && !SemiFunc.RunIsLobbyMenu(), 300);
            yield return Walkabout("join-level");
            LogPlayers("join level");
            yield return new WaitForSeconds(20);
            yield return WaitFor("joiner level 2", () => InGameplay() && LevelGenerator.Instance.Generated, 300);
            yield return new WaitForSeconds(8);
            LogPlayers("join level 2");
            Shot("join-level2");
            Log.Info($"AUTOTEST DONE errors={Log.ErrorCount}");
        }

        static void LogPlayers(string where)
        {
            try
            {
                var room = Photon.Pun.PhotonNetwork.CurrentRoom;
                var sb = new System.Text.StringBuilder($"AUTOTEST {where}: photon players={room?.PlayerCount} master={room?.MasterClientId} me={Photon.Pun.PhotonNetwork.LocalPlayer?.ActorNumber} ping={Photon.Pun.PhotonNetwork.GetPing()}ms servertime={Photon.Pun.PhotonNetwork.ServerTimestamp}; avatars:");
                foreach (var a in SemiFunc.PlayerGetList())
                    sb.Append($" [{SemiFunc.PlayerGetName(a)} id={SemiFunc.PlayerGetSteamID(a)} pos={a.transform.position}]");
                Log.Info(sb.ToString());
            }
            catch (Exception e) { Log.Warn("AUTOTEST players: " + e.Message); }
        }

        IEnumerator Singleplayer()
        {
            yield return WaitFor("main menu", () => FindObjectOfType<MenuPageMain>() != null, 60);
            yield return new WaitForSeconds(3);
            Shot("menu");
            Log.Info("AUTOTEST: new singleplayer game");
            SemiFunc.MainMenuSetSingleplayer();
            SemiFunc.MenuActionSingleplayerGame();
            yield return WaitFor("first level (truck)", () => InGameplay() && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated, 120);
            yield return Walkabout("truck");

            Log.Info("AUTOTEST: change to run level");
            RunManager.instance.ChangeLevel(_completedLevel: true, _levelFailed: false, RunManager.ChangeLevelType.RunLevel);
            yield return new WaitForSeconds(2);
            yield return WaitFor("run level", () => InGameplay() && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated, 180);
            yield return Walkabout("level");

            Log.Info("AUTOTEST: change to shop");
            RunManager.instance.ChangeLevel(_completedLevel: true, _levelFailed: false, RunManager.ChangeLevelType.Shop);
            yield return new WaitForSeconds(2);
            yield return WaitFor("shop", () => InGameplay() && LevelGenerator.Instance != null && LevelGenerator.Instance.Generated, 120);
            yield return Walkabout("shop");
            Log.Info($"AUTOTEST DONE errors={Log.ErrorCount}");
        }

        static bool InGameplay()
        {
            try
            {
                return PlayerController.instance != null && GameDirector.instance != null &&
                       GameDirector.instance.currentState == GameDirector.gameState.Main;
            }
            catch { return false; }
        }

        IEnumerator WaitFor(string what, Func<bool> cond, float timeout)
        {
            float t0 = Time.realtimeSinceStartup;
            while (!SafeCond(cond))
            {
                if (Time.realtimeSinceStartup - t0 > timeout)
                {
                    Log.Error($"AUTOTEST: timeout waiting for {what}");
                    Shot("timeout-" + what);
                    yield break;
                }
                yield return new WaitForSeconds(0.5f);
            }
            Log.Info($"AUTOTEST: reached {what} after {Time.realtimeSinceStartup - t0:0.0}s");
        }

        static bool SafeCond(Func<bool> c)
        {
            try { return c(); } catch { return false; }
        }

        IEnumerator Walkabout(string where)
        {
            yield return new WaitForSeconds(3);
            Shot(where + "-start");
            var p0 = PlayerController.instance.transform.position;
            s_Stick = new Vector2(0, 1);
            yield return new WaitForSeconds(2);
            s_Stick = Vector2.zero;
            s_Look = new Vector2(12, 0);
            yield return new WaitForSeconds(1.5f);
            s_Look = Vector2.zero;
            s_Jump = true;
            yield return new WaitForSeconds(0.2f);
            s_Jump = false;
            s_Stick = new Vector2(0.4f, 1);
            yield return new WaitForSeconds(1.5f);
            s_Stick = Vector2.zero;
            s_Grab = true;
            yield return new WaitForSeconds(1f);
            s_Grab = false;
            var p1 = PlayerController.instance.transform.position;
            Log.Info($"AUTOTEST {where}: moved {(p1 - p0).magnitude:0.00} m, cam yaw {Camera.main?.transform.eulerAngles.y:0}");
            Shot(where + "-end");
        }

        void Shot(string name)
        {
            string p = Path.Combine(Bootstrap.Documents, $"autotest-{m_Shot++:00}-{name}.png");
            ScreenCapture.CaptureScreenshot(p);
            Log.Info("AUTOTEST screenshot " + p);
        }
    }
}
