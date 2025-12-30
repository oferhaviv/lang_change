using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

internal static class Program
{
    // ---------------- Config models ----------------
    private sealed class SharedConfig
    {
        public ConnectionConfig Connection { get; set; } = new();
        public Dictionary<string, string> LanguageMap { get; set; } = new(); // "040D" -> "heb"
    }

    private sealed class ConnectionConfig
    {
        public string Host { get; set; } = "127.0.0.1";
        public int Port { get; set; } = 47655;
    }

    // ---------------- HTTP ----------------
    private static readonly HttpClient Http = new HttpClient { Timeout = TimeSpan.FromSeconds(2) };
    private static SharedConfig _cfg = new SharedConfig();

    // Debounce last lang id
    private static int _lastLangId = -1;

    // ---------------- Win32: foreground hook (optional) ----------------
    private static WinEventDelegate? _winEventDelegate;
    private static IntPtr _hWinEventHook = IntPtr.Zero;

    // ---------------- Win32 constants ----------------
    private const uint EVENT_SYSTEM_FOREGROUND = 0x0003;
    private const uint WINEVENT_OUTOFCONTEXT = 0x0000;

    // Toggle to reduce noise once working
    private static readonly bool Verbose = true;

    public static void Main()
    {
        Console.WriteLine("LangChangeToiCUE started (Foreground polling + optional WinEventHook).");
        Console.WriteLine("Press Ctrl+C to exit.\n");

        LoadConfig();

        // Optional: Foreground WinEventHook (nice-to-have; polling is the real engine)
        _winEventDelegate = WinEventProc;
        _hWinEventHook = SetWinEventHook(
            EVENT_SYSTEM_FOREGROUND, EVENT_SYSTEM_FOREGROUND,
            IntPtr.Zero, _winEventDelegate,
            0, 0, WINEVENT_OUTOFCONTEXT);

        if (_hWinEventHook == IntPtr.Zero && Verbose)
        {
            Console.WriteLine($"[WARN] SetWinEventHook failed. Win32Error={Marshal.GetLastWin32Error()} (polling will still work)");
        }

        // Poll the CURRENT foreground thread periodically (robust, low CPU)
        _ = Task.Run(async () =>
        {
            while (true)
            {
                await Task.Delay(500).ConfigureAwait(false); // 2Hz, very light CPU

                var hwnd = GetForegroundWindow();
                if (hwnd == IntPtr.Zero) continue;

                uint pid;
                uint tid = GetWindowThreadProcessId(hwnd, out pid);
                if (tid == 0) continue;

                PushLangFromThread(tid);
            }
        });

        // keep process alive
        Thread.Sleep(Timeout.Infinite);
    }

    // ---------------- Config ----------------
    private static void LoadConfig()
    {
        var baseDir = AppContext.BaseDirectory;
        var path = Path.Combine(baseDir, "config.json");

        if (!File.Exists(path))
        {
            Console.WriteLine($"[WARN] config.json not found at: {path}");
            Console.WriteLine("Using defaults: http://127.0.0.1:47655 and LanguageMap {040D:heb, 0409:eng}\n");

            _cfg = new SharedConfig
            {
                Connection = new ConnectionConfig { Host = "127.0.0.1", Port = 47655 },
                LanguageMap = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
                {
                    ["040D"] = "heb",
                    ["0409"] = "eng",
                }
            };
            return;
        }

        var json = File.ReadAllText(path);
        _cfg = JsonSerializer.Deserialize<SharedConfig>(json, new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        }) ?? throw new InvalidOperationException("Failed to parse config.json");

        _cfg.Connection ??= new ConnectionConfig();
        _cfg.LanguageMap ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        Console.WriteLine($"Loaded config.json from: {path}");
        Console.WriteLine($"REST target: http://{_cfg.Connection.Host}:{_cfg.Connection.Port}/");
        Console.WriteLine($"LanguageMap entries: {_cfg.LanguageMap.Count}\n");
    }

    // ---------------- Foreground event (optional) ----------------
    private static void WinEventProc(
        IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime)
    {
        if (!Verbose) return;
        if (hwnd == IntPtr.Zero) return;

        uint pid;
        uint tid = GetWindowThreadProcessId(hwnd, out pid);
        if (tid == 0) return;

        Console.WriteLine($"[FG] HWND=0x{hwnd.ToInt64():X} PID={pid} TID={tid}");
    }

    // ---------------- Language -> REST ----------------
    private static void PushLangFromThread(uint tid)
    {
        IntPtr hkl = GetKeyboardLayout((int)tid);
        ushort langId = (ushort)((ulong)hkl & 0xFFFF);

        // Debounce duplicates
        int prev = Interlocked.Exchange(ref _lastLangId, langId);
        if (prev == langId) return;

        string key = langId.ToString("X4");

        if (!_cfg.LanguageMap.TryGetValue(key, out var endpoint) || string.IsNullOrWhiteSpace(endpoint))
        {
            if (Verbose)
                Console.WriteLine($"[INFO] LangID=0x{langId:X4} unmapped.");
            return;
        }

        if (Verbose)
            Console.WriteLine($"[LANG] 0x{langId:X4} -> POST /{endpoint}");

        _ = Task.Run(async () =>
        {
            try
            {
                var url = $"http://{_cfg.Connection.Host}:{_cfg.Connection.Port}/{endpoint}";
                using var resp = await Http.PostAsync(url, content: null).ConfigureAwait(false);

                if (Verbose)
                    Console.WriteLine($"[REST] {(int)resp.StatusCode} {resp.ReasonPhrase}");
            }
            catch (Exception ex)
            {
                if (Verbose)
                    Console.WriteLine($"[REST-WARN] {ex.Message}");
            }
        });
    }

    // ---------------- Win32 interop ----------------
    private delegate void WinEventDelegate(IntPtr hWinEventHook, uint eventType, IntPtr hwnd,
        int idObject, int idChild, uint dwEventThread, uint dwmsEventTime);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWinEventHook(uint eventMin, uint eventMax, IntPtr hmodWinEventProc,
        WinEventDelegate lpfnWinEventProc, uint idProcess, uint idThread, uint dwFlags);

    [DllImport("user32.dll")]
    private static extern IntPtr GetForegroundWindow();

    // returns thread id; outputs process id
    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);

    [DllImport("user32.dll")]
    private static extern IntPtr GetKeyboardLayout(int idThread);
}
