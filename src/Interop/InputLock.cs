using System.Runtime.InteropServices;

namespace DeltaHarmonica.Interop;

/// <summary>
/// Blocks physical mouse and keyboard input from reaching the game while a
/// performance is running, so a stray movement or keypress cannot disturb the
/// octave/semitone modifier state.
///
/// Uses a low-level input hook that swallows events from real hardware but lets
/// through anything this program itself injects (identified by the marker written
/// into dwExtraInfo). Hooks are per-process desktop-wide and require the message
/// loop to be running, which it is - this is a normal WPF UI thread.
/// </summary>
public sealed class InputLock : IDisposable
{
    private const int WH_KEYBOARD_LL = 13;
    private const int WH_MOUSE_LL = 14;

    private const int WM_KEYDOWN = 0x0100;
    private const int WM_KEYUP = 0x0101;
    private const int WM_SYSKEYDOWN = 0x0104;
    private const int WM_SYSKEYUP = 0x0105;

    private delegate IntPtr HookProc(int nCode, IntPtr wParam, IntPtr lParam);

    [StructLayout(LayoutKind.Sequential)]
    private struct KBDLLHOOKSTRUCT
    {
        public uint vkCode;
        public uint scanCode;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MSLLHOOKSTRUCT
    {
        public int ptX, ptY;
        public uint mouseData;
        public uint flags;
        public uint time;
        public UIntPtr dwExtraInfo;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern IntPtr SetWindowsHookExW(int idHook, HookProc lpfn, IntPtr hMod, uint dwThreadId);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnhookWindowsHookEx(IntPtr hhk);

    [DllImport("user32.dll")]
    private static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr GetModuleHandleW(string? lpModuleName);

    private IntPtr _kbHook = IntPtr.Zero;
    private IntPtr _msHook = IntPtr.Zero;

    // Held as fields so the delegates are not collected while the hooks are live.
    private readonly HookProc _kbProc;
    private readonly HookProc _msProc;

    /// <summary>Virtual keys that must keep working even while locked.</summary>
    private readonly HashSet<uint> _alwaysAllow = new();

    private volatile bool _locked;

    /// <summary>True while physical input is being swallowed.</summary>
    public bool IsLocked => _locked;

    /// <summary>Number of physical events blocked since the lock engaged.</summary>
    public long BlockedCount { get; private set; }

    public InputLock()
    {
        _kbProc = KeyboardHook;
        _msProc = MouseHook;
    }

    /// <summary>
    /// Keys that stay usable while locked. Always includes the emergency release
    /// key so the user can never lock themselves out.
    /// </summary>
    public void AllowKey(uint virtualKey) => _alwaysAllow.Add(virtualKey);

    public void ClearAllowedKeys() => _alwaysAllow.Clear();

    public bool Lock()
    {
        if (_locked) return true;

        try
        {
            var mod = GetModuleHandleW(null);
            _kbHook = SetWindowsHookExW(WH_KEYBOARD_LL, _kbProc, mod, 0);
            _msHook = SetWindowsHookExW(WH_MOUSE_LL, _msProc, mod, 0);

            // A failed mouse hook alone is still useful (keyboard is the important one).
            if (_kbHook == IntPtr.Zero && _msHook == IntPtr.Zero)
            {
                Dispose();
                return false;
            }

            BlockedCount = 0;
            _locked = true;
            return true;
        }
        catch
        {
            Dispose();
            return false;
        }
    }

    public void Unlock()
    {
        if (!_locked) return;

        // Release hooks only after clearing the flag, so a late callback still
        // passes events through rather than swallowing them during teardown.
        _locked = false;

        if (_kbHook != IntPtr.Zero) { UnhookWindowsHookEx(_kbHook); _kbHook = IntPtr.Zero; }
        if (_msHook != IntPtr.Zero) { UnhookWindowsHookEx(_msHook); _msHook = IntPtr.Zero; }
    }

    /// <summary>True if the event came from this program rather than real hardware.</summary>
    private static bool IsSynthetic(UIntPtr extraInfo) => extraInfo == InputSimulator.Magic;

    private IntPtr KeyboardHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _locked)
        {
            int msg = wParam.ToInt32();
            bool isKeyEvent = msg is WM_KEYDOWN or WM_KEYUP or WM_SYSKEYDOWN or WM_SYSKEYUP;

            if (isKeyEvent)
            {
                var data = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                if (!IsSynthetic(data.dwExtraInfo) && !_alwaysAllow.Contains(data.vkCode))
                {
                    BlockedCount++;
                    return (IntPtr)1;            // swallow: the game never sees it
                }
            }
        }
        return CallNextHookEx(_kbHook, nCode, wParam, lParam);
    }

    private IntPtr MouseHook(int nCode, IntPtr wParam, IntPtr lParam)
    {
        if (nCode >= 0 && _locked)
        {
            var data = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
            if (!IsSynthetic(data.dwExtraInfo))
            {
                // Let the pointer move so the user is not fighting a frozen cursor,
                // but block every button so clicks cannot disturb the game state.
                const uint WM_MOUSEMOVE = 0x0200;
                if (wParam.ToInt32() != WM_MOUSEMOVE)
                {
                    BlockedCount++;
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(_msHook, nCode, wParam, lParam);
    }

    public void Dispose() => Unlock();
}
