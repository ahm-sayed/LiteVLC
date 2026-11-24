using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Threading;
using LiteVLC.Models;

namespace LiteVLC.Windows;

/// <summary>
/// Click-through translucent OSD / HUD overlay window.
/// Automatically hides itself completely when idle so Windows DWM keeps the D3D11 video
/// in flicker-free DirectFlip mode during normal playback.
/// </summary>
public partial class OsdOverlayWindow : Window
{
    private readonly DispatcherTimer _osdHideTimer;
    private readonly DispatcherTimer _skipHideTimer;
    private bool _osdActive;
    private bool _skipActive;

    private const int GWL_EXSTYLE = -20;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;

    [DllImport("user32.dll")]
    private static extern int GetWindowLong(IntPtr hwnd, int index);

    [DllImport("user32.dll")]
    private static extern int SetWindowLong(IntPtr hwnd, int index, int newStyle);

    public OsdOverlayWindow()
    {
        InitializeComponent();

        _osdHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1150) };
        _osdHideTimer.Tick += (_, _) =>
        {
            _osdHideTimer.Stop();
            var ease = new CubicEase { EasingMode = EasingMode.EaseIn };
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
            var slide = new DoubleAnimation(-8, TimeSpan.FromMilliseconds(200)) { EasingFunction = ease };
            fade.Completed += (_, _) =>
            {
                _osdActive = false;
                HideIfCompletelyIdle();
            };
            OsdPill.BeginAnimation(OpacityProperty, fade);
            OsdTranslate.BeginAnimation(TranslateTransform.YProperty, slide);
        };

        _skipHideTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(750) };
        _skipHideTimer.Tick += (_, _) =>
        {
            _skipHideTimer.Stop();
            var fade = new DoubleAnimation(0, TimeSpan.FromMilliseconds(200));
            fade.Completed += (_, _) =>
            {
                _skipActive = false;
                HideIfCompletelyIdle();
            };
            LeftSkipBadge.BeginAnimation(OpacityProperty, fade);
            RightSkipBadge.BeginAnimation(OpacityProperty, fade);
        };
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        var hwnd = new WindowInteropHelper(this).Handle;
        int exStyle = GetWindowLong(hwnd, GWL_EXSTYLE);
        SetWindowLong(hwnd, GWL_EXSTYLE, exStyle | WS_EX_TRANSPARENT | WS_EX_NOACTIVATE | WS_EX_TOOLWINDOW);
    }

    public bool IsHudVisible => HudPanel.Visibility == Visibility.Visible;
    public bool IsOverlayNeeded => _osdActive || _skipActive || IsHudVisible;

    private void EnsureVisible()
    {
        if (!IsVisible)
            Show();
    }

    private void HideIfCompletelyIdle()
    {
        if (!IsOverlayNeeded && IsVisible)
            Hide();
    }

    public void ShowOsd(string message, string? sub = null, int durationMs = 1150)
    {
        _osdActive = true;
        EnsureVisible();

        OsdTitle.Text = message;
        OsdSub.Text = sub ?? "";
        OsdSub.Visibility = string.IsNullOrWhiteSpace(sub) ? Visibility.Collapsed : Visibility.Visible;

        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        OsdPill.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(130)) { EasingFunction = ease });
        OsdTranslate.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(-10, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });

        _osdHideTimer.Stop();
        _osdHideTimer.Interval = TimeSpan.FromMilliseconds(durationMs);
        _osdHideTimer.Start();
    }

    public void ShowSkipIndicator(double deltaSeconds, string targetTimeText)
    {
        _skipActive = true;
        EnsureVisible();

        bool forward = deltaSeconds >= 0;
        string label = forward ? $"+{Math.Round(deltaSeconds)}s" : $"{Math.Round(deltaSeconds)}s";
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (forward)
        {
            LeftSkipBadge.BeginAnimation(OpacityProperty, null);
            LeftSkipBadge.Opacity = 0;

            RightSkipText.Text = label;
            RightSkipTime.Text = targetTimeText;
            RightSkipBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
            RightSkipTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(-12, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            RightSkipScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            RightSkipScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        }
        else
        {
            RightSkipBadge.BeginAnimation(OpacityProperty, null);
            RightSkipBadge.Opacity = 0;

            LeftSkipText.Text = label;
            LeftSkipTime.Text = targetTimeText;
            LeftSkipBadge.BeginAnimation(OpacityProperty, new DoubleAnimation(1, TimeSpan.FromMilliseconds(100)));
            LeftSkipTranslate.BeginAnimation(TranslateTransform.XProperty, new DoubleAnimation(12, 0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            LeftSkipScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            LeftSkipScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.92, 1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        }

        _skipHideTimer.Stop();
        _skipHideTimer.Start();
    }

    public void SetHudVisible(bool visible)
    {
        HudPanel.Visibility = visible ? Visibility.Visible : Visibility.Collapsed;
        if (visible)
            EnsureVisible();
        else
            HideIfCompletelyIdle();
    }

    public void UpdateHud(PlaybackStats stats)
    {
        if (HudPanel.Visibility != Visibility.Visible) return;
        HudVideoText.Text = $"{stats.VideoCodec} ({stats.Resolution})";
        HudAudioText.Text = stats.AudioCodec;
        HudHwDecText.Text = stats.HwDec;
        HudFpsText.Text = stats.Fps > 0 ? $"{stats.Fps:0.00}" : "—";
        HudCacheText.Text = $"{stats.CacheSeconds:0.1} s buffered";
        HudAvSyncText.Text = $"{stats.AvSyncMs:+0.0;-0.0;0.0} ms";
        string mode = stats.FastSeekEnabled ? "Fast" : "Exact";
        HudSeekText.Text = stats.LastSeekMs > 0 ? $"{stats.LastSeekMs:0.0} ms ({mode})" : $"Ready ({mode})";
        HudDroppedText.Text = stats.DroppedFrames.ToString();
    }
}
