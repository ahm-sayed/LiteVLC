using System;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using LiteVLC.Services;

namespace LiteVLC.Windows;

public partial class UpdateDialog : Window
{
    private readonly AutoUpdaterService _updater = new();
    private UpdateCheckResult? _result;
    private CancellationTokenSource? _cts;
    private bool _isDownloading;

    public bool UpdateLaunched { get; private set; }

    public UpdateDialog(UpdateCheckResult? preloaded = null)
    {
        _result = preloaded;
        InitializeComponent();

        Loaded += async (_, _) =>
        {
            if (_result != null)
                ApplyCheckResult(_result);
            else
                await CheckNowAsync();
        };
    }

    private async Task CheckNowAsync()
    {
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        HeadlineText.Text = "Checking for updates...";
        VersionLineText.Text = $"Current version: v{AutoUpdaterService.GetCurrentVersion()}";
        StatusText.Text = "Checking github.com/ahm-sayed/LiteVLC/releases...";
        NotesText.Text = "";
        UpdateBtn.Visibility = Visibility.Collapsed;

        var res = await _updater.CheckForUpdateAsync(_cts.Token);
        _result = res;
        ApplyCheckResult(res);
    }

    private void ApplyCheckResult(UpdateCheckResult res)
    {
        VersionLineText.Text = $"Installed: v{res.CurrentVersion}   •   Latest on GitHub: v{res.LatestVersion}";

        if (!string.IsNullOrWhiteSpace(res.ErrorMessage))
        {
            HeadlineText.Text = "Could not check for updates";
            StatusText.Text = $"Error: {res.ErrorMessage}";
            NotesText.Text = "Check your internet connection and try again.";
            UpdateBtn.Visibility = Visibility.Collapsed;
            return;
        }

        if (res.UpdateAvailable)
        {
            double mb = res.InstallerSizeBytes > 0 ? res.InstallerSizeBytes / (1024.0 * 1024.0) : 0;
            HeadlineText.Text = $"Update Available: {res.TagName}";
            StatusBanner.Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x26, 0x1A));
            StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x5E, 0x3F));
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            StatusText.Text = mb > 0
                ? $"New version ready ({mb:0.1} MB). Click 'Update Now' to download, install silently, and restart automatically."
                : "New version ready. Click 'Update Now' to install and restart automatically.";
            NotesText.Text = string.IsNullOrWhiteSpace(res.ReleaseNotes) ? "Bug fixes and improvements." : res.ReleaseNotes;
            UpdateBtn.Visibility = Visibility.Visible;
            UpdateBtn.IsEnabled = true;
        }
        else
        {
            HeadlineText.Text = "You are up to date!";
            StatusBanner.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x20, 0x29));
            StatusBanner.BorderBrush = new SolidColorBrush(Color.FromRgb(0x23, 0x52, 0x6B));
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
            StatusText.Text = $"LiteVLC v{res.CurrentVersion} is already the latest release on GitHub.";
            NotesText.Text = string.IsNullOrWhiteSpace(res.ReleaseNotes) ? "" : res.ReleaseNotes;
            UpdateBtn.Visibility = Visibility.Collapsed;
        }
    }

    private async void UpdateNow_Click(object sender, RoutedEventArgs e)
    {
        if (_result == null || !_result.UpdateAvailable || _isDownloading) return;

        _isDownloading = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        UpdateBtn.IsEnabled = false;
        CloseBtn.Content = "Cancel";
        ProgressPanel.Visibility = Visibility.Visible;
        ProgressScale.ScaleX = 0;
        ProgressPctText.Text = "0%";
        ProgressStatusText.Text = $"Downloading {_result.InstallerFileName}...";

        try
        {
            var progress = new Progress<(long Downloaded, long Total)>(p =>
            {
                double dlMb = p.Downloaded / (1024.0 * 1024.0);
                if (p.Total > 0)
                {
                    double totMb = p.Total / (1024.0 * 1024.0);
                    double ratio = Math.Clamp((double)p.Downloaded / p.Total, 0, 1);
                    ProgressScale.ScaleX = ratio;
                    ProgressPctText.Text = $"{ratio * 100:0}%";
                    ProgressStatusText.Text = $"Downloading {dlMb:0.1} MB / {totMb:0.1} MB";
                }
                else
                {
                    ProgressStatusText.Text = $"Downloading {dlMb:0.1} MB...";
                }
            });

            string installerPath = await _updater.DownloadInstallerAsync(_result, progress, _cts.Token);

            ProgressScale.ScaleX = 1;
            ProgressPctText.Text = "100%";
            ProgressStatusText.Text = "Installing update & restarting LiteVLC...";

            AutoUpdaterService.LaunchSilentInstallAndRelaunch(installerPath);
            UpdateLaunched = true;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            _isDownloading = false;
            ProgressPanel.Visibility = Visibility.Collapsed;
            UpdateBtn.IsEnabled = true;
            CloseBtn.Content = "Close";
            StatusText.Text = "Download cancelled.";
        }
        catch (Exception ex)
        {
            _isDownloading = false;
            UpdateBtn.IsEnabled = true;
            CloseBtn.Content = "Close";
            ProgressPanel.Visibility = Visibility.Collapsed;
            StatusText.Text = $"Download failed: {ex.Message}";
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }
}
