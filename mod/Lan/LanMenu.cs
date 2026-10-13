using System;
using System.Collections.Generic;
using RepoShim;
using UnityEngine;

namespace RepoIOS.Lan
{
    /// <summary>
    /// JOIN GAME screen (games found on the Wi-Fi, join by IP address, player name) and the small
    /// "Wi-Fi game" banner the host sees in the lobby. Drawn with IMGUI; taps come from the touch layer.
    /// </summary>
    public class LanMenu : MonoBehaviour
    {
        static LanMenu s_Instance;
        static bool s_Open;
        enum Edit { None, Name, Address }
        static Edit s_Edit;
        static TouchScreenKeyboard s_Keyboard;
        static string s_Status = "";
        static readonly List<(Rect rect, Action action)> s_Buttons = new List<(Rect, Action)>();
        static string s_LastAddress = "";

        GUIStyle m_Title, m_Text, m_Button, m_Small;

        public static bool IsOpen => s_Open;

        void Awake() => s_Instance = this;

        public static void Open()
        {
            s_Open = true;
            s_Status = "";
            s_Edit = Edit.None;
            LanDiscovery.StartListening();
            Log.Info("LAN join screen opened");
        }

        public static void Close()
        {
            s_Open = false;
            s_Edit = Edit.None;
            if (s_Keyboard != null) { s_Keyboard.active = false; s_Keyboard = null; }
            LanDiscovery.StopListening();
        }

        /// Touch layer: a new touch at pos (pixels, origin bottom-left). True = consumed.
        public static bool HandleTouch(Vector2 pos)
        {
            if (!s_Open) return false;
            Tap(new Vector2(pos.x, Screen.height - pos.y));
            return true;
        }

        static void Tap(Vector2 gui)
        {
            if (s_Keyboard != null) return;
            foreach (var (rect, action) in s_Buttons)
                if (rect.Contains(gui))
                {
                    try { action(); }
                    catch (Exception e) { Log.Error("LAN menu: " + e); s_Status = e.Message; }
                    return;
                }
        }

        void Update()
        {
            LanSession.Tick();
            if (!s_Open) return;
            // PC without touch controls (testing): the real mouse
            if (!Bootstrap.TouchEnabled && Input.GetMouseButtonDown(0))
                Tap(new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y));
            if (s_Keyboard != null)
            {
                var st = s_Keyboard.status;
                if (st == TouchScreenKeyboard.Status.Done)
                {
                    string text = (s_Keyboard.text ?? "").Trim();
                    s_Keyboard = null;
                    Edited(text);
                }
                else if (st != TouchScreenKeyboard.Status.Visible)
                {
                    s_Keyboard = null;
                    s_Edit = Edit.None;
                }
            }
            try
            {
                if (SemiFunc.IsMainMenu() == false) Close();   // left the menu some other way
            }
            catch { }
        }

        static void StartEdit(Edit what, string current, TouchScreenKeyboardType type)
        {
            s_Edit = what;
            if (TouchScreenKeyboard.isSupported)
            {
                TouchScreenKeyboard.hideInput = false;
                s_Keyboard = TouchScreenKeyboard.Open(current, type, false, false, false, false,
                    what == Edit.Name ? "Your name" : "Host IP address, e.g. 192.168.1.20");
            }
            else
            {
                s_Status = "Typing needs the on-screen keyboard (iPhone).";
                s_Edit = Edit.None;
            }
        }

        static void Edited(string text)
        {
            var what = s_Edit;
            s_Edit = Edit.None;
            if (text.Length == 0) return;
            if (what == Edit.Name)
            {
                Platform.SetPlayerName(text);
                Log.Info("player name set to " + Platform.PlayerName);
            }
            else if (what == Edit.Address)
            {
                s_LastAddress = text;
                ParseAddress(text, out string host, out int port);
                LanSession.Join(host, port);
                Close();
            }
        }

        static void ParseAddress(string text, out string host, out int port)
        {
            host = text;
            port = LanServer.DefaultPort;
            int c = text.LastIndexOf(':');
            if (c > 0 && int.TryParse(text.Substring(c + 1), out int p)) { host = text.Substring(0, c); port = p; }
        }

        /// Testing hook (autotest): join without the keyboard.
        public static void JoinAddress(string text)
        {
            ParseAddress(text, out string host, out int port);
            Close();
            LanSession.Join(host, port);
        }

        // ---- drawing ---------------------------------------------------------------------------------------
        void OnGUI()
        {
            float h = Screen.height;
            if (m_Title == null)
            {
                m_Title = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                m_Text = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, wordWrap = true };
                m_Small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
                m_Button = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold, wordWrap = true };
            }
            m_Title.fontSize = Mathf.RoundToInt(h * 0.06f);
            m_Text.fontSize = Mathf.RoundToInt(h * 0.035f);
            m_Small.fontSize = Mathf.RoundToInt(h * 0.03f);
            m_Button.fontSize = Mathf.RoundToInt(h * 0.038f);

            if (!s_Open)
            {
                Banner();
                return;
            }
            if (Event.current.type == EventType.Layout) return;
            bool paint = Event.current.type == EventType.Repaint;
            if (paint) s_Buttons.Clear();
            if (!paint) return;

            Rect safe = Screen.safeArea;
            safe.y = h - safe.yMax;   // GUI space
            var panel = new Rect(safe.x + safe.width * 0.12f, safe.y + h * 0.05f, safe.width * 0.76f, safe.height - h * 0.1f);
            Fill(new Rect(0, 0, Screen.width, h), new Color(0, 0, 0, 0.6f));
            Fill(panel, new Color(0.07f, 0.07f, 0.07f, 0.96f));
            float pad = h * 0.03f, y = panel.y + pad, x = panel.x + pad, w = panel.width - pad * 2;
            GUI.color = new Color(1f, 0.62f, 0.1f);
            GUI.Label(new Rect(x, y, w, h * 0.08f), "JOIN A WI-FI GAME", m_Title);
            GUI.color = Color.white;
            y += h * 0.09f;

            // name
            GUI.Label(new Rect(x, y, w * 0.65f, h * 0.08f), "Your name:  <b>" + Esc(Platform.PlayerName) + "</b>", Rich(m_Text));
            Button(new Rect(x + w * 0.68f, y, w * 0.32f, h * 0.08f), "CHANGE NAME", () => StartEdit(Edit.Name, Platform.PlayerName, TouchScreenKeyboardType.Default), false);
            y += h * 0.11f;

            // games found
            var games = LanDiscovery.Games;
            if (games.Count == 0)
            {
                string msg = LanDiscovery.ListenError != null
                    ? "Can't search the network (" + LanDiscovery.ListenError + "). Allow \"Local Network\" for R.E.P.O. in Settings, or join by IP."
                    : "Looking for games on this Wi-Fi" + new string('.', 1 + (int)(Time.unscaledTime * 2) % 3) +
                      "\n\nThe host taps HOST GAME and picks a save first. Both phones must be on the same Wi-Fi.";
                GUI.Label(new Rect(x, y, w, h * 0.3f), msg, m_Small);
                y += h * 0.32f;
            }
            else
            {
                foreach (var g in games)
                {
                    if (y > panel.yMax - h * 0.3f) break;
                    var gg = g;
                    string label = $"{Esc(g.Host)}'s game   {g.Players}/{(g.MaxPlayers > 0 ? g.MaxPlayers.ToString() : "?")} players";
                    if (!string.IsNullOrEmpty(g.Version) && g.Version != CurrentVersion())
                        label += $"\n<size={m_Small.fontSize}><color=#ff6060>version {Esc(g.Version)} (yours: {Esc(CurrentVersion())})</color></size>";
                    Button(new Rect(x, y, w, h * 0.11f), label, () =>
                    {
                        Close();
                        LanSession.Join(gg.Address, gg.Port);
                    }, true);
                    y += h * 0.13f;
                }
            }

            if (!string.IsNullOrEmpty(s_Status))
            {
                GUI.color = new Color(1f, 0.5f, 0.5f);
                GUI.Label(new Rect(x, panel.yMax - pad - h * 0.2f, w, h * 0.07f), s_Status, m_Small);
                GUI.color = Color.white;
            }
            float by = panel.yMax - pad - h * 0.11f;
            Button(new Rect(x, by, w * 0.48f, h * 0.11f), "JOIN BY IP ADDRESS", () => StartEdit(Edit.Address, s_LastAddress, TouchScreenKeyboardType.URL), false);
            Button(new Rect(x + w * 0.52f, by, w * 0.48f, h * 0.11f), "BACK", Close, false);
        }

        static string CurrentVersion()
        {
            try { return BuildManager.instance.version.title; } catch { return ""; }
        }

        // Host: what to tell the others while in the lobby.
        void Banner()
        {
            if (Event.current.type != EventType.Repaint || !LanSession.Hosting) return;
            bool lobby = false;
            try { lobby = SemiFunc.RunIsLobbyMenu(); } catch { }
            if (!lobby) return;
            var info = LanSession.ServerInfo;
            float h = Screen.height;
            Rect safe = Screen.safeArea;
            string text = $"WI-FI GAME  ·  {info.Players} player{(info.Players == 1 ? "" : "s")}  ·  others tap JOIN GAME (or join by IP {LanSession.HostAddress})";
            var r = new Rect(safe.x + h * 0.03f, h - safe.yMax + h * 0.015f, safe.width * 0.7f, h * 0.06f);
            Fill(r, new Color(0, 0, 0, 0.55f));
            GUI.Label(new Rect(r.x + h * 0.015f, r.y, r.width - h * 0.03f, r.height), text, m_Text);
        }

        static GUIStyle Rich(GUIStyle s)
        {
            s.richText = true;
            return s;
        }

        static string Esc(string s) => (s ?? "").Replace("<", "‹").Replace(">", "›");

        void Button(Rect r, string label, Action action, bool primary)
        {
            Fill(r, primary ? new Color(1f, 0.55f, 0.1f, 0.9f) : new Color(1f, 1f, 1f, 0.14f));
            GUI.color = primary ? Color.black : Color.white;
            m_Button.richText = true;
            GUI.Label(r, label, m_Button);
            GUI.color = Color.white;
            s_Buttons.Add((r, action));
        }

        static void Fill(Rect r, Color c)
        {
            var prev = GUI.color;
            GUI.color = c;
            GUI.DrawTexture(r, Texture2D.whiteTexture);
            GUI.color = prev;
        }
    }
}
