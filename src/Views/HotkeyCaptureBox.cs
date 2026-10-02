using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using DeltaHarmonica.Services;

namespace DeltaHarmonica.Views;

/// <summary>
/// A text box that captures the next key combination the user presses.
/// </summary>
public sealed class HotkeyCaptureBox : Border
{
    private readonly TextBlock _text;
    private HotkeyConfig _config = new();
    private bool _capturing;

    public event Action<uint, uint>? Captured;

    public bool IsCapturing => _capturing;

    public HotkeyCaptureBox(HotkeyConfig config)
    {
        CornerRadius = new CornerRadius(7);
        BorderThickness = new Thickness(1);
        Padding = new Thickness(9, 6, 9, 6);
        Cursor = Cursors.Hand;
        Focusable = true;
        Background = new SolidColorBrush(Color.FromRgb(0x15, 0x18, 0x21));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3C));

        _text = new TextBlock
        {
            VerticalAlignment = VerticalAlignment.Center,
            FontFamily = new FontFamily("Microsoft YaHei UI"),
            FontSize = 13,
        };
        Child = _text;

        SetBinding(config);

        MouseLeftButtonDown += (_, __) => BeginCapture();
        LostKeyboardFocus += (_, __) => { if (_capturing) EndCapture(false); };
        PreviewKeyDown += OnKeyDown;
    }

    public void SetBinding(HotkeyConfig config)
    {
        _config = config;
        _text.Text = config.Describe();
        _text.Foreground = new SolidColorBrush(Color.FromRgb(0xE9, 0xEC, 0xF2));
    }

    private void BeginCapture()
    {
        Focus();
        Keyboard.Focus(this);
        _capturing = true;
        _text.Text = "请按下组合键…（Esc 取消）";
        _text.Foreground = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
        BorderBrush = new SolidColorBrush(Color.FromRgb(0xE8, 0xA3, 0x3D));
    }

    public void CancelCapture() => EndCapture(false);

    private void EndCapture(bool commit)
    {
        _capturing = false;
        BorderBrush = new SolidColorBrush(Color.FromRgb(0x2A, 0x2F, 0x3C));
        if (!commit) SetBinding(_config);
    }

    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (!_capturing) return;
        e.Handled = true;

        var key = e.Key == Key.System ? e.SystemKey : e.Key;

        if (key == Key.Escape) { EndCapture(false); return; }

        // Ignore the modifier keys themselves; wait for a "real" key.
        if (key is Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl
            or Key.LeftAlt or Key.RightAlt or Key.LWin or Key.RWin)
            return;

        uint mods = 0;
        var m = Keyboard.Modifiers;
        if ((m & ModifierKeys.Control) != 0) mods |= HotkeyManager.MOD_CONTROL;
        if ((m & ModifierKeys.Shift) != 0) mods |= HotkeyManager.MOD_SHIFT;
        if ((m & ModifierKeys.Alt) != 0) mods |= HotkeyManager.MOD_ALT;
        if ((m & ModifierKeys.Windows) != 0) mods |= HotkeyManager.MOD_WIN;

        uint vk = (uint)KeyInterop.VirtualKeyFromKey(key);
        if (vk == 0) return;

        _config.Modifiers = mods;
        _config.VirtualKey = vk;
        SetBinding(_config);
        EndCapture(true);
        Captured?.Invoke(mods, vk);
    }
}
