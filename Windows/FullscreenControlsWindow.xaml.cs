using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace LiteVLC.Windows;

/// <summary>
/// Compact VLC-style floating control bar for Fullscreen mode.
/// Floats OVER the 1920x1080 D3D11 video without resizing VideoHost (zero edge shift!),
/// and calls Hide() when inactive so Windows DWM stays in flicker-free DirectFlip.
/// </summary>
public partial class FullscreenControlsWindow : Window
{
    private bool _suppressVolumeEvent = true;
    private bool _isScrubbing;
    private bool _hasDraggedSinceDown;
    private double _scrubStartX;
    private double _lastSentSeekTarget = double.NaN;
    private double _duration;
    private double _position;
    private double _cacheAhead;
    private long _lastScrubSeekMs;
    private bool _isShowing;

    private static readonly Geometry PlayGeo = Geometry.Parse("M 4,2 L 15,8.5 L 4,15 Z");
    private static readonly Geometry PauseGeo = Geometry.Parse("M 3,2 L 6.5,2 L 6.5,15 L 3,15 Z M 10.5,2 L 14,2 L 14,15 L 10.5,15 Z");
    private static readonly Geometry VolHighGeo = Geometry.Parse("M 2,7 L 6,7 L 11,3 L 11,15 L 6,11 L 2,11 Z M 14,6 C 15.5,7.5 15.5,10.5 14,12 M 16.5,4 C 19,6.8 19,11.2 16.5,14");
    private static readonly Geometry VolLowGeo = Geometry.Parse("M 2,7 L 6,7 L 11,3 L 11,15 L 6,11 L 2,11 Z M 14,6.5 C 15.3,7.8 15.3,10.2 14,11.5");
    private static readonly Geometry VolMuteGeo = Geometry.Parse("M 2,7 L 6,7 L 11,3 L 11,15 L 6,11 L 2,11 Z M 14,6 L 19,12 M 19,6 L 14,12");

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public bool IsPointerOverDock { get; private set; }
    public bool IsScrubbing => _isScrubbing;

    public event Action? PlayPauseRequested;
    public event Action? SeekBack10Requested;
    public event Action? SeekForward10Requested;
    public event Action? PrevRequested;
    public event Action? NextRequested;
    public event Action? MuteRequested;
    public event Action<double>? VolumeRequested;
    public event Action<double, bool>? SeekAbsoluteRequested;
    public event Action? SpeedCycleRequested;
    public event Action? AspectCycleRequested;
    public event Action? ExitFullscreenRequested;
    public event Action? HoverEntered;
    public event Action? HoverLeft;
    public event Action<KeyEventArgs>? KeyForwarded;

    public FullscreenControlsWindow()
    {
        InitializeComponent();
        _suppressVolumeEvent = false;
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public void PositionCenteredBottom(Rect hostBoundsDip)
    {
        double targetWidth = Math.Min(548, Math.Max(420, hostBoundsDip.Width * 0.44));
        Width = targetWidth;
        Left = hostBoundsDip.Left + (hostBoundsDip.Width - Width) / 2.0;
        // Sit flush at the very bottom edge so it stays completely below subtitles
        Top = hostBoundsDip.Bottom - Height - 2;
    }

    public void ShowAnimated()
    {
        _isShowing = true;
        if (!IsVisible)
        {
            DockCard.Opacity = 0;
            DockTranslate.Y = 12;
            Show();
        }
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        DockCard.BeginAnimation(OpacityProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        DockTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(0, TimeSpan.FromMilliseconds(170)) { EasingFunction = ease });
        UpdateSeekVisuals(_position, _duration, _cacheAhead);
    }

    public void HideAnimated(bool instant = false)
    {
        _isShowing = false;
        SeekHoverPopup.IsOpen = false;
        if (!IsVisible) return;

        if (instant)
        {
            DockCard.BeginAnimation(OpacityProperty, null);
            DockCard.Opacity = 0;
            Hide();
            return;
        }

        var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
        var fade = new DoubleAnimation(0.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        var slide = new DoubleAnimation(10, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
        fade.Completed += (_, _) =>
        {
            if (!_isShowing && IsVisible)
                Hide();
        };
        DockCard.BeginAnimation(OpacityProperty, fade);
        DockTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
    }

    public void SyncState(double position, double duration, double cacheAhead, bool paused, double volume, bool muted, double speed, string aspectShort)
    {
        _position = position;
        _duration = duration;
        _cacheAhead = cacheAhead;

        PlayPausePath.Data = paused ? PlayGeo : PauseGeo;
        PlayPausePath.Margin = paused ? new Thickness(2, 0, 0, 0) : new Thickness(0);

        _suppressVolumeEvent = true;
        VolumeSlider.Value = Math.Clamp(volume, 0, 200);
        _suppressVolumeEvent = false;

        if (muted || volume < 0.5) VolumeIconPath.Data = VolMuteGeo;
        else if (volume < 50) VolumeIconPath.Data = VolLowGeo;
        else VolumeIconPath.Data = VolHighGeo;

        SpeedBtn.Content = $"{speed:0.##}x";
        AspectBtn.Content = aspectShort;

        if (IsVisible && !_isScrubbing)
            UpdateSeekVisuals(position, duration, cacheAhead);
    }

    public void UpdatePosition(double position, double duration, double cacheAhead)
    {
        _position = position;
        _duration = duration;
        _cacheAhead = cacheAhead;
        if (IsVisible && !_isScrubbing)
            UpdateSeekVisuals(position, duration, cacheAhead);
    }

    private void UpdateSeekVisuals(double position, double duration, double cacheAhead)
    {
        TimeReadoutText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";
        double w = SeekTrackSurface.ActualWidth;
        if (w <= 0 || duration <= 0)
        {
            SeekFillScale.ScaleX = 0;
            SeekCacheScale.ScaleX = 0;
            return;
        }

        double ratio = Math.Clamp(position / duration, 0, 1);
        double cacheRatio = Math.Clamp((position + Math.Max(0, cacheAhead)) / duration, ratio, 1);
        SeekFillScale.ScaleX = ratio;
        SeekCacheScale.ScaleX = cacheRatio;
        SeekThumbTranslate.X = Math.Clamp((ratio * w) - (SeekThumb.Width / 2.0), 0, Math.Max(0, w - SeekThumb.Width));
    }

    private double GetTimeAtMouse(MouseEventArgs e)
    {
        double w = SeekTrackSurface.ActualWidth;
        if (w <= 0 || _duration <= 0) return 0;
        double x = Math.Clamp(e.GetPosition(SeekTrackSurface).X, 0, w);
        return (x / w) * _duration;
    }

    private void SeekTrack_MouseEnter(object sender, MouseEventArgs e)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SeekBarHeightScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.65, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
        SeekThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
        SeekThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
    }

    private void SeekTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (_duration <= 0) return;
        _isScrubbing = true;
        _hasDraggedSinceDown = false;
        _scrubStartX = e.GetPosition(SeekTrackSurface).X;
        SeekTrackSurface.CaptureMouse();

        double target = GetTimeAtMouse(e);
        _lastSentSeekTarget = target;
        UpdateSeekVisuals(target, _duration, _cacheAhead);
        SeekAbsoluteRequested?.Invoke(target, false);
        _lastScrubSeekMs = Environment.TickCount64;
    }

    private void SeekTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (_duration <= 0) return;
        double w = SeekTrackSurface.ActualWidth;
        double x = Math.Clamp(e.GetPosition(SeekTrackSurface).X, 0, w);
        double hoverSec = (x / Math.Max(1, w)) * _duration;

        SeekHoverTimeText.Text = FormatTime(hoverSec);
        SeekHoverPopup.HorizontalOffset = Math.Clamp(x - 24, 0, Math.Max(0, w - 54));
        SeekHoverPopup.VerticalOffset = -28;
        SeekHoverPopup.IsOpen = true;

        if (_isScrubbing)
        {
            if (!_hasDraggedSinceDown && Math.Abs(x - _scrubStartX) >= 3.0)
                _hasDraggedSinceDown = true;

            if (!_hasDraggedSinceDown) return;

            UpdateSeekVisuals(hoverSec, _duration, _cacheAhead);
            long now = Environment.TickCount64;
            if (now - _lastScrubSeekMs >= 85 && (double.IsNaN(_lastSentSeekTarget) || Math.Abs(hoverSec - _lastSentSeekTarget) >= 0.35))
            {
                _lastScrubSeekMs = now;
                _lastSentSeekTarget = hoverSec;
                SeekAbsoluteRequested?.Invoke(hoverSec, true);
            }
        }
    }

    private void SeekTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;
        SeekTrackSurface.ReleaseMouseCapture();

        if (_hasDraggedSinceDown)
        {
            double target = GetTimeAtMouse(e);
            if (double.IsNaN(_lastSentSeekTarget) || Math.Abs(target - _lastSentSeekTarget) >= 0.25)
            {
                _lastSentSeekTarget = target;
                SeekAbsoluteRequested?.Invoke(target, false);
            }
            UpdateSeekVisuals(target, _duration, _cacheAhead);
        }
        _hasDraggedSinceDown = false;
    }

    private void SeekTrack_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isScrubbing)
        {
            SeekHoverPopup.IsOpen = false;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            SeekBarHeightScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            SeekThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
            SeekThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(150)) { EasingFunction = ease });
        }
    }

    private void Window_MouseEnter(object sender, MouseEventArgs e)
    {
        IsPointerOverDock = true;
        HoverEntered?.Invoke();
    }

    private void Window_MouseLeave(object sender, MouseEventArgs e)
    {
        IsPointerOverDock = false;
        HoverLeft?.Invoke();
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e) => KeyForwarded?.Invoke(e);

    private void PlayPause_Click(object sender, RoutedEventArgs e) => PlayPauseRequested?.Invoke();
    private void SeekBack10_Click(object sender, RoutedEventArgs e) => SeekBack10Requested?.Invoke();
    private void SeekForward10_Click(object sender, RoutedEventArgs e) => SeekForward10Requested?.Invoke();
    private void Prev_Click(object sender, RoutedEventArgs e) => PrevRequested?.Invoke();
    private void Next_Click(object sender, RoutedEventArgs e) => NextRequested?.Invoke();
    private void Mute_Click(object sender, RoutedEventArgs e) => MuteRequested?.Invoke();
    private void Speed_Click(object sender, RoutedEventArgs e) => SpeedCycleRequested?.Invoke();
    private void Aspect_Click(object sender, RoutedEventArgs e) => AspectCycleRequested?.Invoke();
    private void ExitFullscreen_Click(object sender, RoutedEventArgs e) => ExitFullscreenRequested?.Invoke();

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolumeEvent || !IsLoaded) return;
        VolumeRequested?.Invoke(e.NewValue);
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0)
            return "00:00";
        var ts = TimeSpan.FromSeconds(seconds);
        return ts.TotalHours >= 1
            ? $"{(int)ts.TotalHours}:{ts.Minutes:D2}:{ts.Seconds:D2}"
            : $"{ts.Minutes:D2}:{ts.Seconds:D2}";
    }
}
