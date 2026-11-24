using System.Windows;
using System.Windows.Input;

namespace LiteVLC.Windows;

public partial class OpenUrlDialog : Window
{
    public string StreamUrl { get; private set; } = "";

    public OpenUrlDialog()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            if (Clipboard.ContainsText())
            {
                string clip = Clipboard.GetText().Trim();
                if (clip.StartsWith("http://", System.StringComparison.OrdinalIgnoreCase) ||
                    clip.StartsWith("https://", System.StringComparison.OrdinalIgnoreCase) ||
                    clip.StartsWith("rtsp://", System.StringComparison.OrdinalIgnoreCase))
                {
                    UrlBox.Text = clip;
                    UrlBox.SelectAll();
                }
            }
            UrlBox.Focus();
        };
    }

    private void UrlBox_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter) Play_Click(sender, e);
        else if (e.Key == Key.Escape) Cancel_Click(sender, e);
    }

    private void Play_Click(object sender, RoutedEventArgs e)
    {
        string url = UrlBox.Text.Trim();
        if (string.IsNullOrWhiteSpace(url)) return;
        StreamUrl = url;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}
