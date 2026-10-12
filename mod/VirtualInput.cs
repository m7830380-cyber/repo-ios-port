using System;
using System.Collections.Generic;
using RepoShim;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.Controls;
using UnityEngine.InputSystem.LowLevel;

namespace RepoIOS
{
    /// <summary>
    /// The game reads gameplay input from Input System actions bound to the keyboard and mouse
    /// (InputManager), and menus/chat from the legacy Input API (patched to RepoShim.VInput). This class owns
    /// virtual Keyboard / Mouse / Gamepad devices and, once per frame right before the Input System
    /// processes events, pushes the state the touch layer (and real controllers) asked for into both.
    ///
    /// Requests are made by <see cref="InputKey"/>, and resolved to whatever key or button the game currently
    /// has bound to it, so in-game rebinding and the game's key prompts stay correct.
    /// </summary>
    public static class VirtualInput
    {
        public static Keyboard Kb { get; private set; }
        public static Mouse Mouse { get; private set; }
        public static Gamepad Pad { get; private set; }

        /// Called once per frame before the game sees input (fills the request state below).
        public static event Action Tick;

        // ---- requests for the coming frame (set by TouchHud / ControllerBridge in Tick) --------------
        static readonly HashSet<InputKey> s_Held = new HashSet<InputKey>();
        static readonly HashSet<Key> s_RawKeys = new HashSet<Key>();
        public static Vector2 Stick;               // analog movement, -1..1
        public static Vector2 PointerPos = new Vector2(-1000, -1000);   // screen pixels, bottom-left origin
        public static Vector2 PointerDelta;        // pointer pixels this frame (look / rotate)
        public static float Scroll;                // wheel "notches" this frame (+ = push / up)
        public static bool LeftButton, RightButton; // raw mouse buttons (menus); actions bound to the mouse use Hold()
        public static string Text = "";             // typed text for the legacy inputString this frame

        public static void Hold(InputKey k) => s_Held.Add(k);
        public static void HoldKey(Key k) => s_RawKeys.Add(k);
        public static bool IsHeld(InputKey k) => s_Held.Contains(k);

        static int s_LastFrame = -1;
        static bool s_BindingsDone;
        static int s_Errors;

        public static void Init()
        {
            try
            {
                Kb = InputSystem.AddDevice<Keyboard>("iOSTouchKeyboard");
                Mouse = InputSystem.AddDevice<Mouse>("iOSTouchMouse");
                Pad = InputSystem.AddDevice<Gamepad>("iOSTouchGamepad");
                InputSystem.onBeforeUpdate += OnBeforeUpdate;
                Log.Info($"virtual input devices added; input update mode={InputSystem.settings.updateMode}, " +
                         $"background={InputSystem.settings.backgroundBehavior}");
            }
            catch (Exception e)
            {
                Log.Error("virtual devices: " + e);
            }
        }

        static void OnBeforeUpdate()
        {
            var t = InputState.currentUpdateType;
            if (t != InputUpdateType.Dynamic && t != InputUpdateType.Fixed) return;
            if (Time.frameCount == s_LastFrame) return;
            s_LastFrame = Time.frameCount;
            try
            {
                if (!s_BindingsDone) SetupBindings();
                s_Held.Clear();
                s_RawKeys.Clear();
                Stick = Vector2.zero;
                PointerDelta = Vector2.zero;
                Scroll = 0;
                LeftButton = RightButton = false;
                Text = "";
                Tick?.Invoke();
                Push();
            }
            catch (Exception e)
            {
                if (s_Errors++ < 20) Log.Error("virtual input tick: " + e);
            }
            finally
            {
                VInput.BeginFrame();
            }
        }

        // ---------------------------------------------------------------------------------------------
        // Bindings: the game binds look/cursor to "<Pointer>/..." which also matches the real touchscreen
        // (every finger on a HUD button would turn the camera), so those two point at the mouse. Gamepad
        // bindings are appended so a real MFi / Xbox / PlayStation controller works and the touch stick is
        // analog. Appending (never inserting) keeps the indices the game's saved key bindings use.
        static readonly (InputKey key, string path)[] PadBindings =
        {
            (InputKey.Movement, "<Gamepad>/leftStick"),
            (InputKey.Jump, "<Gamepad>/buttonSouth"),
            (InputKey.Confirm, "<Gamepad>/buttonSouth"),
            (InputKey.Interact, "<Gamepad>/buttonWest"),
            (InputKey.Crouch, "<Gamepad>/buttonEast"),
            (InputKey.Tumble, "<Gamepad>/buttonNorth"),
            (InputKey.Sprint, "<Gamepad>/leftStickPress"),
            (InputKey.Grab, "<Gamepad>/rightTrigger"),
            (InputKey.Rotate, "<Gamepad>/leftTrigger"),
            (InputKey.Map, "<Gamepad>/select"),
            (InputKey.Menu, "<Gamepad>/start"),
            (InputKey.Back, "<Gamepad>/start"),
            (InputKey.Inventory1, "<Gamepad>/dpad/left"),
            (InputKey.Inventory2, "<Gamepad>/dpad/up"),
            (InputKey.Inventory3, "<Gamepad>/dpad/right"),
            (InputKey.Chat, "<Gamepad>/dpad/down"),
            (InputKey.SpectateNext, "<Gamepad>/rightShoulder"),
            (InputKey.SpectatePrevious, "<Gamepad>/leftShoulder"),
        };

        static void SetupBindings()
        {
            var im = InputManager.instance;
            if (im == null) return;
            s_BindingsDone = true;
            int n = 0;
            foreach (var (key, path) in PadBindings)
            {
                var a = im.GetAction(key);
                if (a == null) continue;
                bool has = false;
                foreach (var b in a.bindings) if (b.path == path) has = true;
                if (has) continue;
                bool was = a.enabled;
                a.Disable();
                a.AddBinding(path);
                if (was) a.Enable();
                n++;
            }
            n += Repoint(im, InputKey.MouseDelta, "<Pointer>/delta", "<Mouse>/delta");
            n += Repoint(im, InputKey.MouseInput, "<Pointer>/position", "<Mouse>/position");
            Log.Info($"input bindings adjusted ({n} changes)");
        }

        static int Repoint(InputManager im, InputKey key, string from, string to)
        {
            var a = im.GetAction(key);
            if (a == null) return 0;
            for (int i = 0; i < a.bindings.Count; i++)
                if (a.bindings[i].path == from)
                {
                    a.ApplyBindingOverride(i, to);
                    return 1;
                }
            return 0;
        }

        // ---------------------------------------------------------------------------------------------
        static readonly List<Key> s_Keys = new List<Key>();

        static void Push()
        {
            if (Kb == null) return;
            var im = InputManager.instance;
            var ms = new MouseState { position = PointerPos, delta = PointerDelta, scroll = new Vector2(0, Scroll * 120f) };
            bool left = LeftButton, right = RightButton;
            s_Keys.Clear();
            foreach (var k in s_RawKeys) s_Keys.Add(k);
            foreach (var k in s_Held)
            {
                Resolve(im, k, out Key key, out int mouse);
                if (key != Key.None) s_Keys.Add(key);
                if (mouse == 0) left = true;
                if (mouse == 1) right = true;
            }
            if (left) ms = ms.WithButton(MouseButton.Left, true);
            if (right) ms = ms.WithButton(MouseButton.Right, true);
            InputSystem.QueueStateEvent(Kb, new KeyboardState(s_Keys.ToArray()));
            InputSystem.QueueStateEvent(Mouse, ms);
            InputSystem.QueueStateEvent(Pad, new GamepadState { leftStick = Stick });

            // legacy API (menus, chat typing, object rotation): same state
            VInput.VMousePosition = PointerPos;
            VInput.VMouseDelta += PointerDelta;
            VInput.VScroll += new Vector2(0, Scroll);
            VInput.SetMouseButton(0, left);
            VInput.SetMouseButton(1, right);
            VInput.VInputString += Text;
            foreach (var kc in s_LegacyKeys) VInput.SetKey(kc, false);
            s_LegacyKeys.Clear();
            foreach (var k in s_Keys)
            {
                var kc = ToKeyCode(k);
                if (kc == KeyCode.None) continue;
                VInput.SetKey(kc, true);
                s_LegacyKeys.Add(kc);
            }
        }

        static readonly List<KeyCode> s_LegacyKeys = new List<KeyCode>();

        static readonly Dictionary<InputKey, (Key key, int mouse)> s_Resolved = new Dictionary<InputKey, (Key, int)>();
        static int s_ResolveFrame;

        // Key / mouse button the game currently has bound to an action (first keyboard or mouse binding).
        static void Resolve(InputManager im, InputKey k, out Key key, out int mouse)
        {
            if (s_ResolveFrame != Time.frameCount / 30)
            {
                s_ResolveFrame = Time.frameCount / 30;   // refresh twice a second (rebinding in the menu)
                s_Resolved.Clear();
            }
            if (s_Resolved.TryGetValue(k, out var r)) { key = r.key; mouse = r.mouse; return; }
            key = Key.None;
            mouse = -1;
            var a = im != null ? im.GetAction(k) : null;
            if (a != null)
                foreach (var b in a.bindings)
                {
                    string path = b.effectivePath;
                    if (string.IsNullOrEmpty(path) || b.isComposite || b.isPartOfComposite) continue;
                    if (path.StartsWith("<Keyboard>/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (InputControlPath.TryFindControl(Kb, path) is KeyControl kc) { key = kc.keyCode; break; }
                    }
                    else if (path.StartsWith("<Mouse>/", StringComparison.OrdinalIgnoreCase))
                    {
                        if (path.EndsWith("leftButton")) { mouse = 0; break; }
                        if (path.EndsWith("rightButton")) { mouse = 1; break; }
                    }
                }
            s_Resolved[k] = (key, mouse);
        }

        static KeyCode ToKeyCode(Key k)
        {
            switch (k)
            {
                case Key.Enter: return KeyCode.Return;
                case Key.Escape: return KeyCode.Escape;
                case Key.Backspace: return KeyCode.Backspace;
                case Key.Space: return KeyCode.Space;
                case Key.Tab: return KeyCode.Tab;
                case Key.LeftShift: return KeyCode.LeftShift;
                case Key.LeftCtrl: return KeyCode.LeftControl;
                case Key.UpArrow: return KeyCode.UpArrow;
                case Key.DownArrow: return KeyCode.DownArrow;
            }
            if (k >= Key.A && k <= Key.Z) return KeyCode.A + (k - Key.A);
            if (k >= Key.Digit1 && k <= Key.Digit9) return KeyCode.Alpha1 + (k - Key.Digit1);
            if (k == Key.Digit0) return KeyCode.Alpha0;
            return KeyCode.None;
        }
    }
}
