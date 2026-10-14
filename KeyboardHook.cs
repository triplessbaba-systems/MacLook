using System.Runtime.InteropServices;
using System.Windows.Threading;

namespace MacLook;

/// <summary>
/// Global keyboard hook: a lone Windows key opens MacLook's Start menu, Win+A the Control Center,
/// and Alt+Tab the macOS-style app switcher. Runs on its own thread so a busy UI can never make
/// Windows drop the hook; UI work is posted to the dispatcher.
/// </summary>
internal static class KeyboardHook
{
    const int WH_KEYBOARD_LL = 13, WM_KEYDOWN = 0x100, WM_SYSKEYDOWN = 0x104, WM_QUIT = 0x12;
    const byte VK_SPACE = 0x20, VK_TAB = 0x09, VK_SHIFT = 0x10, VK_MENU = 0x12, VK_ESCAPE = 0x1B, VK_LEFT = 0x25, VK_RIGHT = 0x27;
    const byte VK_A = 0x41, VK_Q = 0x51, VK_LWIN = 0x5B, VK_RWIN = 0x5C, VK_LMENU = 0xA4, VK_RMENU = 0xA5;
    const byte VK_DUMMY = 0xE8; // unassigned key; makes Windows think the Win/Alt press was part of a shortcut

    static Native.LowLevelKeyboardProc? proc; // kept alive for the native side
    static IntPtr hook;
    static uint threadId;
    static Dispatcher ui = null!;
    static Config config = null!;
    // Read on every key so changes in MacLook Ayarları apply at once.
    static bool StartEnabled => config.WinKeyOpensStart;
    static bool SwitcherEnabled => config.MacAppSwitcher;
    static bool SpotlightEnabled => config.SpotlightHotkey;

    // State, only touched on the hook thread.
    static bool winDown, winCombo, altDown, switching, switchCancelled;

    public static void Start(Dispatcher dispatcher, Config config)
    {
        ui = dispatcher;
        KeyboardHook.config = config;

        var thread = new Thread(() =>
        {
            threadId = Native.GetCurrentThreadId();
            proc = Callback;
            hook = Native.SetWindowsHookEx(WH_KEYBOARD_LL, proc, Native.GetModuleHandle(null), 0);
            while (Native.GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                Native.TranslateMessage(ref msg);
                Native.DispatchMessage(ref msg);
            }
            Native.UnhookWindowsHookEx(hook);
        }) { IsBackground = true, Name = "MacLook keyboard hook" };
        thread.Start();
    }

    public static void Stop()
    {
        if (threadId != 0) Native.PostThreadMessage(threadId, WM_QUIT, IntPtr.Zero, IntPtr.Zero);
    }

    static IntPtr Callback(int code, IntPtr wParam, IntPtr lParam)
    {
        if (code >= 0)
        {
            var key = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
            if (key.dwExtraInfo != Native.InjectedMarker)
            {
                bool down = wParam == WM_KEYDOWN || wParam == WM_SYSKEYDOWN;
                try
                {
                    if (Handle((byte)key.vkCode, down)) return 1; // swallow
                }
                catch { }
            }
        }
        return Native.CallNextHookEx(hook, code, wParam, lParam);
    }

    /// <returns>true to swallow the key.</returns>
    static bool Handle(byte vk, bool down)
    {
        // ---- Windows key on its own -> Start menu ----
        if (vk is VK_LWIN or VK_RWIN)
        {
            if (down)
            {
                if (!winDown) { winDown = true; winCombo = false; }
                return false;
            }
            winDown = false;
            if (!StartEnabled || winCombo || switching) return false;
            // Replay the release after a dummy key so Windows sees a "shortcut" and keeps its Start closed.
            Native.Key(VK_DUMMY, true);
            Native.Key(VK_DUMMY, false);
            Native.Key(vk, false);
            ui.BeginInvoke(() => App.StartMenu.Toggle());
            return true;
        }
        if (winDown && down) winCombo = true;

        // ---- Win+A -> Control Center ----
        if (StartEnabled && winDown && vk == VK_A)
        {
            if (down)
            {
                // A is swallowed, so give Windows a dummy key instead; otherwise it sees a lone Win tap.
                Native.Key(VK_DUMMY, true);
                Native.Key(VK_DUMMY, false);
                ui.BeginInvoke(() => App.ControlCenter.Toggle(fromBar: true));
            }
            return true;
        }

        // ---- Win+Tab -> Mission Control (optional) ----
        if (config.WinTabMissionControl && winDown && vk == VK_TAB)
        {
            if (down)
            {
                Native.Key(VK_DUMMY, true);
                Native.Key(VK_DUMMY, false);
                ui.BeginInvoke(() => App.MissionControl.Toggle());
            }
            return true;
        }

        // ---- Alt+Space -> Spotlight (Alt sits where Cmd is on a Mac keyboard) ----
        if (vk == VK_SPACE && SpotlightEnabled && !switching && (Native.GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
        {
            if (down)
            {
                // Space is swallowed; a dummy key stops the app in front from treating this as a lone Alt press.
                Native.Key(VK_DUMMY, true);
                Native.Key(VK_DUMMY, false);
                ui.BeginInvoke(() => App.Spotlight.Toggle());
            }
            return true;
        }
        if (!SwitcherEnabled) return false;

        // ---- Alt+Tab -> app switcher ----
        if (vk is VK_LMENU or VK_RMENU)
        {
            if (down) { altDown = true; return false; }
            altDown = false;
            if (!switching) return false;
            switching = false;
            bool cancelled = switchCancelled;
            ui.BeginInvoke(() =>
            {
                // Activate while Windows still thinks Alt is held (it allows the focus change), then release it.
                if (cancelled) App.Switcher.Cancel();
                else App.Switcher.Commit();
                Native.Key(VK_DUMMY, true);
                Native.Key(VK_DUMMY, false);
                Native.Key(vk, false);
            });
            return true;
        }

        if (vk == VK_TAB && altDown && (Native.GetAsyncKeyState(VK_MENU) & 0x8000) != 0)
        {
            if (down)
            {
                bool back = (Native.GetAsyncKeyState(VK_SHIFT) & 0x8000) != 0;
                if (!switching)
                {
                    switching = true;
                    switchCancelled = false;
                    ui.BeginInvoke(() => App.Switcher.Begin(back));
                }
                else if (!switchCancelled)
                    ui.BeginInvoke(() => App.Switcher.Move(back ? -1 : 1));
            }
            return true;
        }

        if (switching)
        {
            switch (vk)
            {
                case VK_ESCAPE:
                    if (down && !switchCancelled)
                    {
                        switchCancelled = true;
                        ui.BeginInvoke(() => App.Switcher.Cancel());
                    }
                    return true;
                case VK_LEFT or VK_RIGHT:
                    if (down && !switchCancelled) ui.BeginInvoke(() => App.Switcher.Move(vk == VK_LEFT ? -1 : 1));
                    return true;
                case VK_Q:
                    if (down && !switchCancelled) ui.BeginInvoke(() => App.Switcher.QuitSelected());
                    return true;
            }
        }
        return false;
    }
}
