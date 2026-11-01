using Avalonia.Controls;
using Avalonia.Interactivity;

namespace G_write;

public partial class AboutWindow : Window
{
    public AboutWindow()
    {
        InitializeComponent();
        var lbl = this.FindControl<TextBlock>("lblVersion");
        if (lbl != null)
        {
            lbl.Text = $"Версия: {AppVersion.DisplayString} (Cross-Platform)";
        }
    }

    private void CloseButton_Click(object? sender, RoutedEventArgs e)
    {
        Close();
    }
}
