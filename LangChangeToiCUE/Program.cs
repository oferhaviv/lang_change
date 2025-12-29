// Program.cs
// .NET 8 Console app (Windows only)
//
// Goal: Detect input language/layout changes reliably with low latency (no polling loop).
// Approach:
//  - SetWinEventHook(EVENT_SYSTEM_FOREGROUND) keeps track of the current foreground window.
//  - WH_KEYBOARD_LL (low-level keyboard hook) fires on real key events.
//  - On key-down / key-up, we query the foreground thread HKL and detect layout changes.
//  - When we detect a likely layout-switch hotkey (Win+Space or Alt+Shift), we do a short re-check burst
//    (20/60/120/200ms) to catch delayed HKL updates.
//
// Next step for iCUE: in OnLayoutChanged(langId) you’ll trigger your iCUE hotkey/action.

using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    // WinEvent hook for foreground changes
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Low-level keyboard hook
    private const int WH_KEYBOARD_LL = 13;

    // Keyboard messages
    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    // Virtual keys (subset)
    private const int VK_MENU = 0x12;   // Alt
    private const int VK_SHIFT = 0x10;  // Shift
    private const int VK_LWIN = 0x5B;
    private const int VK_RWIN = 0x5C;
    private const int VK_SPACE = 0x20;

    // Hooks & delegates
    private static WinEventDelegate? _winEventProc;
    private static IntPtr _winEventHook = IntPtr.Zero;

    private static LowLevelKeyboardProc? _kbdProc;
    private static IntPtr _kbdHook = IntPtr.Zero;

    // Current foreground window info
    private static IntPtr _fgHwnd = IntPtr.Zero;
    private static uint _fgTid = 0;
    private static uint _fgPid = 0;

    // Layout tracking
    private static ushort _lastLang = 0;

    // Modifier state for detecting common layout switch hotkeys
    private static bool _altDown, _shiftDown, _lwinDown, _rwinDown, _spaceDown;

    // Burst re-check token (cancels previous burst when a new one starts)
    private static int _burstToken = 0;

    [STAThread]
    private static void Main()
    {
        Console.WriteLine("Foreground + KeyboardHook layout watcher started.");
        Console.WriteLine("Switch language (Win+Space / Alt+Shift) and type; it will log layout changes.");
        Console.WriteLine("Press Ctrl+C to exit.\n");

        // Foreground change hook
        _winEventProc = OnWinEvent;
        _winEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventProc,
            0, 0,
            WINEVENT_OUTOFCONTEXT);

        if (_winEventHook == IntPtr.Zero)
            ThrowLastWin32("SetWinEventHook failed");

        // Prime initial foreground
        UpdateForeground(GetForegroundWindow());

        // Install low-level keyboard hook (does not require injection)
        _kbdProc = KeyboardProc;
        _kbdHook = SetKeyboardHook(_kbdProc);

        // Message loop keeps win-event callbacks reliable
        MSG msg;
        while (GetMessage(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessage(ref msg);
        }

        Cleanup();
    }

    // --- Foreground tracking ---

    private static void OnWinEvent(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime)
    {
        if (eventType == EVENT_SYSTEM_FOREGROUND && hwnd != IntPtr.Zero)
        {
            UpdateForeground(hwnd);
        }
    }

    private static void UpdateForeground(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero) return;

        _fgHwnd = hwnd;
        _fgTid = GetWindowThreadProcessId(hwnd, out _fgPid);

        var (lang, hkl) = GetLangForTid(_fgTid);
        _lastLang = lang;

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Foreground changed");
        Console.WriteLine($"  HWND=0x{_fgHwnd.ToInt64():X}  PID={_fgPid}  TID={_fgTid}");
        Console.WriteLine($"  HKL=0x{hkl.ToInt64():X}  LangID=0x{lang:X4} ({LangName(lang)})");
        Console.WriteLine();
    }

    // --- Keyboard hook & layout detection ---

    private static IntPtr KeyboardProc(int nCode, IntPtr wParam, IntPtr lParam)
    {
        try
        {
            if (nCode >= 0)
            {
                int msg = (int)wParam;
                bool isDown = (msg == WM_KEYDOWN || msg == WM_SYSKEYDOWN);
                bool isUp = (msg == WM_KEYUP || msg == WM_SYSKEYUP);

                if (isDown || isUp)
                {
                    var kb = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    int vk = (int)kb.vkCode;

                    // Track modifier/key states (simple; sufficient for detecting Win+Space / Alt+Shift)
                    if (vk == VK_MENU) _altDown = isDown ? true : isUp ? false : _altDown;
                    if (vk == VK_SHIFT) _shiftDown = isDown ? true : isUp ? false : _shiftDown;
                    if (vk == VK_LWIN) _lwinDown = isDown ? true : isUp ? false : _lwinDown;
                    if (vk == VK_RWIN) _rwinDown = isDown ? true : isUp ? false : _rwinDown;
                    if (vk == VK_SPACE) _spaceDown = isDown ? true : isUp ? false : _spaceDown;

                    // Always check on key-down AND key-up (some switches apply on key-up)
                    CheckLayoutOnce();

                    // Detect common language switch hotkeys
                    bool winSpace = ((_lwinDown || _rwinDown) && _spaceDown);
                    bool altShift = (_altDown && _shiftDown);

                    // If user is initiating a layout switch, do a short burst of delayed checks
                    if (isDown && (winSpace || altShift))
                        StartRecheckBurst();
                }
            }
        }
        catch
        {
            // Never throw from a hook callback.
        }

        return CallNextHookEx(_kbdHook, nCode, wParam, lParam);
    }

    private static void CheckLayoutOnce()
    {
        // Resolve foreground live (more reliable than cached tid alone)
        IntPtr hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return;

        uint tid = GetWindowThreadProcessId(hwnd, out _);
        var (lang, hkl) = GetLangForTid(tid);

        if (lang != 0 && lang != _lastLang)
        {
            _lastLang = lang;
            Console.WriteLine($"[{DateTime.Now:HH:mm:ss.fff}] Layout changed: 0x{lang:X4} ({LangName(lang)})  HKL=0x{hkl.ToInt64():X}");

            OnLayoutChanged(lang);
        }
    }

    private static void StartRecheckBurst()
    {
        int token = Interlocked.Increment(ref _burstToken);

        // Small delays to catch delayed HKL updates after the hotkey sequence
        int[] delaysMs = { 20, 60, 120, 200 };

        foreach (int delay in delaysMs)
        {
            _ = Task.Run(async () =>
            {
                await Task.Delay(delay).ConfigureAwait(false);
                if (token != _burstToken) return; // a newer burst started
                CheckLayoutOnce();
            });
        }
    }

    // --- Where you’ll integrate iCUE later ---
    private static void OnLayoutChanged(ushort langId)
    {
        // TODO: Trigger iCUE action here:
        //  - if Hebrew -> enable "Q blue" indicator
        //  - if English -> disable indicator
        //
        // For now we only log:
        // Console.WriteLine($"[ACTION] Would update iCUE for {LangName(langId)}");
    }

    // --- Helpers ---

    private static (ushort langId, IntPtr hkl) GetLangForTid(uint tid)
    {
        IntPtr hkl = GetKeyboardLayout(tid);
        ushort langId = (ushort)((ulong)hkl.ToInt64() & 0xFFFF);
        return (langId, hkl);
    }

    private static string LangName(ushort langId) => langId switch
    {
        0x0409 => "English (US)",
        0x040D => "Hebrew",
        _ => "Other"
    };

    private static IntPtr SetKeyboardHook(LowLevelKeyboardProc proc)
    {
        using var p = Process.GetCurrentProcess();
        using var m = p.MainModule!;
        IntPtr hMod = GetModuleHandle(m.ModuleName);

        IntPtr hook = SetWindowsHookEx(WH_KEYBOARD_LL, proc, hMod, 0);
        if (hook == IntPtr.Zero)
            ThrowLastWin32("SetWindowsHookEx(WH_KEYBOARD_LL) failed");

        return hook;
    }

    private static void Cleanup()
    {
        if (_kbdHook != IntPtr.Zero) UnhookWindowsHookEx(_kbdHook);
        if (_winEventHook != IntPtr.Zero) UnhookWinEvent(_winEventHook);
    }

    private static void ThrowLastWin32(string message)
    {
        int err = Marshal.GetLastWin32Error();
        throw new System.ComponentModel.Win32Exception(err, message);
    }

    // --- Win32 structs & delegates ---

    private delegate void WinEventDelegate(
        IntPtr hWinEventHook,
        uint eventType,
        IntPtr hwnd,
        int idObject,
        int idChild,
        uint dwEventThread,
        uint dwmsEventTime);

    private delegate IntPtr LowLevelKeyboardProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public IntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSG
    {
        public IntPtr hwnd;
        public uint message;
        public IntPtr wParam;
        public IntPtr lParam;
        public uint time;
        public POINT pt;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int x;
        public int y;
    }

    // --- P/Invokes ---

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(
        uint eventMin,
        uint eventMax,
        IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc,
        uint idProcess,
        uint idThread,
        uint dwFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWinEvent(IntPtr hWinEventHook);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(uint idThread);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookEx(int idHook, LowLevelKeyboardProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandle(string? lpModuleName);

    [DllImport("user32.dll")]
    private static extern sbyte GetMessage(out MSG lpMsg, IntPtr hWnd, uint wMsgFilterMin, uint wMsgFilterMax);

    [DllImport("user32.dll")]
    private static extern bool TranslateMessage([In] ref MSG lpMsg);

    [DllImport("user32.dll")]
    private static extern IntPtr DispatchMessage([In] ref MSG lpmsg);
}
