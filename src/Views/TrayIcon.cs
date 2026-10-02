using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;

namespace DeltaHarmonica.Views;

/// <summary>
/// Minimal system tray icon built on Shell_NotifyIcon, so the app can keep
/// running (and keep its hotkeys alive) while the window is hidden.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private const int WM_APP = 0x8000;
    private const int WM_TRAYICON = WM_APP + 1;
    private const int WM_LBUTTONUP = 0x0202;
    private const int WM_RBUTTONUP = 0x0205;
    private const int WM_COMMAND = 0x0111;

    private const uint NIM_ADD = 0x00000000;
    private const uint NIM_MODIFY = 0x00000001;
    private const uint NIM_DELETE = 0x00000002;
    private const uint NIF_MESSAGE = 0x00000001;
    private const uint NIF_ICON = 0x00000002;
    private const uint NIF_TIP = 0x00000004;
    private const uint NIF_INFO = 0x00000010;

    private const int IDM_SHOW = 1001;
    private const int IDM_EXIT = 1002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct NOTIFYICONDATA
    {
        public int cbSize;
        public IntPtr hWnd;
        public int uID;
        public uint uFlags;
        public int uCallbackMessage;
        public IntPtr hIcon;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 128)] public string szTip;
        public uint dwState;
        public uint dwStateMask;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)] public string szInfo;
        public int uTimeoutOrVersion;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 64)] public string szInfoTitle;
        public uint dwInfoFlags;
        public Guid guidItem;
        public IntPtr hBalloonIcon;
    }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode)]
    private static extern bool Shell_NotifyIconW(uint dwMessage, ref NOTIFYICONDATA lpData);

    [DllImport("user32.dll")]
    private static extern IntPtr LoadIconW(IntPtr hInstance, IntPtr lpIconName);

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern IntPtr CreatePopupMenu();

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool AppendMenuW(IntPtr hMenu, uint uFlags, IntPtr uIDNewItem, string lpNewItem);

    [DllImport("user32.dll")]
    private static extern bool DestroyMenu(IntPtr hMenu);

    [DllImport("user32.dll")]
    private static extern int TrackPopupMenu(IntPtr hMenu, uint uFlags, int x, int y,
                                             int nReserved, IntPtr hWnd, IntPtr prcRect);

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X, Y; }

    private const uint MF_STRING = 0x00000000;
    private const uint MF_SEPARATOR = 0x00000800;
    private const uint TPM_RIGHTBUTTON = 0x0002;
    private const uint TPM_RETURNCMD = 0x0100;

    private readonly MainWindow _owner;
    private HwndSource? _source;
    private IntPtr _handle;
    private NOTIFYICONDATA _data;
    private bool _added;

    public event Action? RestoreRequested;

    public TrayIcon(MainWindow owner)
    {
        _owner = owner;
        _handle = new WindowInteropHelper(owner).EnsureHandle();
        _source = HwndSource.FromHwnd(_handle);
        _source?.AddHook(WndProc);

        _data = new NOTIFYICONDATA
        {
            cbSize = Marshal.SizeOf<NOTIFYICONDATA>(),
            hWnd = _handle,
            uID = 1,
            uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP,
            uCallbackMessage = WM_TRAYICON,
            hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512),   // IDI_APPLICATION
            szTip = "三角洲行动 · 口风琴自动演奏器",
            szInfo = string.Empty,
            szInfoTitle = string.Empty,
        };
    }

    public void SetVisible(bool visible)
    {
        if (visible && !_added)
        {
            _added = Shell_NotifyIconW(NIM_ADD, ref _data);
        }
        else if (!visible && _added)
        {
            Shell_NotifyIconW(NIM_DELETE, ref _data);
            _added = false;
        }
    }

    public void ShowBalloon(string title, string text)
    {
        if (!_added) SetVisible(true);
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP | NIF_INFO;
        _data.szInfoTitle = title;
        _data.szInfo = text;
        _data.dwInfoFlags = 0x00000001;   // NIIF_INFO
        Shell_NotifyIconW(NIM_MODIFY, ref _data);
        _data.uFlags = NIF_MESSAGE | NIF_ICON | NIF_TIP;
    }

    private IntPtr WndProc(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_TRAYICON)
        {
            int evt = lParam.ToInt32() & 0xFFFF;
            if (evt == WM_LBUTTONUP)
            {
                RestoreRequested?.Invoke();
                handled = true;
            }
            else if (evt == WM_RBUTTONUP)
            {
                GetCursorPos(out var pt);
                var menu = CreatePopupMenu();
                AppendMenuW(menu, MF_STRING, (IntPtr)IDM_SHOW, "显示窗口");
                AppendMenuW(menu, MF_SEPARATOR, IntPtr.Zero, string.Empty);
                AppendMenuW(menu, MF_STRING, (IntPtr)IDM_EXIT, "退出");

                SetForegroundWindow(hwnd);
                int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD,
                                         pt.X, pt.Y, 0, hwnd, IntPtr.Zero);
                DestroyMenu(menu);

                if (cmd == IDM_SHOW) RestoreRequested?.Invoke();
                else if (cmd == IDM_EXIT) _owner.ExitApplication();

                handled = true;
            }
        }
        return IntPtr.Zero;
    }

    public void Dispose()
    {
        SetVisible(false);
        if (_source is not null)
        {
            _source.RemoveHook(WndProc);
            _source = null;
        }
    }
}
