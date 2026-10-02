using System.Windows;
using System.Windows.Threading;
using DeltaHarmonica.Views;

namespace DeltaHarmonica;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += OnUnhandled;
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            if (args.ExceptionObject is Exception ex) ShowFatal(ex);
        };

        var win = new MainWindow();
        MainWindow = win;
        win.Show();
    }

    private void OnUnhandled(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        ShowFatal(e.Exception);
        e.Handled = true;
    }

    private static void ShowFatal(Exception ex)
    {
        MessageBox.Show(
            "程序遇到一个未处理的错误：\n\n" + ex.Message + "\n\n" + ex.StackTrace,
            "错误", MessageBoxButton.OK, MessageBoxImage.Error);
    }
}
