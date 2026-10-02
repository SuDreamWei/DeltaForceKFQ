using System.Runtime.InteropServices;
using System.Windows.Input;

namespace DeltaHarmonica.Interop;

/// <summary>
/// Synthesises keyboard and mouse input via SendInput.
///
/// Scancodes are filled in properly (unlike many scripts which only set wVk) and
/// extended keys are flagged, because a missing scancode is one of the clearest
/// "this is synthetic" signals.
/// </summary>
public static class InputSimulator
{
    /// <summary>Marker written to dwExtraInfo so synthetic events are identifiable.</summary>
    public static readonly UIntPtr Magic = (UIntPtr)0x44524D4E;

    private const int INPUT_MOUSE = 0;
    private const int INPUT_KEYBOARD = 1;

    private const uint KEYEVENTF_EXTENDEDKEY = 0x0001;
    private const uint KEYEVENTF_KEYUP = 0x0002;

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
    private const uint MOUSEEVENTF_MIDDLEDOWN = 0x0020;
    private const uint MOUSEEVENTF_MIDDLEUP = 0x0040;

    [StructLayout(LayoutKind.Sequential)]
    private struct MOUSEINPUT
    {
        public int dx, dy;
        public uint mouseData, dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk, wScan;
        public uint dwFlags, time;
        public UIntPtr dwExtraInfo;
    }

    [StructLayout(LayoutKind.Explicit)]
    private struct INPUTUNION
    {
        [FieldOffset(0)] public MOUSEINPUT mi;
        [FieldOffset(0)] public KEYBDINPUT ki;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct INPUT
    {
        public int type;
        public INPUTUNION u;
    }

    [DllImport("user32.dll", SetLastError = true)]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [DllImport("user32.dll")]
    private static extern ushort MapVirtualKey(uint uCode, uint uMapType);

    private const uint MAPVK_VK_TO_VSC = 0;

    private static readonly int[] ExtendedKeys =
    {
        0x5B, 0x5C, 0x5D,             // Win, Win, Apps
        0x2D, 0x2E, 0x24, 0x23, 0x21, 0x22,   // Insert Delete Home End PgUp PgDn
        0x25, 0x26, 0x27, 0x28,       // arrows
        0x90, 0x6F,                   // NumLock, Divide
    };

    public static bool KeyDown(int vk) => SendKey(vk, false);
    public static bool KeyUp(int vk) => SendKey(vk, true);

    private static bool SendKey(int vk, bool up)
    {
        var scan = MapVirtualKey((uint)vk, MAPVK_VK_TO_VSC);
        uint flags = up ? KEYEVENTF_KEYUP : 0;
        if (Array.IndexOf(ExtendedKeys, vk) >= 0) flags |= KEYEVENTF_EXTENDEDKEY;

        var inputs = new INPUT[1];
        inputs[0].type = INPUT_KEYBOARD;
        inputs[0].u.ki = new KEYBDINPUT
        {
            wVk = (ushort)vk,
            wScan = scan,
            dwFlags = flags,
            time = 0,
            dwExtraInfo = Magic,
        };
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
    }

    public static bool MouseDown(MouseButton b) => SendMouse(b, false);
    public static bool MouseUp(MouseButton b) => SendMouse(b, true);

    private static bool SendMouse(MouseButton b, bool up)
    {
        uint f = (b, up) switch
        {
            (MouseButton.Left, false) => MOUSEEVENTF_LEFTDOWN,
            (MouseButton.Left, true) => MOUSEEVENTF_LEFTUP,
            (MouseButton.Right, false) => MOUSEEVENTF_RIGHTDOWN,
            (MouseButton.Right, true) => MOUSEEVENTF_RIGHTUP,
            (MouseButton.Middle, false) => MOUSEEVENTF_MIDDLEDOWN,
            (MouseButton.Middle, true) => MOUSEEVENTF_MIDDLEUP,
            _ => 0u,
        };
        if (f == 0) return false;

        var inputs = new INPUT[1];
        inputs[0].type = INPUT_MOUSE;
        inputs[0].u.mi = new MOUSEINPUT { dwFlags = f, dwExtraInfo = Magic };
        return SendInput(1, inputs, Marshal.SizeOf<INPUT>()) == 1;
    }

    public enum MouseButton { Left, Middle, Right }
}
