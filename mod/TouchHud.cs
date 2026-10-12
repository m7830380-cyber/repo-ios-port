using System;
using System.Collections.Generic;
using RepoShim;
using UnityEngine;
using UnityEngine.InputSystem;

namespace RepoIOS
{
    /// <summary>
    /// On-screen controls. Every touch is turned into requests on <see cref="VirtualInput"/>, so the game
    /// sees ordinary keyboard/mouse input and keeps its own logic and settings.
    ///
    ///  * gameplay (cursor locked): floating stick on the left, drag-to-look on the right, buttons for every
    ///    action of the game's InputKey list. GRAB / ROTATE also look while held (that is how objects are
    ///    carried and turned); PUSH / PULL (mouse wheel) appear while something is held.
    ///  * menus (cursor free): the finger is the mouse. It moves the pointer, then holds the left button
    ///    (tap = click, drag = sliders); a second finger dragging up/down is the wheel.
    ///  * text entry (chat, save names): the iOS keyboard; typed text goes to the game's text input.
    /// </summary>
    public class TouchHud : MonoBehaviour
    {
        public static TouchHud Instance;

        // ---- settings (PlayerPrefs) -----------------------------------------------------------------
        public static float LookScale = 1f;
        public static float Opacity = 1f;
        public static bool EdgeSprint = true;
        public static bool GrabToggle;
        public static bool Hidden;

        enum Mode { Menu, Gameplay, Text }

        enum Shape { Circle, Rect }

        class Btn
        {
            public string Label;
            public InputKey Key;
            public bool HasKey;
            public Action OnTap;               // HUD-only buttons
            public bool Look;                  // dragging from this button also aims
            public bool Latch;                 // tap toggles a held state
            public bool Repeat;                // held: pulses (wheel)
            public float RepeatDir;
            public Func<bool> Visible;
            public float X, Y, R;              // anchor position in screen-height units
            public int AnchorX;                // -1 left edge, 0 centre, +1 right edge
            public int AnchorY;                // -1 bottom, +1 top
            // runtime
            public Vector2 Center;
            public float Radius;
            public int Finger = -1;
            public bool Latched;
            public float RepeatTimer;
            public bool Shown;
            public bool Down => Finger >= 0 || Latched;
        }

        readonly List<Btn> m_Buttons = new List<Btn>();
        Btn m_Grab, m_Rotate, m_Sprint, m_Crouch, m_Hide, m_Gear, m_Emote;
        bool m_EmotesOpen, m_SettingsOpen;

        Mode m_Mode = Mode.Menu;
        int m_StickFinger = -1;
        Vector2 m_StickOrigin, m_StickPos;
        float m_EdgeTime;
        int m_LookFinger = -1;
        Vector2 m_LookLast;
        readonly Dictionary<InputKey, int> m_Taps = new Dictionary<InputKey, int>();

        // menu pointer
        int m_PointerFinger = -1;
        int m_PointerFrames;
        Vector2 m_PointerPos = new Vector2(-1000, -1000);
        int m_ClickHold;
        int m_ScrollFinger = -1;
        Vector2 m_ScrollLast;
        float m_ScrollAccum;

        // text entry
        TouchScreenKeyboard m_Keyboard;
        string m_KbLast = "";
        readonly Queue<string> m_TextOps = new Queue<string>();
        int m_BackspaceFrames;

        Texture2D m_Circle, m_Ring;
        GUIStyle m_Label, m_Small;
        Rect m_SettingsRect;

        void Awake()
        {
            Instance = this;
            LoadSettings();
            Func<bool> grabbing = () => IsGrabbing();
            Func<bool> spectating = () => IsSpectating();
            Func<bool> alive = () => !IsSpectating();

            m_Grab = Add("GRAB", InputKey.Grab, 1, 0.26f, -1, 0.25f, 0.115f, alive);
            m_Grab.Look = true;
            Add("JUMP", InputKey.Jump, 1, 0.09f, -1, 0.31f, 0.08f, alive);
            m_Crouch = Add("CROUCH", InputKey.Crouch, 1, 0.10f, -1, 0.12f, 0.065f, alive);
            m_Crouch.Latch = true;
            Add("TUMBLE", InputKey.Tumble, 1, 0.45f, -1, 0.10f, 0.06f, alive);
            Add("USE", InputKey.Interact, 1, 0.46f, -1, 0.29f, 0.065f, alive);
            m_Rotate = Add("ROTATE", InputKey.Rotate, 1, 0.44f, -1, 0.47f, 0.065f, grabbing);
            m_Rotate.Look = true;
            var push = Add("PUSH", InputKey.Push, 1, 0.24f, -1, 0.50f, 0.055f, grabbing);
            push.Repeat = true; push.RepeatDir = 1f;
            var pull = Add("PULL", InputKey.Pull, 1, 0.12f, -1, 0.47f, 0.055f, grabbing);
            pull.Repeat = true; pull.RepeatDir = -1f;
            m_Sprint = Add("SPRINT", InputKey.Sprint, -1, 0.09f, -1, 0.55f, 0.055f, alive);
            m_Sprint.Latch = true;

            Add("<", InputKey.SpectatePrevious, 1, 0.34f, -1, 0.20f, 0.07f, spectating);
            Add(">", InputKey.SpectateNext, 1, 0.14f, -1, 0.20f, 0.07f, spectating);

            // top row
            Add("MENU", InputKey.Menu, -1, 0.07f, 1, 0.07f, 0.045f, null);
            var map = Add("MAP", InputKey.Map, -1, 0.18f, 1, 0.07f, 0.045f, alive);
            map.Latch = true;
            Add("CHAT", InputKey.Chat, -1, 0.29f, 1, 0.07f, 0.045f, null);
            Add("1", InputKey.Inventory1, 0, -0.11f, 1, 0.07f, 0.045f, alive);
            Add("2", InputKey.Inventory2, 0, 0f, 1, 0.07f, 0.045f, alive);
            Add("3", InputKey.Inventory3, 0, 0.11f, 1, 0.07f, 0.045f, alive);
            m_Emote = AddHud(":)", 1, 0.62f, 1, 0.07f, 0.045f, () => m_EmotesOpen = !m_EmotesOpen, alive);
            var exprKeys = new[] { InputKey.Expression1, InputKey.Expression2, InputKey.Expression3, InputKey.Expression4, InputKey.Expression5, InputKey.Expression6 };
            for (int i = 0; i < 6; i++)
                Add("E" + (i + 1), exprKeys[i], 1, 0.62f, 1, 0.17f + i * 0.095f, 0.042f, () => m_EmotesOpen && !IsSpectating());
            m_Gear = AddHud("SET", 1, 0.51f, 1, 0.07f, 0.045f, () => m_SettingsOpen = !m_SettingsOpen, null);
            m_Hide = AddHud("HIDE", 1, 0.40f, 1, 0.07f, 0.045f, () => { Hidden = !Hidden; SaveSettings(); }, null);

            m_Circle = MakeCircle(128, false);
            m_Ring = MakeCircle(128, true);
            VirtualInput.Tick += OnInputTick;
        }

        Btn Add(string label, InputKey key, int ax, float x, int ay, float y, float r, Func<bool> visible)
        {
            var b = new Btn { Label = label, Key = key, HasKey = true, AnchorX = ax, X = x, AnchorY = ay, Y = y, R = r, Visible = visible };
            m_Buttons.Add(b);
            return b;
        }

        Btn AddHud(string label, int ax, float x, int ay, float y, float r, Action tap, Func<bool> visible)
        {
            var b = new Btn { Label = label, OnTap = tap, AnchorX = ax, X = x, AnchorY = ay, Y = y, R = r, Visible = visible };
            m_Buttons.Add(b);
            return b;
        }

        // ---- game state ---------------------------------------------------------------------------
        static bool IsGrabbing()
        {
            try { return PhysGrabber.instance != null && PhysGrabber.instance.grabbed; } catch { return false; }
        }

        static bool IsSpectating()
        {
            try { return SpectateCamera.instance != null && SpectateCamera.instance.isActiveAndEnabled; } catch { return false; }
        }

        static bool TextInputActive()
        {
            try
            {
                if (ChatManager.instance != null && ChatManager.instance.StateIsActive()) return true;
                if (MenuManager.instance != null && MenuManager.instance.textInputActive) return true;
            }
            catch { }
            return false;
        }

        static bool GameplayActive()
        {
            // the game locks the cursor for mouse look and frees it for every menu (CursorManager)
            if (IosCursor.LockState != CursorLockMode.Locked) return false;
            try
            {
                if (PlayerController.instance == null) return false;
                if (GameDirector.instance != null)
                {
                    var s = GameDirector.instance.currentState;
                    if (s != GameDirector.gameState.Main && s != GameDirector.gameState.Death) return false;
                }
            }
            catch { return false; }
            return true;
        }

        // ---- per frame (called by VirtualInput right before the game reads input) -----------------
        void OnInputTick()
        {
            Layout();
            Mode mode = TextInputActive() ? Mode.Text : GameplayActive() ? Mode.Gameplay : Mode.Menu;
            if (mode != m_Mode)
            {
                Log.Info($"input mode {m_Mode} -> {mode}");
                ReleaseAll();
                if (mode == Mode.Gameplay) HudOwnsToggles();
                m_Mode = mode;
            }
            ControllerBridge.Tick(mode == Mode.Gameplay);
            switch (mode)
            {
                case Mode.Gameplay: Gameplay(); break;
                case Mode.Menu: Menu(); break;
                case Mode.Text: Text(); break;
            }
            if (mode != Mode.Text) CloseKeyboard();
            // one-frame taps (2 frames down so the game's WasPressedThisFrame always sees them)
            var done = new List<InputKey>();
            foreach (var kv in m_Taps)
            {
                VirtualInput.Hold(kv.Key);
                if (kv.Value <= 1) done.Add(kv.Key);
            }
            foreach (var k in done) m_Taps.Remove(k);
            foreach (var k in new List<InputKey>(m_Taps.Keys)) m_Taps[k]--;
        }

        void Tap(InputKey k) => m_Taps[k] = 2;

        // CROUCH / SPRINT / MAP latch in the HUD, GRAB has its own toggle option: the game's "toggle"
        // settings for these must be off or a held virtual key would flip them once and stick.
        static void HudOwnsToggles()
        {
            try
            {
                var im = InputManager.instance;
                if (im == null) return;
                foreach (var k in new[] { InputKey.Crouch, InputKey.Sprint, InputKey.Map, InputKey.Grab })
                    if (im.InputToggleGet(k)) im.InputToggleRebind(k, false);
            }
            catch (Exception e) { Log.Warn("toggle settings: " + e.Message); }
        }

        void Layout()
        {
            float h = Screen.height, w = Screen.width;
            var safe = Screen.safeArea;
            foreach (var b in m_Buttons)
            {
                b.Radius = b.R * h;
                float x = b.AnchorX < 0 ? safe.xMin + b.X * h : b.AnchorX > 0 ? safe.xMax - b.X * h : w * 0.5f + b.X * h;
                float y = b.AnchorY < 0 ? safe.yMin + b.Y * h : safe.yMax - b.Y * h;
                b.Center = new Vector2(x, y);
            }
        }

        void ReleaseAll()
        {
            foreach (var b in m_Buttons) { b.Finger = -1; b.RepeatTimer = 0; }
            m_StickFinger = -1;
            m_LookFinger = -1;
            m_PointerFinger = -1;
            m_ScrollFinger = -1;
            m_Taps.Clear();
        }

        // ---- gameplay -----------------------------------------------------------------------------
        void Gameplay()
        {
            foreach (var b in m_Buttons)
            {
                bool shown = (b.Visible == null || b.Visible()) && (!Hidden || b == m_Hide);
                if (!shown && b.Shown) { b.Finger = -1; if (b != m_Crouch && b != m_Sprint) b.Latched = false; }
                b.Shown = shown;
            }
            var seen = new HashSet<int>();
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                if (t.phase == UnityEngine.TouchPhase.Ended || t.phase == UnityEngine.TouchPhase.Canceled)
                {
                    Release(t.fingerId);
                    continue;
                }
                seen.Add(t.fingerId);
                if (t.phase == UnityEngine.TouchPhase.Began || !IsTracked(t.fingerId)) Begin(t.fingerId, t.position);
                Move(t.fingerId, t.position);
            }
            foreach (var b in m_Buttons) if (b.Finger >= 0 && !seen.Contains(b.Finger)) b.Finger = -1;
            if (m_StickFinger >= 0 && !seen.Contains(m_StickFinger)) m_StickFinger = -1;
            if (m_LookFinger >= 0 && !seen.Contains(m_LookFinger)) m_LookFinger = -1;

            // movement
            bool edge = false;
            if (m_StickFinger >= 0)
            {
                float rad = 0.12f * Screen.height;
                Vector2 v = (m_StickPos - m_StickOrigin) / rad;
                if (v.magnitude > 1f)
                {
                    // drag the origin along so the stick never needs to be re-centred
                    m_StickOrigin = m_StickPos - v.normalized * rad;
                    v = v.normalized;
                }
                if (v.magnitude < 0.1f) v = Vector2.zero;
                VirtualInput.Stick = v;
                edge = v.magnitude > 0.97f && v.y > 0.5f;
            }
            m_EdgeTime = edge ? m_EdgeTime + Time.unscaledDeltaTime : 0f;

            foreach (var b in m_Buttons)
            {
                if (!b.Shown || !b.HasKey) continue;
                if (b.Repeat)
                {
                    if (b.Finger < 0) continue;
                    b.RepeatTimer -= Time.unscaledDeltaTime;
                    if (b.RepeatTimer <= 0f)
                    {
                        VirtualInput.Scroll += b.RepeatDir;
                        b.RepeatTimer = 0.06f;
                    }
                    continue;
                }
                if (b == m_Grab && GrabToggle)
                {
                    if (b.Latched) VirtualInput.Hold(b.Key);
                    continue;
                }
                if (b.Down) VirtualInput.Hold(b.Key);
            }
            if (EdgeSprint && m_EdgeTime > 0.12f) VirtualInput.Hold(InputKey.Sprint);
        }

        bool IsTracked(int id)
        {
            if (id == m_StickFinger || id == m_LookFinger) return true;
            foreach (var b in m_Buttons) if (b.Finger == id) return true;
            return false;
        }

        Btn HitButton(Vector2 pos)
        {
            Btn hit = null;
            float best = float.MaxValue;
            foreach (var b in m_Buttons)
            {
                if (!b.Shown || b.Finger >= 0) continue;
                float d = Vector2.Distance(pos, b.Center);
                if (d <= b.Radius * 1.2f && d < best) { best = d; hit = b; }
            }
            return hit;
        }

        void Begin(int id, Vector2 pos)
        {
            if (DebugOverlay.HandleTouch(pos)) return;
            if (m_SettingsOpen && SettingsTouch(pos)) return;
            var hit = HitButton(pos);
            if (hit != null)
            {
                hit.Finger = id;
                if (hit.OnTap != null) hit.OnTap();
                else if (hit.Latch || (hit == m_Grab && GrabToggle)) hit.Latched = !hit.Latched;
                else if (!hit.Repeat && IsTapKey(hit.Key)) Tap(hit.Key);
                if (hit.Repeat) hit.RepeatTimer = 0f;
                if (hit.Look && m_LookFinger < 0) { m_LookFinger = id; m_LookLast = pos; }
                return;
            }
            if (Hidden) { /* hidden HUD: whole left half still moves, right half still looks */ }
            if (pos.x < Screen.width * 0.42f)
            {
                if (m_StickFinger < 0) { m_StickFinger = id; m_StickOrigin = pos; m_StickPos = pos; }
            }
            else if (m_LookFinger < 0)
            {
                m_LookFinger = id;
                m_LookLast = pos;
            }
        }

        // keys the game only reads on press (a tap is a clean press); everything else is held while touched
        static bool IsTapKey(InputKey k) =>
            k == InputKey.Menu || k == InputKey.Chat || k == InputKey.Inventory1 || k == InputKey.Inventory2 ||
            k == InputKey.Inventory3 || k == InputKey.SpectateNext || k == InputKey.SpectatePrevious;

        void Move(int id, Vector2 pos)
        {
            if (id == m_StickFinger) m_StickPos = pos;
            if (id == m_LookFinger)
            {
                Vector2 d = pos - m_LookLast;
                m_LookLast = pos;
                float dpi = Screen.dpi > 0 ? Screen.dpi : 460f;
                VirtualInput.PointerDelta += d * (LookScale * 1.6f * 460f / dpi);
            }
        }

        void Release(int id)
        {
            foreach (var b in m_Buttons) if (b.Finger == id) b.Finger = -1;
            if (id == m_StickFinger) m_StickFinger = -1;
            if (id == m_LookFinger) m_LookFinger = -1;
        }

        // ---- menus: finger = mouse ------------------------------------------------------------------
        void Menu()
        {
            bool pointerSeen = false, scrollSeen = false;
            for (int i = 0; i < Input.touchCount; i++)
            {
                var t = Input.GetTouch(i);
                bool ended = t.phase == UnityEngine.TouchPhase.Ended || t.phase == UnityEngine.TouchPhase.Canceled;
                if (t.phase == UnityEngine.TouchPhase.Began)
                {
                    if (DebugOverlay.HandleTouch(t.position)) continue;
                    if (m_PointerFinger < 0)
                    {
                        m_PointerFinger = t.fingerId;
                        m_PointerFrames = 0;
                        m_PointerPos = t.position;
                    }
                    else if (m_ScrollFinger < 0)
                    {
                        m_ScrollFinger = t.fingerId;
                        m_ScrollLast = t.position;
                    }
                }
                if (t.fingerId == m_PointerFinger)
                {
                    VirtualInput.PointerDelta += t.position - m_PointerPos;
                    m_PointerPos = t.position;
                    if (ended)
                    {
                        // a quick tap still gets its hover frame plus two pressed frames
                        if (m_PointerFrames < 3) m_ClickHold = 3 - m_PointerFrames;
                        m_PointerFinger = -1;
                    }
                    else pointerSeen = true;
                }
                else if (t.fingerId == m_ScrollFinger)
                {
                    if (ended) { m_ScrollFinger = -1; continue; }
                    scrollSeen = true;
                    m_ScrollAccum += (t.position.y - m_ScrollLast.y) / (Screen.height * 0.04f);
                    m_ScrollLast = t.position;
                }
            }
            if (!pointerSeen) m_PointerFinger = -1;
            if (!scrollSeen) m_ScrollFinger = -1;
            int notches = (int)m_ScrollAccum;
            if (notches != 0)
            {
                VirtualInput.Scroll += notches;
                m_ScrollAccum -= notches;
            }
            VirtualInput.PointerPos = m_PointerPos;
            if (m_PointerFinger >= 0)
            {
                // first frame only moves the pointer so the game registers hover before the press
                if (m_PointerFrames++ >= 1) VirtualInput.LeftButton = true;
            }
            else if (m_ClickHold > 0)
            {
                m_ClickHold--;
                VirtualInput.LeftButton = true;
            }
        }

        // ---- text entry: iOS keyboard ----------------------------------------------------------------
        void Text()
        {
            if (m_Keyboard == null || !m_Keyboard.active && m_Keyboard.status == TouchScreenKeyboard.Status.Visible)
            {
                TouchScreenKeyboard.hideInput = false;
                m_Keyboard = TouchScreenKeyboard.Open("", TouchScreenKeyboardType.Default, false, false, false, false, "");
                m_KbLast = "";
                m_TextOps.Clear();
                Log.Info("iOS keyboard opened");
            }
            string now = m_Keyboard.text ?? "";
            if (now != m_KbLast)
            {
                int common = 0;
                while (common < now.Length && common < m_KbLast.Length && now[common] == m_KbLast[common]) common++;
                for (int i = common; i < m_KbLast.Length; i++) m_TextOps.Enqueue("\b");
                if (common < now.Length) m_TextOps.Enqueue(now.Substring(common));
                m_KbLast = now;
            }
            if (m_BackspaceFrames > 0)
            {
                m_BackspaceFrames--;   // release frame between two backspaces
            }
            else if (m_TextOps.Count > 0)
            {
                string op = m_TextOps.Dequeue();
                if (op == "\b")
                {
                    // the chat deletes on the ChatDelete key, menu text fields on a "\b" in inputString
                    VirtualInput.Hold(InputKey.ChatDelete);
                    VirtualInput.Text += "\b";
                    m_BackspaceFrames = 1;
                }
                else VirtualInput.Text += op;
            }
            else if (m_Keyboard.status == TouchScreenKeyboard.Status.Done)
            {
                Tap(InputKey.Confirm);
                m_Keyboard = null;
            }
            else if (m_Keyboard.status == TouchScreenKeyboard.Status.Canceled || m_Keyboard.status == TouchScreenKeyboard.Status.LostFocus)
            {
                Tap(InputKey.Back);
                m_Keyboard = null;
            }
            // menus with text fields still need the pointer
            Menu();
        }

        void CloseKeyboard()
        {
            if (m_Keyboard == null) return;
            try { m_Keyboard.active = false; } catch { }
            m_Keyboard = null;
            m_TextOps.Clear();
        }

        // ---- settings panel ---------------------------------------------------------------------------
        bool SettingsTouch(Vector2 pos)
        {
            var gp = new Vector2(pos.x, Screen.height - pos.y);
            if (!m_SettingsRect.Contains(gp)) return false;
            float rowH = m_SettingsRect.height / 5f;
            int row = (int)((gp.y - m_SettingsRect.y) / rowH);
            bool right = gp.x > m_SettingsRect.center.x;
            switch (row)
            {
                case 0: LookScale = Mathf.Clamp(LookScale * (right ? 1.15f : 1f / 1.15f), 0.2f, 5f); break;
                case 1: Opacity = Mathf.Clamp(Opacity + (right ? 0.15f : -0.15f), 0.25f, 1.6f); break;
                case 2: EdgeSprint = !EdgeSprint; break;
                case 3: GrabToggle = !GrabToggle; m_Grab.Latched = false; break;
                case 4: Platform.UserFpsCap = Platform.UserFpsCap == 60 ? 0 : Platform.UserFpsCap == 0 ? 30 : 60; Platform.ApplyFrameRate(); break;
            }
            SaveSettings();
            return true;
        }

        static void LoadSettings()
        {
            LookScale = PlayerPrefs.GetFloat("ios_look", 1f);
            Opacity = PlayerPrefs.GetFloat("ios_opacity", 1f);
            EdgeSprint = PlayerPrefs.GetInt("ios_edgesprint", 1) == 1;
            GrabToggle = PlayerPrefs.GetInt("ios_grabtoggle", 0) == 1;
            Hidden = PlayerPrefs.GetInt("ios_hidden", 0) == 1;
            Platform.UserFpsCap = PlayerPrefs.GetInt("ios_fpscap", 60);
        }

        static void SaveSettings()
        {
            PlayerPrefs.SetFloat("ios_look", LookScale);
            PlayerPrefs.SetFloat("ios_opacity", Opacity);
            PlayerPrefs.SetInt("ios_edgesprint", EdgeSprint ? 1 : 0);
            PlayerPrefs.SetInt("ios_grabtoggle", GrabToggle ? 1 : 0);
            PlayerPrefs.SetInt("ios_hidden", Hidden ? 1 : 0);
            PlayerPrefs.SetInt("ios_fpscap", Platform.UserFpsCap);
            PlayerPrefs.Save();
        }

        // ---- drawing ------------------------------------------------------------------------------
        void OnGUI()
        {
            if (Event.current.type != EventType.Repaint) return;
            if (m_Mode != Mode.Gameplay) return;
            if (m_Label == null)
            {
                m_Label = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleCenter, fontStyle = FontStyle.Bold };
                m_Small = new GUIStyle(GUI.skin.label) { alignment = TextAnchor.MiddleLeft, fontStyle = FontStyle.Bold };
            }
            float h = Screen.height;
            var prev = GUI.color;
            float a = Opacity;
            foreach (var b in m_Buttons)
            {
                if (!b.Shown) continue;
                m_Label.fontSize = Mathf.RoundToInt(Mathf.Min(h * 0.026f, b.Radius * 0.55f));
                var r = new Rect(b.Center.x - b.Radius, h - b.Center.y - b.Radius, b.Radius * 2, b.Radius * 2);
                bool down = b.Down || (b == m_Gear && m_SettingsOpen) || (b == m_Emote && m_EmotesOpen) || (b == m_Hide && Hidden);
                GUI.color = down ? new Color(1f, 0.55f, 0.1f, 0.55f * a) : new Color(0f, 0f, 0f, 0.28f * a);
                GUI.DrawTexture(r, m_Circle);
                GUI.color = new Color(1f, 1f, 1f, 0.5f * a);
                GUI.DrawTexture(r, m_Ring);
                GUI.color = new Color(1f, 1f, 1f, 0.9f * Mathf.Min(1f, a));
                GUI.Label(r, b == m_Hide && Hidden ? "SHOW" : b.Label, m_Label);
            }
            if (m_StickFinger >= 0)
            {
                float rad = 0.12f * h;
                var o = new Rect(m_StickOrigin.x - rad, h - m_StickOrigin.y - rad, rad * 2, rad * 2);
                GUI.color = new Color(1f, 1f, 1f, 0.35f * a);
                GUI.DrawTexture(o, m_Ring);
                float k = rad * 0.45f;
                var kn = new Rect(m_StickPos.x - k, h - m_StickPos.y - k, k * 2, k * 2);
                GUI.color = m_EdgeTime > 0.12f && EdgeSprint ? new Color(1f, 0.55f, 0.1f, 0.6f * a) : new Color(1f, 1f, 1f, 0.45f * a);
                GUI.DrawTexture(kn, m_Circle);
            }
            if (m_SettingsOpen) DrawSettings();
            GUI.color = prev;
        }

        void DrawSettings()
        {
            float h = Screen.height, w = Screen.width;
            m_SettingsRect = new Rect(w * 0.5f - h * 0.45f, h * 0.2f, h * 0.9f, h * 0.55f);
            GUI.color = new Color(0, 0, 0, 0.8f);
            GUI.DrawTexture(m_SettingsRect, Texture2D.whiteTexture);
            GUI.color = Color.white;
            m_Small.fontSize = Mathf.RoundToInt(h * 0.03f);
            string[] rows =
            {
                $"Look sensitivity: {LookScale:0.00}    [ - | + ]",
                $"Button opacity: {Opacity:0.00}    [ - | + ]",
                $"Sprint when stick at edge: {(EdgeSprint ? "ON" : "OFF")}",
                $"Grab: {(GrabToggle ? "TAP TO TOGGLE" : "HOLD")}",
                $"Frame rate cap: {(Platform.UserFpsCap == 0 ? "display max" : Platform.UserFpsCap + " fps")}",
            };
            float rowH = m_SettingsRect.height / rows.Length;
            for (int i = 0; i < rows.Length; i++)
                GUI.Label(new Rect(m_SettingsRect.x + h * 0.04f, m_SettingsRect.y + i * rowH, m_SettingsRect.width - h * 0.08f, rowH), rows[i], m_Small);
        }

        static Texture2D MakeCircle(int size, bool ring)
        {
            var t = new Texture2D(size, size, TextureFormat.RGBA32, false);
            var px = new Color32[size * size];
            float c = (size - 1) * 0.5f;
            for (int y = 0; y < size; y++)
                for (int x = 0; x < size; x++)
                {
                    float d = Mathf.Sqrt((x - c) * (x - c) + (y - c) * (y - c)) / c;
                    float al = ring ? Mathf.Clamp01(1f - Mathf.Abs(d - 0.93f) * 25f) : Mathf.Clamp01((1f - d) * c * 0.5f);
                    px[y * size + x] = new Color32(255, 255, 255, (byte)(al * 255));
                }
            t.SetPixels32(px);
            t.Apply();
            return t;
        }
    }

    /// <summary>
    /// Real game controllers: the action bindings already include gamepad buttons (VirtualInput); this adds
    /// what bindings can't express: right stick = mouse look, shoulders = push/pull wheel while holding.
    /// </summary>
    static class ControllerBridge
    {
        static float s_Wheel;

        public static void Tick(bool gameplay)
        {
            foreach (var pad in Gamepad.all)
            {
                if (pad == VirtualInput.Pad || !pad.enabled) continue;
                Vector2 rs = pad.rightStick.ReadValue();
                if (rs.magnitude < 0.12f) rs = Vector2.zero;
                if (gameplay)
                {
                    // response curve: fine aim near the centre
                    VirtualInput.PointerDelta += rs * rs.magnitude * 1100f * TouchHud.LookScale * Time.unscaledDeltaTime;
                    float wheel = (pad.rightShoulder.isPressed ? 1f : 0f) - (pad.leftShoulder.isPressed ? 1f : 0f);
                    s_Wheel += wheel * Time.unscaledDeltaTime * 16f;
                    int n = (int)s_Wheel;
                    if (n != 0) { VirtualInput.Scroll += n; s_Wheel -= n; }
                }
                else
                {
                    // menus: right stick scrolls lists
                    VirtualInput.Scroll += rs.y * Time.unscaledDeltaTime * 8f;
                }
            }
        }
    }
}
