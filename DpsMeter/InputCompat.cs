using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using UnityEngine;

namespace TbhDpsMeter
{
    /// <summary>
    /// OS-level input via Win32. This game's Unity input (both legacy UnityEngine.Input
    /// and the new Input System) does not deliver live mouse data to plugin code:
    /// Mouse.current.position is frozen and IMGUI events never fire. So we read the
    /// cursor and buttons straight from user32 and map desktop coords into the game
    /// window's client area, which equals IMGUI/GUI coordinates (top-left origin).
    /// Call Poll() once per frame; the accessors return the snapshot/edges.
    /// </summary>
    internal static class InputCompat
    {
        [StructLayout(LayoutKind.Sequential)] private struct POINT { public int X; public int Y; }
        [StructLayout(LayoutKind.Sequential)] private struct RECT { public int Left, Top, Right, Bottom; }
        [DllImport("user32.dll")] private static extern bool GetCursorPos(out POINT p);
        [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
        [DllImport("user32.dll")] private static extern IntPtr GetActiveWindow();
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr FindWindow(string lpClassName, string lpWindowName);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);
        [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
        [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc lpEnumFunc, IntPtr lParam);
        [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hWnd);
        [DllImport("user32.dll")] private static extern bool ScreenToClient(IntPtr hWnd, ref POINT p);
        [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] private static extern short GetAsyncKeyState(int vKey);
        private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

        // Low-level mouse hook: lets us SWALLOW a left-click when the cursor is over one of our
        // panels, so the click no longer falls through to the game (or, in windowed mode, the
        // desktop / other apps). Polling can detect a click but cannot consume it; only a hook can.
        [StructLayout(LayoutKind.Sequential)]
        private struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public IntPtr dwExtraInfo; }
        private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr SetWindowsHookEx(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);
        [DllImport("user32.dll", SetLastError = true)] private static extern bool UnhookWindowsHookEx(IntPtr hhk);
        [DllImport("user32.dll", SetLastError = true)] private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)] private static extern IntPtr GetModuleHandle(string lpModuleName);

        // Message pump for the dedicated hook thread (WH_MOUSE_LL delivers callbacks via the installing
        // thread's message queue — running it off the Unity main thread is what keeps high-polling-rate
        // mice from stuttering the game).
        [StructLayout(LayoutKind.Sequential)] private struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }
        [DllImport("user32.dll")] private static extern int GetMessage(out MSG lpMsg, IntPtr hWnd, uint min, uint max);
        [DllImport("user32.dll")] private static extern bool TranslateMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern IntPtr DispatchMessage(ref MSG lpMsg);
        [DllImport("user32.dll")] private static extern bool PostThreadMessage(uint idThread, uint Msg, IntPtr wParam, IntPtr lParam);
        [DllImport("kernel32.dll")] private static extern uint GetCurrentThreadId();
        private const uint WM_QUIT = 0x0012;
        // Thread messages (WM_APP+) that tell the hook thread to install/remove the hook. The hook
        // only exists while the game is foreground — a WH_MOUSE_LL hook makes EVERY system mouse
        // event round-trip into this process, so leaving it installed while the game idles in the
        // background adds system-wide cursor latency (background process priority + GC pauses stall
        // the hook thread, and the whole desktop's mouse waits on it).
        private const uint MSG_HOOK_INSTALL = 0x8001, MSG_HOOK_REMOVE = 0x8002;

        private const int WH_MOUSE_LL = 14;
        private const int HC_ACTION = 0;
        private const int WM_LBUTTONDOWN = 0x0201, WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
        private const int WM_MOUSEWHEEL = 0x020A;

        private const string UnityWindowClass = "UnityWndClass";

        private const int VK_LBUTTON = 0x01;
        private const int VK_F9 = 0x78;
        private const int VK_PRIOR = 0x21;  // PageUp
        private const int VK_NEXT = 0x22;   // PageDown

        private static int _cw, _ch, _rawX, _rawY;
        private static float _sx = 1f, _sy = 1f;
        private static IntPtr _window;
        private static string _windowSource = "none";

        // Snapshot of Unity/window state the hook thread may read (it must NOT touch Unity APIs like
        // Screen.* or mutate _window). Refreshed every frame by Poll() on the main thread.
        private static volatile int _scrW = 1, _scrH = 1;
        private static IntPtr _hookGameHwnd;

        private static Vector2 _pos;
        private static bool _down, _pressed, _released;
        private static bool _lb, _f9, _pgUp, _pgDn;            // previous key states
        private static int _seenDownSeq, _seenUpSeq;           // last hook edge counts consumed by Poll
        private static bool _f9Edge, _pgUpEdge, _pgDnEdge;
        private static bool _fg;   // game window is foreground (refreshed each Poll; gates global input)

        private static bool Key(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        // Panel rectangles (GUI coords) the hook should treat as solid. One slot per overlay;
        // each overlay re-registers its rect + visibility every frame from Update().
        private const int MaxPanels = 11;  // slots 0-7 = overlays; slot 8 = price-box (drag/adjust mode); slot 9 = gear-score; slot 10 = item-stats
        private static readonly Rect[] _panelRect = new Rect[MaxPanels];
        private static readonly bool[] _panelOn = new bool[MaxPanels];

        /// <summary>Register an overlay's panel so left-clicks over it are swallowed (not passed
        /// through). slot is a stable per-overlay id (0 DPS / 1 taken / 2 compare).</summary>
        public static void SetPanel(int slot, bool visible, Rect rectGui)
        {
            if (slot < 0 || slot >= MaxPanels) return;
            _panelOn[slot] = visible;
            _panelRect[slot] = rectGui;
        }

        // Drag arbitration: when panels overlap, only one may grab a drag. The id doubles as the
        // z-order (panels are drawn in id order, so a higher id is on top and wins the press).
        private static int _dragOwner = -1, _dragFrame = -1;

        /// <summary>Request drag ownership for this panel on the current press. Returns true only
        /// for the topmost (highest-id) panel that asks this frame; a later higher-id caller steals
        /// it, so lower panels must re-check OwnsDrag() before continuing a drag.</summary>
        public static bool ClaimDrag(int id)
        {
            if (_pressed && _dragFrame != _polledFrame) { _dragFrame = _polledFrame; _dragOwner = -1; }
            if (_dragOwner == id) return true;
            if (_dragOwner == -1 || id > _dragOwner) { _dragOwner = id; return true; }
            return false;
        }

        public static bool OwnsDrag(int id) => _dragOwner == id;
        public static void ReleaseDrag(int id) { if (_dragOwner == id) _dragOwner = -1; }

        private static IntPtr _hookHandle = IntPtr.Zero;
        private static HookProc _hookProc;   // keep a managed ref alive so the GC can't collect the thunk
        private static bool _hookTried;
        private static Thread _hookThread;
        private static uint _hookThreadId;
        // Main-thread-readable mirror of "hook is live" (_hookHandle belongs to the hook thread).
        private static volatile bool _hookInstalled;
        private static bool _hookLoggedOnce;
        private static float _lostFocusAt = -1f;   // when the game last left the foreground (-1 = focused)
        private const float UnhookGraceSecs = 0.5f;  // brief alt-tabs don't churn install/uninstall

        private static void EnsureMouseHook()
        {
            if (_hookTried) return;
            _hookTried = true;
            try
            {
                // Pump the WH_MOUSE_LL hook on its OWN thread. A low-level hook is dispatched
                // through the installing thread's message queue and BLOCKS system-wide mouse delivery
                // until the callback returns; on the Unity main thread that means high-Hz mice (500–
                // 1000Hz) pile up behind the frame loop and stutter. A dedicated pump processes each
                // event in microseconds, decoupled from rendering. The hook itself is installed and
                // removed on demand (MSG_HOOK_INSTALL/REMOVE from Poll) so it only exists while the
                // game is the foreground window.
                _hookProc = MouseHookCallback;
                _hookThread = new Thread(HookThreadProc) { IsBackground = true, Name = "TbhMouseHook" };
                _hookThread.Start();
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("mouse hook ex: " + e.Message); }
        }

        private static void HookThreadProc()
        {
            try
            {
                _hookThreadId = GetCurrentThreadId();
                // Above-normal priority so a busy foreground app can't starve the pump while the
                // game (and this thread) sit in a deprioritized background process.
                try { Thread.CurrentThread.Priority = System.Threading.ThreadPriority.Highest; } catch { }
                // pump until WM_QUIT (posted by UninstallMouseHook); install/remove arrive as
                // thread messages from Poll() on the main thread
                while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
                {
                    if (msg.message == MSG_HOOK_INSTALL && _hookHandle == IntPtr.Zero)
                    {
                        _hookHandle = SetWindowsHookEx(WH_MOUSE_LL, _hookProc, GetModuleHandle(null), 0);
                        _hookInstalled = _hookHandle != IntPtr.Zero;
                        if (_hookInstalled) _hookLbDown = false;   // forget any stale press from before
                        if (_hookLoggedOnce && _hookInstalled && Plugin.DebugDamage != null && Plugin.DebugDamage.Value)
                            Plugin.Logger?.LogInfo("[hook] reinstalled (game foreground)");
                        if (!_hookLoggedOnce)
                        {
                            _hookLoggedOnce = true;
                            Plugin.Logger?.LogInfo(_hookInstalled
                                ? "Mouse click-through guard active (WH_MOUSE_LL, foreground-only)."
                                : "WH_MOUSE_LL install failed; panels will still pass clicks through.");
                        }
                    }
                    else if (msg.message == MSG_HOOK_REMOVE && _hookHandle != IntPtr.Zero)
                    {
                        UnhookWindowsHookEx(_hookHandle);
                        _hookHandle = IntPtr.Zero;
                        _hookInstalled = false;
                        if (Plugin.DebugDamage != null && Plugin.DebugDamage.Value)
                            Plugin.Logger?.LogInfo("[hook] removed (game left foreground)");
                    }
                    else { TranslateMessage(ref msg); DispatchMessage(ref msg); }
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[hook] thread ex: " + e.Message); }
            finally
            {
                try { if (_hookHandle != IntPtr.Zero) { UnhookWindowsHookEx(_hookHandle); _hookHandle = IntPtr.Zero; _hookInstalled = false; } }
                catch { }
            }
        }

        /// <summary>Install the hook only while the game is foreground; remove it (after a short grace
        /// period) when the game goes to the background, so an idle/backgrounded game never adds
        /// latency to the rest of the desktop's mouse. Posts are retried every frame until the state
        /// matches, which also covers the race where Poll posts before the thread's queue exists.</summary>
        private static void UpdateHookLifecycle(IntPtr gameHwnd)
        {
            if (_hookThreadId == 0) return;
            bool foreground = gameHwnd != IntPtr.Zero && GetForegroundWindow() == gameHwnd;
            bool want;
            if (foreground) { _lostFocusAt = -1f; want = true; }
            else
            {
                if (_lostFocusAt < 0f) _lostFocusAt = Time.unscaledTime;
                want = Time.unscaledTime - _lostFocusAt < UnhookGraceSecs;
            }
            if (want != _hookInstalled)
                PostThreadMessage(_hookThreadId, want ? MSG_HOOK_INSTALL : MSG_HOOK_REMOVE, IntPtr.Zero, IntPtr.Zero);
        }

        /// <summary>Remove the hook (call on shutdown so we don't leave a global hook dangling).</summary>
        public static void UninstallMouseHook()
        {
            try { if (_hookThreadId != 0) PostThreadMessage(_hookThreadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero); }
            catch { }
        }

        // Swallow left-clicks that land on a visible panel so they don't fall through to the game
        // or desktop. Set false to make the hook log-only (diagnostics).
        private static bool _swallowEnabled = true;

        // Left-button state sourced FROM the hook. Swallowing a click stops GetAsyncKeyState from
        // reporting it, so once the hook is installed it becomes our source of truth for the button.
        // Sequence counters latch press/release edges so a down+up inside one frame isn't missed.
        private static volatile bool _hookLbDown;
        private static volatile int _hookDownSeq, _hookUpSeq;
        // Middle-button press edge (the game doesn't use middle-click; the price overlay uses it to pin an
        // item). We only record the edge — never swallow it — so the game is unaffected. WM_MBUTTONDOWN is
        // the middle-button CLICK and is distinct from WM_MOUSEWHEEL (the wheel scroll), so panel scrolling
        // is unaffected.
        private static volatile int _hookMbDownSeq;
        private static int _seenMbDownSeq;
        private static bool _mbPressed, _mb;
        private const int WM_MBUTTONDOWN = 0x0207;
        private const int VK_MBUTTON = 0x04;

        // Mouse wheel sourced from the hook (Unity input is frozen): accumulated notches (WHEEL_DELTA=120
        // each) + the panel slot the cursor was over. Read & reset on the main thread in Poll().
        private static volatile int _hookWheelSlot = -1;
        private static int _hookWheelAccum;
        private static int _wheel, _wheelSlot = -1;

        private static IntPtr MouseHookCallback(int nCode, IntPtr wParam, IntPtr lParam)
        {
            try
            {
                if (nCode == HC_ACTION)
                {
                    int msg = wParam.ToInt32();
                    // CRITICAL: this is a SYSTEM-WIDE hook. Only act on clicks when the GAME is the
                    // foreground window — otherwise we'd swallow clicks meant for other apps (e.g. a
                    // browser) just because they land where a panel sits in game coords.
                    if ((msg == WM_LBUTTONDOWN || msg == WM_LBUTTONUP || msg == WM_LBUTTONDBLCLK) && GameIsForeground())
                    {
                        if (msg == WM_LBUTTONUP) { _hookLbDown = false; _hookUpSeq++; }
                        else { _hookLbDown = true; _hookDownSeq++; }

                        var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (OverAnyPanel(data.pt.X, data.pt.Y, out var g, out int slot, out _))
                        {
                            if (Plugin.DebugDamage != null && Plugin.DebugDamage.Value)
                                Plugin.Logger?.LogInfo($"[hook] {(msg == WM_LBUTTONUP ? "up" : "down")} gui=({g.x:0},{g.y:0}) slot={slot} swallow={_swallowEnabled}");
                            if (_swallowEnabled) return (IntPtr)1;   // don't forward to the game or desktop
                        }
                    }
                    if (msg == WM_MBUTTONDOWN && GameIsForeground()) _hookMbDownSeq++;   // edge only, never swallowed
                    if (msg == WM_MOUSEWHEEL && GameIsForeground())
                    {
                        var wd = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                        if (OverAnyPanel(wd.pt.X, wd.pt.Y, out _, out int wslot, out _))
                        {
                            _hookWheelSlot = wslot;
                            System.Threading.Interlocked.Add(ref _hookWheelAccum, (short)((wd.mouseData >> 16) & 0xffff));
                            if (_swallowEnabled) return (IntPtr)1;   // scroll our panel, not the game
                        }
                    }
                }
            }
            catch (Exception e) { Plugin.Logger?.LogWarning("[hook] cb ex: " + e.Message); }
            return CallNextHookEx(_hookHandle, nCode, wParam, lParam);
        }

        private static bool OverAnyPanel(int screenX, int screenY, out Vector2 gui, out int slot, out Rect rect)
        {
            slot = -1; rect = default;
            if (!ScreenToGuiPoint(screenX, screenY, out gui)) return false;
            for (int i = 0; i < MaxPanels; i++)
                if (_panelOn[i] && _panelRect[i].Contains(gui)) { slot = i; rect = _panelRect[i]; return true; }
            return false;
        }

        /// <summary>Map a desktop point to IMGUI/GUI coords (same basis as the cursor in Poll()).</summary>
        private static bool ScreenToGuiPoint(int screenX, int screenY, out Vector2 gui)
        {
            // Hook-thread safe: cached handle + cached screen size (no Unity API, no _window mutation).
            gui = default;
            IntPtr h = _hookGameHwnd;
            if (h == IntPtr.Zero) return false;
            var p = new POINT { X = screenX, Y = screenY };
            ScreenToClient(h, ref p);
            int cw = _cw, ch = _ch;
            if (GetClientRect(h, out var rc)) { cw = rc.Right - rc.Left; ch = rc.Bottom - rc.Top; }
            float fx = cw > 0 ? (float)_scrW / cw : 1f;
            float fy = ch > 0 ? (float)_scrH / ch : 1f;
            gui = new Vector2(p.X * fx, p.Y * fy);
            return true;
        }

        private static int _polledFrame = -1;
        public static void Poll()
        {
            // run at most once per frame even if several components call it
            int frame = Time.frameCount;
            if (frame == _polledFrame) return;
            _polledFrame = frame;
            EnsureMouseHook();
            try
            {
                if (GetCursorPos(out var p))
                {
                    _rawX = p.X; _rawY = p.Y;
                    IntPtr h = ResolveGameWindow();
                    var cp = p;
                    if (h != IntPtr.Zero)
                    {
                        ScreenToClient(h, ref cp);
                        if (GetClientRect(h, out var rc))
                        {
                            _cw = rc.Right - rc.Left;
                            _ch = rc.Bottom - rc.Top;
                        }
                    }
                    // Scale physical client pixels -> Unity GUI (logical) pixels so the
                    // cursor lines up with the IMGUI rects regardless of DPI/resolution.
                    _sx = _cw > 0 ? (float)Screen.width / _cw : 1f;
                    _sy = _ch > 0 ? (float)Screen.height / _ch : 1f;
                    _pos = new Vector2(cp.X * _sx, cp.Y * _sy);
                    // hand the hook thread a fresh snapshot (it can't read Unity APIs itself)
                    _hookGameHwnd = h; _scrW = Screen.width; _scrH = Screen.height;
                    UpdateHookLifecycle(h);
                }

                if (_hookInstalled)
                {
                    // Hook is the source of truth (GetAsyncKeyState goes blind once we swallow).
                    // Latch edges via the sequence counters so single-frame clicks aren't lost.
                    _pressed = _hookDownSeq != _seenDownSeq; _seenDownSeq = _hookDownSeq;
                    _released = _hookUpSeq != _seenUpSeq; _seenUpSeq = _hookUpSeq;
                    _down = _hookLbDown; _lb = _down;
                    _mbPressed = _hookMbDownSeq != _seenMbDownSeq; _seenMbDownSeq = _hookMbDownSeq;
                }
                else
                {
                    bool lb = Key(VK_LBUTTON);
                    _pressed = lb && !_lb;
                    _released = !lb && _lb;
                    _down = lb; _lb = lb;
                    bool mb = Key(VK_MBUTTON); _mbPressed = mb && !_mb; _mb = mb;
                }

                bool f9 = Key(VK_F9); _f9Edge = f9 && !_f9; _f9 = f9;
                bool pu = Key(VK_PRIOR); _pgUpEdge = pu && !_pgUp; _pgUp = pu;
                bool pd = Key(VK_NEXT); _pgDnEdge = pd && !_pgDn; _pgDn = pd;

                _wheel = System.Threading.Interlocked.Exchange(ref _hookWheelAccum, 0);
                _wheelSlot = _hookWheelSlot;

                // GetAsyncKeyState/GetCursorPos are SYSTEM-GLOBAL: with the game in the background,
                // clicks and hotkeys typed into other apps would still register here (the game itself
                // never sees them — Windows only routes WM_* messages to the focused window). Track the
                // prev-states above as usual (so no stale edge fires on refocus) but suppress all edge
                // outputs while another window has focus. Held-state (_down) is kept for drag release.
                _fg = GameIsForeground();
                if (!_fg)
                {
                    _pressed = false; _mbPressed = false;
                    _f9Edge = false; _pgUpEdge = false; _pgDnEdge = false;
                    _wheel = 0;
                }
            }
            catch { }
        }

        /// <summary>True only when the game's own window is the foreground window. Gates the global
        /// mouse hook so it never swallows clicks destined for other applications.</summary>
        private static bool GameIsForeground()
        {
            // Runs on the hook thread: use the cached handle (set by Poll on the main thread), never
            // ResolveGameWindow() — that mutates _window and would race the main thread.
            try
            {
                IntPtr fg = GetForegroundWindow();
                IntPtr game = _hookGameHwnd;
                return game != IntPtr.Zero && fg == game;
            }
            catch { return false; }
        }

        private static IntPtr ResolveGameWindow()
        {
            if (_window != IntPtr.Zero && IsWindow(_window))
            {
                return _window;
            }

            _window = FindUnityWindowForCurrentProcess();
            if (_window != IntPtr.Zero)
            {
                _windowSource = "UnityWndClass/pid";
                return _window;
            }

            _window = FindWindow(UnityWindowClass, null);
            if (_window != IntPtr.Zero)
            {
                _windowSource = "UnityWndClass";
                return _window;
            }

            _window = GetActiveWindow();
            if (_window != IntPtr.Zero)
            {
                _windowSource = "active";
                return _window;
            }

            _window = GetForegroundWindow();
            _windowSource = _window != IntPtr.Zero ? "foreground" : "none";
            return _window;
        }

        private static IntPtr FindUnityWindowForCurrentProcess()
        {
            uint currentPid = (uint)Process.GetCurrentProcess().Id;
            IntPtr found = IntPtr.Zero;

            EnumWindows((hWnd, _) =>
            {
                GetWindowThreadProcessId(hWnd, out uint pid);
                if (pid != currentPid) return true;

                var className = new StringBuilder(128);
                if (GetClassName(hWnd, className, className.Capacity) <= 0) return true;
                if (!string.Equals(className.ToString(), UnityWindowClass, StringComparison.Ordinal)) return true;

                found = hWnd;
                return false;
            }, IntPtr.Zero);

            return found;
        }

        public static Vector2 MouseGuiPos() => _pos;
        public static bool MousePressed() => _pressed;
        public static bool MouseHeld() => _down;
        public static bool MouseReleased() => _released;
        /// <summary>Middle-button press edge this frame. The game ignores middle-click; the price overlay
        /// uses it to pin/unpin the hovered item. Edge only — never swallowed.</summary>
        public static bool MiddlePressed() => _mbPressed;
        /// <summary>Accumulated mouse-wheel delta this frame (WHEEL_DELTA=120 per notch, + = up) for the
        /// panel at <paramref name="slot"/>; 0 if the wheel wasn't over that panel.</summary>
        public static float WheelDelta(int slot) => _wheel != 0 && _wheelSlot == slot ? _wheel : 0f;
        public static bool TogglePressed() => _f9Edge;
        public static bool OpacityUpPressed() => _pgUpEdge;
        public static bool OpacityDownPressed() => _pgDnEdge;

        // General per-key edge detection for arbitrary hotkeys (e.g. the damage-taken
        // panel's F10). Call at most once per frame per key.
        private static readonly Dictionary<int, bool> _keyPrev = new Dictionary<int, bool>();
        public static bool TogglePressed(KeyCode key) => KeyPressed(key);
        public static bool KeyPressed(KeyCode key)
        {
            int vk = Vk(key);
            if (vk == 0) return false;
            bool down = Key(vk);
            _keyPrev.TryGetValue(vk, out bool prev);
            _keyPrev[vk] = down;
            // still track prev-state above so refocusing doesn't replay a stale press, but hotkeys typed
            // into OTHER apps must not toggle our panels (GetAsyncKeyState is system-global).
            return down && !prev && _fg;
        }

        /// <summary>True while either Ctrl key is held (used to qualify hotkeys like Ctrl+PageUp).</summary>
        public static bool CtrlHeld() => Key(0x11); // VK_CONTROL

        private static int Vk(KeyCode k)
        {
            if (k >= KeyCode.F1 && k <= KeyCode.F12) return 0x70 + (int)(k - KeyCode.F1); // F1=0x70
            switch (k)
            {
                case KeyCode.PageUp: return 0x21;
                case KeyCode.PageDown: return 0x22;
                case KeyCode.Home: return 0x24;
                case KeyCode.End: return 0x23;
                case KeyCode.Insert: return 0x2D;
                case KeyCode.Delete: return 0x2E;
                case KeyCode.Tab: return 0x09;
                case KeyCode.Backslash: return 0xDC;
            }
            if (k >= KeyCode.A && k <= KeyCode.Z) return 0x41 + (int)(k - KeyCode.A);
            if (k >= KeyCode.Alpha0 && k <= KeyCode.Alpha9) return 0x30 + (int)(k - KeyCode.Alpha0);
            return 0;
        }

        public static string Probe()
            => $"cursorGui={_pos} raw=({_rawX},{_rawY}) hwnd=0x{_window.ToInt64():X} source={_windowSource} client={_cw}x{_ch} screen={Screen.width}x{Screen.height} scale=({_sx:0.###},{_sy:0.###}) down={_down}";
    }
}
