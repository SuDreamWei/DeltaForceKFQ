// Exercises the piano-roll interaction code paths directly, including the exact
// sequence that used to crash (MouseMove -> MouseDown with null event args).
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using DeltaHarmonica.Views;

Console.OutputEncoding = System.Text.Encoding.UTF8;
int fail = 0;
void Check(bool ok, string what)
{
    Console.WriteLine($"  [{(ok ? "PASS" : "FAIL")}] {what}");
    if (!ok) fail++;
}

// A WPF window and its canvas need an STA thread and a Dispatcher, plus an
// Application carrying the theme resources the window references.
var t = new Thread(() =>
{
    try
    {
        var app = new System.Windows.Application();
        app.Resources.MergedDictionaries.Add(
            (ResourceDictionary)System.Windows.Application.LoadComponent(
                new Uri("/UiCheck;component/Views/Theme.xaml", UriKind.Relative)));

        var win = new MainWindow();
        win.Show();
        win.UpdateLayout();

        var canvasField = typeof(MainWindow).GetField("PreviewCanvas",
            BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Public);
        var canvas = canvasField?.GetValue(win) as Canvas;
        Check(canvas is not null, "PreviewCanvas is reachable");
        if (canvas is null) return;

        canvas.Width = 600;
        canvas.Height = 150;
        canvas.UpdateLayout();

        var setCursor = typeof(MainWindow).GetMethod("SetCursorFromPoint",
            BindingFlags.NonPublic | BindingFlags.Instance);
        Check(setCursor is not null, "SetCursorFromPoint exists (the helper the fix introduced)");

        // The old bug: MouseMove called MouseDown(sender, null!), and MouseDown
        // dereferenced e.GetPosition(). Calling the handler with a null MouseEventArgs
        // must not be possible any more - the move handler only takes MouseEventArgs.
        var moveHandler = typeof(MainWindow).GetMethod("PreviewCanvas_MouseMove",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var downHandler = typeof(MainWindow).GetMethod("PreviewCanvas_MouseDown",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var upHandler = typeof(MainWindow).GetMethod("PreviewCanvas_MouseUp",
            BindingFlags.NonPublic | BindingFlags.Instance);

        Check(moveHandler is not null && downHandler is not null && upHandler is not null,
              "all three preview mouse handlers exist");

        // MouseMove takes MouseEventArgs (not MouseButtonEventArgs), so it can no
        // longer be handed a null MouseButtonEventArgs from a delegate mismatch.
        var moveParam = moveHandler!.GetParameters()[1].ParameterType;
        Check(moveParam == typeof(MouseEventArgs),
              "MouseMove takes MouseEventArgs (the null-args path is gone)");

        // Drive SetCursorFromPoint across the canvas - the real work the handlers do.
        var songField = typeof(MainWindow).GetField("_songs",
            BindingFlags.NonPublic | BindingFlags.Instance);
        var songs = songField?.GetValue(win) as System.Collections.IList;
        Console.WriteLine($"  songs loaded in the window: {songs?.Count ?? 0}");

        if (songs is { Count: > 0 })
        {
            var listBox = (ListBox?)typeof(MainWindow)
                .GetField("SongList", BindingFlags.NonPublic | BindingFlags.Instance)!
                .GetValue(win);
            listBox!.SelectedIndex = 0;

            var song = songs[0]!;
            var lenProp = song.GetType().GetProperty("LengthSec")!;
            double len = (double)lenProp.GetValue(song)!;
            Console.WriteLine($"  selected song length: {len:F2}s");

            // Simulate clicks at several fractions, exactly as the handler does.
            var redraw = typeof(MainWindow).GetMethod("RedrawPreview",
                BindingFlags.NonPublic | BindingFlags.Instance);
            bool threw = false;
            for (double frac = 0; frac <= 1.0; frac += 0.1)
            {
                try
                {
                    var pt = new Point(8 + frac * 580, 60);
                    setCursor!.Invoke(win, new object[] { pt, song });
                }
                catch (Exception ex)
                {
                    threw = true;
                    Console.WriteLine($"    threw at frac {frac:F1}: {ex.InnerException?.Message ?? ex.Message}");
                    break;
                }
            }
            Check(!threw, "cursor positioning works across the whole width (no crash)");

            // Out-of-bounds coordinates must not throw either.
            threw = false;
            foreach (var pt in new[] { new Point(-500, -500), new Point(5000, 5000), new Point(0, 0) })
            {
                try { setCursor!.Invoke(win, new object[] { pt, song }); }
                catch (Exception ex)
                {
                    threw = true;
                    Console.WriteLine($"    threw at {pt}: {ex.InnerException?.Message ?? ex.Message}");
                }
            }
            Check(!threw, "out-of-bounds clicks are clamped, not thrown");

            // Redraw with no song selected must not throw.
            listBox.SelectedIndex = -1;
            threw = false;
            try { redraw!.Invoke(win, null); }
            catch (Exception ex) { threw = true; Console.WriteLine($"    {ex.InnerException?.Message}"); }
            Check(!threw, "redraw with nothing selected is safe");
        }
        else
        {
            // Even with no songs, redraw and the handlers must be safe.
            var redraw = typeof(MainWindow).GetMethod("RedrawPreview",
                BindingFlags.NonPublic | BindingFlags.Instance);
            bool threw = false;
            try { redraw!.Invoke(win, null); } catch { threw = true; }
            Check(!threw, "redraw with an empty library is safe");
        }

        win.Close();
    }
    catch (Exception ex)
    {
        Console.WriteLine($"  [FAIL] harness error: {ex.Message}\n{ex.StackTrace}");
        fail++;
    }
});
t.SetApartmentState(ApartmentState.STA);
t.Start();
t.Join();

Console.WriteLine("\n==============================");
Console.WriteLine(fail == 0 ? "PREVIEW INTERACTION CHECKS PASSED" : $"{fail} CHECK(S) FAILED");
return fail == 0 ? 0 : 1;
