using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LiteVLC.Services;
using Microsoft.Win32;

namespace LiteVLC.Windows;

public partial class TrimVideoDialog : Window
{
    private readonly string _videoPath;
    private readonly double _totalDuration;
    private readonly Func<double> _getCurrentPos;
    private CancellationTokenSource? _cts;
    private bool _isBusy;
    private string? _savedClipPath;

    public string? SavedClipPath => _savedClipPath;

    public TrimVideoDialog(string videoPath, double totalDuration, Func<double> getCurrentPos)
    {
        _videoPath = videoPath;
        _totalDuration = Math.Max(0, totalDuration);
        _getCurrentPos = getCurrentPos;

        InitializeComponent();

        FileNameText.Text = Path.GetFileName(videoPath);
        double cur = Math.Clamp(getCurrentPos(), 0, _totalDuration);
        double end = _totalDuration > 0 ? Math.Min(_totalDuration, cur + 30) : cur + 30;
        if (end <= cur + 1 && _totalDuration > 2)
        {
            cur = Math.Max(0, _totalDuration - 30);
            end = _totalDuration;
        }

        StartBox.Text = FormatHms(cur);
        EndBox.Text = FormatHms(end);

        bool hasFfmpeg = VideoTrimService.IsSystemFfmpegAvailable();
        if (!hasFfmpeg)
        {
            FastCopyRadio.IsEnabled = false;
            FastCopyRadio.Content = "Instant Lossless Stream Copy (requires ffmpeg on PATH)";
        }

        Loaded += (_, _) => UpdateRangeVisual();
    }

    private void UpdateRangeVisual()
    {
        if (!IsLoaded) return;
        bool okS = TryParseTime(StartBox.Text, out double s);
        bool okE = TryParseTime(EndBox.Text, out double e);

        if (!okS || !okE || e <= s)
        {
            RangeSummaryText.Text = "Enter valid Start < End times";
            ClipDurationText.Text = "Invalid";
            ClipDurationText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            CutBtn.IsEnabled = false;
            return;
        }

        if (_totalDuration > 0)
        {
            s = Math.Clamp(s, 0, _totalDuration);
            e = Math.Clamp(e, 0, _totalDuration);
        }

        double len = Math.Max(0, e - s);
        RangeSummaryText.Text = $"Selected Clip: {FormatHms(s)} → {FormatHms(e)}";
        ClipDurationText.Text = $"Duration: {FormatHms(len)}";
        ClipDurationText.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8B, 0x42));
        CutBtn.IsEnabled = !_isBusy && len >= 0.2;

        double trackW = RangeBarTrack.ActualWidth;
        if (trackW > 0 && _totalDuration > 0)
        {
            double left = (s / _totalDuration) * trackW;
            double width = Math.Max(6, ((e - s) / _totalDuration) * trackW);
            RangeHighlight.Margin = new Thickness(left, 0, 0, 0);
            RangeHighlight.Width = Math.Min(width, Math.Max(0, trackW - left));
        }
    }

    private void TimeBox_TextChanged(object sender, TextChangedEventArgs e) => UpdateRangeVisual();

    private void SetStartCurrent_Click(object sender, RoutedEventArgs e)
    {
        StartBox.Text = FormatHms(_getCurrentPos());
        UpdateRangeVisual();
    }

    private void SetEndCurrent_Click(object sender, RoutedEventArgs e)
    {
        EndBox.Text = FormatHms(_getCurrentPos());
        UpdateRangeVisual();
    }

    private async void Cut_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        if (!TryParseTime(StartBox.Text, out double start) || !TryParseTime(EndBox.Text, out double end) || end <= start)
            return;

        string baseName = Path.GetFileNameWithoutExtension(_videoPath);
        string srcExt = Path.GetExtension(_videoPath);
        bool lossless = FastCopyRadio.IsChecked == true;
        string defaultExt = lossless && !string.IsNullOrWhiteSpace(srcExt) ? srcExt : ".mp4";

        var dlg = new SaveFileDialog
        {
            Title = "Save Trimmed Clip",
            InitialDirectory = Path.GetDirectoryName(_videoPath) ?? Environment.GetFolderPath(Environment.SpecialFolder.MyVideos),
            FileName = $"{baseName}_clip_{FormatFileStamp(start)}_{FormatFileStamp(end)}{defaultExt}",
            Filter = "MP4 Video|*.mp4|MKV Video|*.mkv|MP3 Audio|*.mp3|All Files|*.*"
        };

        if (dlg.ShowDialog(this) != true) return;

        _isBusy = true;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();

        CutBtn.IsEnabled = false;
        OpenFolderBtn.Visibility = Visibility.Collapsed;
        CloseBtn.Content = "Cancel";
        ProgressGrid.Visibility = Visibility.Visible;
        ProgressScale.ScaleX = 0;
        PctText.Text = "0%";
        StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
        StatusText.Text = lossless ? "Performing lossless stream copy..." : "Cutting frame-exact clip...";

        try
        {
            var progress = new Progress<double>(p =>
            {
                ProgressScale.ScaleX = Math.Clamp(p, 0, 1);
                PctText.Text = $"{p * 100:0}%";
            });

            await VideoTrimService.TrimAsync(_videoPath, dlg.FileName, start, end, lossless, progress, _cts.Token);

            _savedClipPath = dlg.FileName;
            ProgressScale.ScaleX = 1;
            PctText.Text = "100%";
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
            StatusText.Text = $"Saved: {Path.GetFileName(dlg.FileName)}";
            OpenFolderBtn.Visibility = Visibility.Visible;
            CloseBtn.Content = "Done";
        }
        catch (OperationCanceledException)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            StatusText.Text = "Trim cancelled.";
            PctText.Text = "";
            CloseBtn.Content = "Close";
        }
        catch (Exception ex)
        {
            StatusText.Foreground = new SolidColorBrush(Color.FromRgb(0xF8, 0x71, 0x71));
            StatusText.Text = $"Error: {ex.Message}";
            PctText.Text = "";
            CloseBtn.Content = "Close";
        }
        finally
        {
            _isBusy = false;
            CutBtn.IsEnabled = true;
        }
    }

    private void OpenFolder_Click(object sender, RoutedEventArgs e)
    {
        if (!string.IsNullOrWhiteSpace(_savedClipPath) && File.Exists(_savedClipPath))
            Process.Start("explorer.exe", $"/select,\"{_savedClipPath}\"");
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
        DialogResult = !string.IsNullOrWhiteSpace(_savedClipPath);
        Close();
    }

    private static bool TryParseTime(string input, out double seconds)
    {
        seconds = 0;
        if (string.IsNullOrWhiteSpace(input)) return false;
        string s = input.Trim();

        if (double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double plain) && plain >= 0)
        {
            seconds = plain;
            return true;
        }

        string[] parts = s.Split(':');
        if (parts.Length == 2 &&
            int.TryParse(parts[0], out int m) &&
            double.TryParse(parts[1], NumberStyles.Float, CultureInfo.InvariantCulture, out double sec) &&
            m >= 0 && sec >= 0 && sec < 60)
        {
            seconds = m * 60 + sec;
            return true;
        }

        if (parts.Length == 3 &&
            int.TryParse(parts[0], out int h) &&
            int.TryParse(parts[1], out int m3) &&
            double.TryParse(parts[2], NumberStyles.Float, CultureInfo.InvariantCulture, out double s3) &&
            h >= 0 && m3 >= 0 && m3 < 60 && s3 >= 0 && s3 < 60)
        {
            seconds = h * 3600 + m3 * 60 + s3;
            return true;
        }

        return false;
    }

    private static string FormatHms(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "00:00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2}";
    }

    private static string FormatFileStamp(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours:D2}-{ts.Minutes:D2}-{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}-{ts.Seconds:D2}";
    }
}
