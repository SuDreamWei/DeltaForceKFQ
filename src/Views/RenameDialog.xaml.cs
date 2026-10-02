using System.Windows;

namespace DeltaHarmonica.Views;

public partial class RenameDialog : Window
{
    public string NewTitle { get; private set; } = string.Empty;

    public RenameDialog(string current)
    {
        InitializeComponent();
        NameBox.Text = current;
        NewTitle = current;

        Loaded += (_, __) =>
        {
            NameBox.Focus();
            NameBox.SelectAll();
        };
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        var t = NameBox.Text.Trim();
        if (t.Length == 0)
        {
            MessageBox.Show(this, "名称不能为空。", "提示", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        NewTitle = t;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => DialogResult = false;
}
