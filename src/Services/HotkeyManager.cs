using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeltaHarmonica.Services;

public enum HotkeyAction
{
    NextSong,
    Restart,
    StopPlayback,
    ToggleWindow,
    /// <summary>Play the selected/current song immediately, skipping the lead-in.</summary>
    InstantPlay,
}

public sealed class HotkeyBinding
{
    public HotkeyAction Action { get; set; }
    public uint Modifiers { get; set; }
    public uint VirtualKey { get; set; }

    public string Describe()
    {
        var parts = new List<string>();
        if ((Modifiers & HotkeyManager.MOD_CONTROL) != 0) parts.Add("Ctrl");
        if ((Modifiers & HotkeyManager.MOD_SHIFT) != 0) parts.Add("Shift");
        if ((Modifiers & HotkeyManager.MOD_ALT) != 0) parts.Add("Alt");
        if ((Modifiers & HotkeyManager.MOD_WIN) != 0) parts.Add("Win");
        parts.Add(KeyName(VirtualKey));
        return string.Join("+", parts);
    }

    public static string KeyName(uint vk) => vk switch
    {
        >= 0x70 and <= 0x87 => "F" + (vk - 0x6F),
        0x30 => "0", 0x31 => "1", 0x32 => "2", 0x33 => "3", 0x34 => "4",
        0x35 => "5", 0x36 => "6", 0x37 => "7", 0x38 => "8", 0x39 => "9",
        >= 0x41 and <= 0x5A => ((char)vk).ToString(),
        _ => "0x" + vk.ToString("X2"),
    };
}

/// <summary>Registers system-wide hotkeys through a message-only window hook.</summary>
public sealed class HotkeyManager : IDisposable
{
    public const uint MOD_ALT = 0x0001;
    public const uint MOD_CONTROL = 0x0002;
    public const uint MOD_SHIFT = 0x0004;
    public const uint MOD_WIN = 0x0008;
    public const uint MOD_NOREPEAT = 0x4000;

    private const int WM_HOTKEY = 0x0312;

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    private HwndSource? _source;
    private readonly Dictionary<int, HotkeyBinding> _registered = new();
    private int _nextId = 1;

    public event Action<HotkeyAction>? Triggered;

    /// <summary>Hotkeys that failed to register, so the UI can report them.</summary>
    public List<string> Failures { get; } = new();

    public void Attach(Window window)
    {
        var helper = new WindowInteropHelper(window);
        var handle = helper.EnsureHandle();
        _source = HwndSource.FromHwnd(handle);
        _source?.AddHook(WndProc);
    }

    public bool Register(HotkeyAction action, uint modifiers, uint vk)
    {
        if (_source is null) throw new InvalidOperationException("HotkeyManager 尚未 Attach");

        // Remove any previous binding for this action.
        foreach (var kv in _registered.Where(k => k.Value.Action == action).ToList())
        {
            UnregisterHotKey(_source.Handle, kv.Key);
            _registered.Remove(kv.Key);
        }

        int id = _nextId++;
        if (!RegisterHotKey(_source.Handle, id, modifiers | MOD_NOREPEAT, vk))
        {
            Failures.Add($"{action} ({new HotkeyBinding { Modifiers = modifiers, VirtualKey = vk }.Describe()})");
            return false;
        }

        _registered[id] = new HotkeyBinding { Action = action, Modifiers = modifiers, VirtualKey = vk };
        return true;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY)
        {
            int id = wParam.ToInt32();
            if (_registered.TryGetValue(id, out var b))
            {
                Triggered?.Invoke(b.Action);
                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        if (_source is not null)
        {
            foreach (var id in _registered.Keys)
                UnregisterHotKey(_source.Handle, id);
            _source.RemoveHook(WndProc);
            _source = null;
        }
        _registered.Clear();
    }
}
