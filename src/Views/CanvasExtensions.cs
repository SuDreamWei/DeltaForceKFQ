using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DeltaHarmonica.Views;

/// <summary>Small helpers for placing children on a Canvas.</summary>
internal static class CanvasExtensions
{
    public static T At<T>(this T el, double left, double top) where T : UIElement
    {
        Canvas.SetLeft(el, left);
        Canvas.SetTop(el, top);
        return el;
    }

    /// <summary>Rounded rectangle brush helper for the piano roll.</summary>
    public static SolidColorBrush Frozen(byte r, byte g, byte b, byte a = 255)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
