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
            yield return WaitFor("main menu", () => FindObjectOfType<MenuPageMain>() != null, 60);
            yield return new WaitForSeconds(3);
            Shot("menu");
            Log.Info("AUTOTEST: host game button (expect 'not available' popup)");
            FindObjectOfType<MenuPageMain>().ButtonEventHostGame();
            yield return new WaitForSeconds(1.5f);
            Shot("menu-host-popup");
            MenuManager.instance.PageCloseAll();
            yield return new WaitForSeconds(1f);
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
