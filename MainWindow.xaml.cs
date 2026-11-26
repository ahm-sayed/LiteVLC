using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Windows.Threading;
using LiteVLC.Controls;
using LiteVLC.Engine;
using LiteVLC.Models;
using LiteVLC.Services;
using LiteVLC.Windows;
using Microsoft.Win32;

namespace LiteVLC;

public partial class MainWindow : Window
{
    private readonly string[] _startupArgs;
    private readonly MpvEngine _engine = new();
    private readonly VideoHost _videoHost = new();
    private readonly ObservableCollection<PlaylistItem> _playlist = new();
    private readonly AppPersistedState _state;
    private readonly AutoUpdaterService _updater = new();
    private UpdateCheckResult? _cachedUpdateResult;

    private OsdOverlayWindow? _osdOverlay;
    private FullscreenControlsWindow? _fsControls;
    private readonly DispatcherTimer _hudTimer;
    private readonly DispatcherTimer _fsHideTimer;
    private readonly DispatcherTimer _fsMousePollTimer;
    private readonly DispatcherTimer _clickTimer;
    private readonly DispatcherTimer _autoSaveTimer;
    private readonly DispatcherTimer _autoSyncDebounceTimer;

    private CancellationTokenSource? _subSyncCts;
    private bool _isSubSyncChecking;
    private string? _lastCheckedSubKey;
    private long _lastSingleClickPauseTick;
    private bool _wasPausedBeforeSingleClick;
    private HwndSource? _hwndSource;

    private int _fsLastCursorX;
    private int _fsLastCursorY;

    private int _currentIndex = -1;
    private LoopMode _loopMode = LoopMode.Off;
    private bool _isFullscreen;
    private bool _isPlaylistOpen;
    private long _fsIgnoreMoveUntil;
    private WindowState _preFsWindowState;
    private RECT _preFsWinRect;
    private WindowChrome? _savedWindowChrome;
    private bool _suppressVolumeEvent = true;

    // Seek bar scrubbing state
    private bool _isScrubbing;
    private bool _hasDraggedSinceDown;
    private double _scrubStartX;
    private double _scrubTargetSeconds;
    private double _lastSentSeekTarget = double.NaN;
    private long _lastScrubSeekMs;

    // Vector path geometries (zero emoji artifacts, crisp at every DPI)
    private static readonly Geometry PlayGeo = Geometry.Parse("M 4,2 L 15,8.5 L 4,15 Z");
    private static readonly Geometry PauseGeo = Geometry.Parse("M 3,2 L 6.5,2 L 6.5,15 L 3,15 Z M 10.5,2 L 14,2 L 14,15 L 10.5,15 Z");
    private static readonly Geometry VolHighGeo = Geometry.Parse("M 2,7 L 6,7 L 11,3 L 11,15 L 6,11 L 2,11 Z M 14,6 C 15.5,7.5 15.5,10.5 14,12 M 16.5,4 C 19,6.8 19,11.2 16.5,14");
    private static readonly Geometry VolLowGeo = Geometry.Parse("M 2,7 L 6,7 L 11,3 L 11,15 L 6,11 L 2,11 Z M 14,6.5 C 15.3,7.8 15.3,10.2 14,11.5");
    private static readonly Geometry VolMuteGeo = Geometry.Parse("M 2,7 L 6,7 L 11,3 L 11,15 L 6,11 L 2,11 Z M 14,6 L 19,12 M 19,6 L 14,12");
    private static readonly Geometry MaxGeo = Geometry.Parse("M 0.5,0.5 L 9.5,0.5 L 9.5,9.5 L 0.5,9.5 Z");
    private static readonly Geometry RestoreGeo = Geometry.Parse("M 0.5,2.5 L 7.5,2.5 L 7.5,9.5 L 0.5,9.5 Z M 2.5,2.5 L 2.5,0.5 L 9.5,0.5 L 9.5,7.5 L 7.5,7.5");
    private static readonly Geometry FsEnterGeo = Geometry.Parse("M 2,6 L 2,2 L 6,2 M 12,2 L 16,2 L 16,6 M 16,12 L 16,16 L 12,16 M 6,16 L 2,16 L 2,12");
    private static readonly Geometry FsExitGeo = Geometry.Parse("M 6,2 L 6,6 L 2,6 M 12,2 L 12,6 L 16,6 M 16,12 L 12,12 L 12,16 M 2,12 L 6,12 L 6,16");

    // Win32 True Fullscreen P/Invoke (covers Windows Taskbar completely on any monitor/DPI)
    private const uint MONITOR_DEFAULTTONEAREST = 2;
    private const uint SWP_FRAMECHANGED = 0x0020;
    private const uint SWP_SHOWWINDOW = 0x0040;
    private const uint SWP_NOZORDER = 0x0004;
    private static readonly IntPtr HWND_TOP = IntPtr.Zero;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT
    {
        public int Left, Top, Right, Bottom;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO
    {
        public int cbSize;
        public RECT rcMonitor;
        public RECT rcWork;
        public uint dwFlags;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT
    {
        public int X, Y;
    }

    [DllImport("user32.dll")]
    private static extern bool GetCursorPos(out POINT lpPoint);

    [DllImport("user32.dll")]
    private static extern uint GetDoubleClickTime();

    private const int WM_DEVICECHANGE = 0x0219;
    private const int DBT_DEVNODES_CHANGED = 0x0007;

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint dwFlags);

    [DllImport("user32.dll", CharSet = CharSet.Auto)]
    private static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO lpmi);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowPos(IntPtr hWnd, IntPtr hWndInsertAfter, int X, int Y, int cx, int cy, uint uFlags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT lpRect);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private const int DWMWA_USE_IMMERSIVE_DARK_MODE = 20;

    // Aspect / Fill modes (includes Crop-to-Fill & Stretch to eliminate black bars)
    private static readonly (string Label, string ShortLabel, string Value, bool KeepAspect, double PanScan)[] AspectModes =
    [
        ("Fit (Original)", "Fit", "-1", true, 0.0),
        ("Fill (No Black Bars)", "Fill", "-1", true, 1.0),
        ("Stretch (Full Screen)", "Stretch", "-1", false, 0.0),
        ("16:9 Widescreen", "16:9", "16:9", true, 0.0),
        ("4:3 Classic", "4:3", "4:3", true, 0.0),
        ("21:9 Cinema", "21:9", "21:9", true, 0.0)
    ];
    private int _aspectIndex;

    private static readonly HashSet<string> SubtitleExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".srt", ".ass", ".ssa", ".vtt", ".sub", ".idx", ".sup", ".lrc"
    };

    private static readonly HashSet<string> MediaExtensions = new(StringComparer.OrdinalIgnoreCase)
    {
        ".mp4", ".mkv", ".avi", ".webm", ".mov", ".wmv", ".flv", ".m4v", ".ts", ".m2ts", ".mpg", ".mpeg",
        ".3gp", ".ogv", ".vob", ".rmvb", ".asf",
        ".mp3", ".flac", ".wav", ".ogg", ".opus", ".aac", ".m4a", ".wma", ".alac", ".m3u", ".m3u8"
    };

    public MainWindow(string[] args)
    {
        _startupArgs = args;
        _state = StateStore.Load();

        InitializeComponent();
        _savedWindowChrome = WindowChrome.GetWindowChrome(this);
        PlaylistListBox.ItemsSource = _playlist;

        _hudTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
        _hudTimer.Tick += (_, _) =>
        {
            if (_osdOverlay is { IsHudVisible: true })
                _osdOverlay.UpdateHud(_engine.GetStatsSnapshot());
        };

        _fsHideTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2.0) };
        _fsHideTimer.Tick += (_, _) =>
        {
            _fsHideTimer.Stop();
            bool overFloating = _fsControls is { IsPointerOverDock: true } || _fsControls is { IsScrubbing: true };
            if (_isFullscreen && !_isScrubbing && !overFloating && _engine.HasMedia && !_engine.IsPaused)
                HideFullscreenControls();
        };

        // Hardware cursor watcher (active ONLY in Fullscreen) — works reliably even over mpv's child HWND
        // and is 100% immune to synthetic WM_MOUSEMOVE events!
        _fsMousePollTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(90) };
        _fsMousePollTimer.Tick += (_, _) =>
        {
            if (!_isFullscreen || !GetCursorPos(out POINT pt)) return;
            int dx = Math.Abs(pt.X - _fsLastCursorX);
            int dy = Math.Abs(pt.Y - _fsLastCursorY);
            if (dx >= 8 || dy >= 8)
            {
                _fsLastCursorX = pt.X;
                _fsLastCursorY = pt.Y;
                OnFullscreenActivity();
            }
        };

        // Periodic resume position persistence every 5 seconds so scene is never lost
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _autoSaveTimer.Tick += (_, _) =>
        {
            if (_state.RememberPosition && _engine.HasMedia && !_engine.IsPaused)
            {
                SaveCurrentResumePosition();
                StateStore.Save(_state);
            }
        };

        // Debounce automatic subtitle-to-audio sync check after FileLoaded / SubtitleTrackChanged
        _autoSyncDebounceTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(700) };
        _autoSyncDebounceTimer.Tick += (_, _) =>
        {
            _autoSyncDebounceTimer.Stop();
            if (_state.AutoSyncSubtitles)
                _ = RunSubtitleSyncCheckAsync(isManual: false);
        };

        // Distinguish single-click (Play/Pause) from double-click (instant Fullscreen without pausing!)
        _clickTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(320) };
        _clickTimer.Tick += (_, _) =>
        {
            _clickTimer.Stop();
            if (_engine.HasMedia)
            {
                bool wasPaused = _engine.IsPaused;
                _wasPausedBeforeSingleClick = wasPaused;
                _lastSingleClickPauseTick = Environment.TickCount64;
                _engine.TogglePause();
                ShowOsd(wasPaused ? "Playing" : "Paused", FormatTime(_engine.Position));
            }
        };

        HookVideoHostEvents();
        HookEngineEvents();
        ApplyInitialUiState();
    }

    private void ApplyInitialUiState()
    {
        _suppressVolumeEvent = true;
        VolumeSlider.Value = Math.Clamp(_state.Volume, 0, 200);
        VolumeText.Text = $"{Math.Round(VolumeSlider.Value)}%";
        _suppressVolumeEvent = false;

        UpdateFastSeekBadge(_state.FastSeek);
        UpdateResumeBadge(_state.RememberPosition);
        UpdateAudioBoostBadge(_state.AudioBoost);
        UpdateAutoSyncBadge(_state.AutoSyncSubtitles);
        SetSubSyncButtonState(SubSyncUiState.Idle);
        UpdateBtn.ToolTip = $"LiteVLC v{AutoUpdaterService.GetCurrentVersion()} — Check for Updates on GitHub";
        RefreshRecentFilesUi();
    }

    private void HookVideoHostEvents()
    {
        _videoHost.VideoSingleClicked += () =>
        {
            Activate();
            Focus();
            _clickTimer.Stop();
            _clickTimer.Start();
        };

        _videoHost.VideoDoubleClicked += () =>
        {
            _clickTimer.Stop();

            // If a casual slower double-click (>320ms, <= OS double-click time) already fired the single-click
            // pause toggle right before click #2 arrived, seamlessly revert it so double-click NEVER pauses playback!
            long dblLimit = Math.Max((long)GetDoubleClickTime() + 250, 750);
            if (_engine.HasMedia && _lastSingleClickPauseTick > 0 &&
                (Environment.TickCount64 - _lastSingleClickPauseTick) <= dblLimit)
            {
                _lastSingleClickPauseTick = 0;
                _engine.SetPause(_wasPausedBeforeSingleClick);
                _osdOverlay?.HideOsdImmediate();
            }

            ToggleFullscreen();
        };

        _videoHost.VideoRightClicked += ShowVideoContextMenu;

        _videoHost.MouseWheelDelta += delta =>
        {
            double step = delta > 0 ? 5 : -5;
            _engine.AdjustVolume(step);
            ShowOsd($"Volume {Math.Round(_engine.Volume)}%");
        };

        _videoHost.VideoMouseMoved += OnFullscreenActivity;
        _videoHost.FilesDropped += HandleDroppedPaths;
    }

    private void HookEngineEvents()
    {
        _engine.PositionChanged += pos => Dispatcher.BeginInvoke(() =>
        {
            if (_isScrubbing) return;
            UpdateSeekBarVisuals(pos, _engine.Duration, _engine.CacheDuration);
            _fsControls?.UpdatePosition(pos, _engine.Duration, _engine.CacheDuration);
        });

        _engine.DurationChanged += dur => Dispatcher.BeginInvoke(() =>
        {
            if (_currentIndex >= 0 && _currentIndex < _playlist.Count && dur > 0)
                _playlist[_currentIndex].DurationText = FormatTime(dur);
            UpdateSeekBarVisuals(_engine.Position, dur, _engine.CacheDuration);
            _fsControls?.UpdatePosition(_engine.Position, dur, _engine.CacheDuration);
        });

        _engine.CacheDurationChanged += cache => Dispatcher.BeginInvoke(() =>
        {
            UpdateSeekBarVisuals(_isScrubbing ? _scrubTargetSeconds : _engine.Position, _engine.Duration, cache);
            _fsControls?.UpdatePosition(_isScrubbing ? _scrubTargetSeconds : _engine.Position, _engine.Duration, cache);
        });

        _engine.PauseChanged += paused => Dispatcher.BeginInvoke(() =>
        {
            PlayPausePath.Data = paused ? PlayGeo : PauseGeo;
            PlayPausePath.Margin = paused ? new Thickness(2, 0, 0, 0) : new Thickness(0);
            SyncFullscreenDockState();
            if (paused)
            {
                SaveCurrentResumePosition();
                StateStore.Save(_state);
                if (_isFullscreen)
                {
                    _fsHideTimer.Stop();
                    ShowFullscreenControls();
                }
            }
            else if (_isFullscreen && _fsControls is { IsVisible: true, IsPointerOverDock: false, IsScrubbing: false })
            {
                // Resumed via Space/keyboard/click — smoothly hide floating bar immediately without requiring mouse movement
                HideFullscreenControls();
            }
        });

        _engine.VolumeChanged += vol => Dispatcher.BeginInvoke(() =>
        {
            _suppressVolumeEvent = true;
            VolumeSlider.Value = Math.Clamp(vol, 0, 200);
            VolumeText.Text = $"{Math.Round(vol)}%";
            VolumeText.Foreground = vol > 100
                ? new SolidColorBrush(Color.FromRgb(0xFF, 0x9F, 0x43))
                : new SolidColorBrush(Color.FromRgb(0x94, 0xA3, 0xB8));
            _suppressVolumeEvent = false;
            UpdateMuteIcon();
            SyncFullscreenDockState();
        });

        _engine.MuteChanged += _ => Dispatcher.BeginInvoke(() =>
        {
            UpdateMuteIcon();
            SyncFullscreenDockState();
        });

        _engine.SpeedChanged += spd => Dispatcher.BeginInvoke(() =>
        {
            SpeedBtn.Content = $"{spd:0.##}x";
            SyncFullscreenDockState();
        });

        _engine.MediaTitleChanged += title => Dispatcher.BeginInvoke(() =>
        {
            if (!string.IsNullOrWhiteSpace(title))
            {
                TitleText.Text = title;
                Title = $"{title} — LiteVLC";
                AudioStageTitle.Text = title;
            }
        });

        _engine.FileLoaded += () => Dispatcher.BeginInvoke(() =>
        {
            SeekThumb.Visibility = Visibility.Visible;
            SyncOverlayBounds();
            if (_state.AutoSyncSubtitles)
                ScheduleAutoSubSync();
        });

        _engine.SubtitleTrackChanged += () => Dispatcher.BeginInvoke(() =>
        {
            if (!_engine.HasMedia) return;
            var active = _engine.GetActiveSubtitleTrack();
            if (active == null)
            {
                CancelSubtitleSyncCheck(resetBadge: true);
                _lastCheckedSubKey = null;
                return;
            }
            if (_state.AutoSyncSubtitles)
                ScheduleAutoSubSync();
            else
                SetSubSyncButtonState(SubSyncUiState.Idle);
        });

        _engine.VideoOutputChanged += hasVideo => Dispatcher.BeginInvoke(() =>
        {
            if (!_engine.HasMedia) return;
            if (hasVideo)
            {
                ShowStage(StageMode.Video);
            }
            else
            {
                AudioStageSubtitle.Text = "Audio Playback  •  WASAPI Hi-Fi";
                ShowStage(StageMode.AudioOrLoading);
            }
            SyncOverlayBounds();
        });

        _engine.FileLoadFailed += () => Dispatcher.BeginInvoke(() =>
        {
            CancelSubtitleSyncCheck(resetBadge: true);
            ShowStage(StageMode.Empty);
            ShowOsd("Unable to Play File", "File missing or unsupported");
        });

        _engine.FileEnded += isEof => Dispatcher.BeginInvoke(() =>
        {
            CancelSubtitleSyncCheck(resetBadge: true);
            if (!isEof) return;
            // Clear saved resume position on clean finish
            if (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            {
                _state.ResumePositions.Remove(_playlist[_currentIndex].Path);
                StateStore.Save(_state);
            }

            if (_loopMode == LoopMode.RepeatOne && _currentIndex >= 0)
                PlayIndex(_currentIndex, ignoreResume: true);
            else if (_currentIndex + 1 < _playlist.Count)
                PlayIndex(_currentIndex + 1);
            else if (_loopMode == LoopMode.RepeatAll && _playlist.Count > 0)
                PlayIndex(0);
        });

        _engine.SeekPreview += (delta, predicted) => Dispatcher.BeginInvoke(() =>
        {
            SyncOverlayBounds();
            _osdOverlay?.ShowSkipIndicator(delta, FormatTime(predicted));
            UpdateSeekBarVisuals(predicted, _engine.Duration, _engine.CacheDuration);
            _fsControls?.UpdatePosition(predicted, _engine.Duration, _engine.CacheDuration);
        });

        _engine.SeekCompleted += ms => Dispatcher.BeginInvoke(() =>
        {
            SeekLatencyBadge.Visibility = Visibility.Visible;
            SeekLatencyText.Text = $"{ms:0}ms";
            SeekLatencyText.Foreground = ms < 80
                ? new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80))
                : new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        });
    }

    protected override void OnSourceInitialized(EventArgs e)
    {
        base.OnSourceInitialized(e);
        // Keep the OS non-client frame permanently dark so Windows never flashes a white titlebar
        IntPtr hwnd = new WindowInteropHelper(this).Handle;
        int dark = 1;
        DwmSetWindowAttribute(hwnd, DWMWA_USE_IMMERSIVE_DARK_MODE, ref dark, sizeof(int));

        // Listen for Windows audio device plug/unplug (Bluetooth/HDMI/USB DAC) & sleep wake
        // so WASAPI audio automatically recovers without restarting LiteVLC.
        _hwndSource = HwndSource.FromHwnd(hwnd);
        _hwndSource?.AddHook(WndProcHook);
        SystemEvents.PowerModeChanged += OnSystemPowerModeChanged;
    }

    private IntPtr WndProcHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_DEVICECHANGE && wParam.ToInt32() == DBT_DEVNODES_CHANGED)
            _engine.MarkAudioNeedsReload();
        return IntPtr.Zero;
    }

    private void OnSystemPowerModeChanged(object? sender, PowerModeChangedEventArgs e)
    {
        if (e.Mode == PowerModes.Resume)
            _engine.MarkAudioNeedsReload();
    }

    private void StageContainer_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ClickCount == 2 && _engine.HasMedia && EmptyStatePanel.Visibility != Visibility.Visible)
        {
            ToggleFullscreen();
            e.Handled = true;
        }
    }

    private void Window_Loaded(object sender, RoutedEventArgs e)
    {
        VideoHostSlot.Visibility = Visibility.Visible;
        VideoHostSlot.Content = _videoHost;
        VideoHostSlot.UpdateLayout();

        try
        {
            _engine.Initialize(_videoHost.VideoHandle, _state.Volume, _state.FastSeek, _state.AudioBoost);
        }
        catch (Exception ex)
        {
            MessageBox.Show(this,
                $"Could not initialize libmpv engine:\n{ex.Message}\n\nEnsure libmpv-2.dll is next to LiteVLC.exe.",
                "LiteVLC Engine Error", MessageBoxButton.OK, MessageBoxImage.Error);
        }

        ShowStage(StageMode.Empty);

        // Create click-through overlay & compact floating fullscreen controller WITHOUT showing them until needed
        // (keeps Windows DWM in pure DirectFlip with ZERO flicker during playback!)
        _osdOverlay = new OsdOverlayWindow { Owner = this };
        InitFullscreenControlsWindow();

        LocationChanged += (_, _) => { if (_osdOverlay?.IsVisible == true) SyncOverlayBounds(); };
        SizeChanged += (_, _) =>
        {
            if (_osdOverlay?.IsVisible == true) SyncOverlayBounds();
            UpdateSeekBarVisuals(_isScrubbing ? _scrubTargetSeconds : _engine.Position, _engine.Duration, _engine.CacheDuration);
        };
        StateChanged += (_, _) =>
        {
            MaxRestorePath.Data = WindowState == WindowState.Maximized ? RestoreGeo : MaxGeo;
            if (!_isFullscreen)
                RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
            if (_osdOverlay != null && WindowState == WindowState.Minimized && _osdOverlay.IsVisible)
                _osdOverlay.Hide();
            Dispatcher.BeginInvoke(() =>
            {
                if (_osdOverlay?.IsVisible == true) SyncOverlayBounds();
            }, DispatcherPriority.Loaded);
        };
        StageContainer.SizeChanged += (_, _) => { if (_osdOverlay?.IsVisible == true) SyncOverlayBounds(); };

        _hudTimer.Start();
        _autoSaveTimer.Start();

        if (_startupArgs.Length > 0)
            HandleDroppedPaths(_startupArgs);

        // Silent background check for newer GitHub releases (non-blocking)
        _ = CheckForUpdateSilentlyAsync();
    }

    private void Window_Closing(object? sender, System.ComponentModel.CancelEventArgs e)
    {
        SaveCurrentResumePosition();
        _state.Volume = _engine.Volume;
        _state.FastSeek = _engine.FastSeekEnabled;
        StateStore.Save(_state);

        _hudTimer.Stop();
        _fsHideTimer.Stop();
        _fsMousePollTimer.Stop();
        _clickTimer.Stop();
        _autoSaveTimer.Stop();
        _autoSyncDebounceTimer.Stop();
        CancelSubtitleSyncCheck(resetBadge: false);
        SystemEvents.PowerModeChanged -= OnSystemPowerModeChanged;
        _hwndSource?.RemoveHook(WndProcHook);
        _fsControls?.Close();
        _osdOverlay?.Close();
        _engine.Dispose();
    }

    private void SyncOverlayBounds()
    {
        if (_osdOverlay == null || !IsLoaded || WindowState == WindowState.Minimized) return;
        if (StageContainer.ActualWidth <= 1 || StageContainer.ActualHeight <= 1) return;

        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null) return;
            Point topLeftPx = StageContainer.PointToScreen(new Point(0, 0));
            Matrix fromDevice = source.CompositionTarget.TransformFromDevice;
            Point topLeftDip = fromDevice.Transform(topLeftPx);

            _osdOverlay.Left = topLeftDip.X;
            _osdOverlay.Top = topLeftDip.Y;
            _osdOverlay.Width = StageContainer.ActualWidth;
            _osdOverlay.Height = StageContainer.ActualHeight;
        }
        catch { }
    }

    private enum StageMode { Empty, AudioOrLoading, Video }

    private void ShowStage(StageMode mode)
    {
        _videoHost.IsRenderingVideo = (mode == StageMode.Video);
        VideoHostSlot.Visibility = mode == StageMode.Video ? Visibility.Visible : Visibility.Collapsed;
        AudioStagePanel.Visibility = mode == StageMode.AudioOrLoading ? Visibility.Visible : Visibility.Collapsed;
        EmptyStatePanel.Visibility = mode == StageMode.Empty ? Visibility.Visible : Visibility.Collapsed;

        if (mode != StageMode.Video)
            _videoHost.RepaintSolidBackground();

        if (mode == StageMode.Empty)
        {
            TitleText.Text = "Ready • Direct3D 11 Engine";
            Title = "LiteVLC";
            TimeReadoutText.Text = "00:00 / 00:00";
            SeekFillScale.ScaleX = 0;
            SeekCacheScale.ScaleX = 0;
            SeekThumb.Visibility = Visibility.Collapsed;
            SeekLatencyBadge.Visibility = Visibility.Collapsed;
            RefreshRecentFilesUi();
        }
    }

    private static bool IsAudioOnlyExtension(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".mp3", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".flac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wav", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".ogg", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".opus", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".aac", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m4a", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".wma", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".alac", StringComparison.OrdinalIgnoreCase);
    }

    private void ShowOsd(string message, string? sub = null, int durationMs = 1150)
    {
        SyncOverlayBounds();
        _osdOverlay?.ShowOsd(message, sub, durationMs);
    }

    // ==================== MEDIA LOADING & PLAYLIST ====================

    private void HandleDroppedPaths(string[] paths)
    {
        if (paths.Length == 0) return;

        var mediaFiles = new List<string>();
        var subtitleFiles = new List<string>();

        foreach (string raw in paths)
        {
            if (string.IsNullOrWhiteSpace(raw)) continue;

            if (raw.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
                raw.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase))
            {
                mediaFiles.Add(raw);
                continue;
            }

            if (Directory.Exists(raw))
            {
                var found = Directory.EnumerateFiles(raw, "*.*", SearchOption.TopDirectoryOnly)
                    .Where(f => MediaExtensions.Contains(Path.GetExtension(f)))
                    .OrderBy(f => f, StringComparer.OrdinalIgnoreCase);
                mediaFiles.AddRange(found);
            }
            else if (File.Exists(raw))
            {
                string ext = Path.GetExtension(raw);
                if (SubtitleExtensions.Contains(ext))
                {
                    subtitleFiles.Add(raw);
                }
                else if (PlaylistFileService.IsPlaylistFile(raw))
                {
                    var loaded = PlaylistFileService.Load(raw);
                    if (loaded.Count > 0)
                    {
                        LoadParsedPlaylist(loaded, Path.GetFileName(raw));
                        return;
                    }
                }
                else
                {
                    mediaFiles.Add(raw);
                }
            }
        }

        if (subtitleFiles.Count > 0 && mediaFiles.Count == 0 && _engine.HasMedia)
        {
            foreach (string sub in subtitleFiles)
                _engine.AddExternalSubtitle(sub);
            ShowOsd("Subtitle Loaded", Path.GetFileName(subtitleFiles[^1]));
            return;
        }

        if (mediaFiles.Count > 0)
        {
            EnqueueAndPlay(mediaFiles, replacePlaylist: !_engine.HasMedia || _playlist.Count == 0);
            if (subtitleFiles.Count > 0)
            {
                foreach (string sub in subtitleFiles)
                    _engine.AddExternalSubtitle(sub);
            }
        }
    }

    private void EnqueueAndPlay(IEnumerable<string> paths, bool replacePlaylist)
    {
        if (replacePlaylist)
        {
            SaveCurrentResumePosition();
            _playlist.Clear();
            _currentIndex = -1;
        }

        int firstAdded = _playlist.Count;
        foreach (string p in paths)
        {
            string title = p.StartsWith("http", StringComparison.OrdinalIgnoreCase) || p.StartsWith("rtsp", StringComparison.OrdinalIgnoreCase)
                ? p
                : Path.GetFileName(p);
            _playlist.Add(new PlaylistItem { Path = p, Title = title });
        }

        UpdatePlaylistHeader();
        if (firstAdded < _playlist.Count && (replacePlaylist || !_engine.HasMedia))
            PlayIndex(firstAdded);
    }

    private void PlayIndex(int index, bool ignoreResume = false)
    {
        if (index < 0 || index >= _playlist.Count) return;

        SaveCurrentResumePosition();
        CancelSubtitleSyncCheck(resetBadge: true);
        _lastCheckedSubKey = null;

        for (int i = 0; i < _playlist.Count; i++)
            _playlist[i].IsPlaying = i == index;

        _currentIndex = index;
        var item = _playlist[index];
        PlaylistListBox.SelectedIndex = index;

        double? resume = null;
        if (!ignoreResume && _state.RememberPosition && !item.IsStream &&
            _state.ResumePositions.TryGetValue(item.Path, out double savedPos) && savedPos > 2.0)
        {
            resume = savedPos;
        }

        if (!item.IsStream)
        {
            _state.RecentFiles.RemoveAll(r => string.Equals(r, item.Path, StringComparison.OrdinalIgnoreCase));
            _state.RecentFiles.Insert(0, item.Path);
            if (_state.RecentFiles.Count > 8)
                _state.RecentFiles.RemoveRange(8, _state.RecentFiles.Count - 8);
            StateStore.Save(_state);
        }

        AudioStageTitle.Text = item.Title;
        AudioStageSubtitle.Text = IsAudioOnlyExtension(item.Path)
            ? "Audio Playback  •  WASAPI Hi-Fi"
            : "Loading Media...";
        // Show solid dark Audio/Loading stage until libmpv confirms real video frames (eliminates transparent HWND!)
        ShowStage(StageMode.AudioOrLoading);
        _engine.LoadFile(item.Path, resume);
        TitleText.Text = item.Title;
        Title = $"{item.Title} — LiteVLC";

        if (resume.HasValue)
            ShowOsd($"Resumed at {FormatTime(resume.Value)}", "Press 0 to restart", durationMs: 2200);
    }

    private void SaveCurrentResumePosition()
    {
        if (!_state.RememberPosition || !_engine.HasMedia || _currentIndex < 0 || _currentIndex >= _playlist.Count)
            return;

        var item = _playlist[_currentIndex];
        if (item.IsStream || _engine.Duration < 10) return;

        if (_engine.Position > 2 && _engine.Position < _engine.Duration - 3)
            _state.ResumePositions[item.Path] = _engine.Position;
        else if (_engine.Position >= _engine.Duration - 3)
            _state.ResumePositions.Remove(item.Path);
    }

    private void RefreshRecentFilesUi()
    {
        var entries = _state.RecentFiles
            .Where(File.Exists)
            .Take(5)
            .Select(p =>
            {
                string badge = "";
                if (_state.RememberPosition && _state.ResumePositions.TryGetValue(p, out double pos) && pos > 2)
                    badge = $"Resume {FormatTime(pos)}";
                return new RecentFileEntry
                {
                    Path = p,
                    FileName = Path.GetFileName(p),
                    ResumeBadge = badge
                };
            })
            .ToList();

        RecentFilesList.ItemsSource = entries;
        RecentFilesSection.Visibility = entries.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    private void UpdatePlaylistHeader()
    {
        PlaylistHeaderText.Text = $"Playlist ({_playlist.Count})";
    }

    // ==================== GPU-TRANSFORMED PRECISION SEEK BAR (ZERO LAYOUT PASS!) ====================

    private void UpdateSeekBarVisuals(double position, double duration, double cacheAhead)
    {
        TimeReadoutText.Text = $"{FormatTime(position)} / {FormatTime(duration)}";

        double width = SeekTrackSurface.ActualWidth;
        if (width <= 0 || duration <= 0)
        {
            SeekFillScale.ScaleX = 0;
            SeekCacheScale.ScaleX = 0;
            return;
        }

        double ratio = Math.Clamp(position / duration, 0, 1);
        double cacheRatio = Math.Clamp((position + Math.Max(0, cacheAhead)) / duration, ratio, 1);

        // GPU-only transforms — ZERO WPF Measure/Arrange invalidation during playback!
        SeekFillScale.ScaleX = ratio;
        SeekCacheScale.ScaleX = cacheRatio;

        double thumbX = Math.Clamp((ratio * width) - (SeekThumb.Width / 2.0), 0, Math.Max(0, width - SeekThumb.Width));
        SeekThumbTranslate.X = thumbX;
    }

    private void SeekTrack_MouseEnter(object sender, MouseEventArgs e)
    {
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
        SeekBarHeightScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.75, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        SeekThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
        SeekThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.12, TimeSpan.FromMilliseconds(140)) { EasingFunction = ease });
    }

    private double GetTimeAtMouse(MouseEventArgs e)
    {
        double w = SeekTrackSurface.ActualWidth;
        if (w <= 0 || _engine.Duration <= 0) return 0;
        double x = Math.Clamp(e.GetPosition(SeekTrackSurface).X, 0, w);
        return (x / w) * _engine.Duration;
    }

    private void SeekTrack_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (!_engine.HasMedia || _engine.Duration <= 0) return;
        _isScrubbing = true;
        _hasDraggedSinceDown = false;
        _scrubStartX = e.GetPosition(SeekTrackSurface).X;
        SeekTrackSurface.CaptureMouse();

        _scrubTargetSeconds = GetTimeAtMouse(e);
        _lastSentSeekTarget = _scrubTargetSeconds;
        UpdateSeekBarVisuals(_scrubTargetSeconds, _engine.Duration, _engine.CacheDuration);
        _engine.SeekAbsolute(_scrubTargetSeconds, forceFast: false);
        _lastScrubSeekMs = Environment.TickCount64;
    }

    private void SeekTrack_MouseMove(object sender, MouseEventArgs e)
    {
        if (!_engine.HasMedia || _engine.Duration <= 0) return;
        double w = SeekTrackSurface.ActualWidth;
        double x = Math.Clamp(e.GetPosition(SeekTrackSurface).X, 0, w);
        double hoverSec = (x / Math.Max(1, w)) * _engine.Duration;

        SeekHoverTimeText.Text = FormatTime(hoverSec);
        SeekHoverPopup.HorizontalOffset = Math.Clamp(x - 28, 0, Math.Max(0, w - 60));
        SeekHoverPopup.VerticalOffset = -30;
        SeekHoverPopup.IsOpen = true;

        if (_isScrubbing)
        {
            if (!_hasDraggedSinceDown && Math.Abs(x - _scrubStartX) >= 3.0)
                _hasDraggedSinceDown = true;

            if (!_hasDraggedSinceDown) return;

            _scrubTargetSeconds = hoverSec;
            UpdateSeekBarVisuals(hoverSec, _engine.Duration, _engine.CacheDuration);
            long now = Environment.TickCount64;
            if (now - _lastScrubSeekMs >= 85 && (double.IsNaN(_lastSentSeekTarget) || Math.Abs(hoverSec - _lastSentSeekTarget) >= 0.35))
            {
                _lastScrubSeekMs = now;
                _lastSentSeekTarget = hoverSec;
                _engine.SeekAbsolute(hoverSec, forceFast: true);
            }
        }
    }

    private void SeekTrack_MouseLeftButtonUp(object sender, MouseButtonEventArgs e)
    {
        if (!_isScrubbing) return;
        _isScrubbing = false;
        SeekTrackSurface.ReleaseMouseCapture();

        // Only issue a final seek if the user actually dragged to a new timestamp not yet seeked.
        // Prevents the double-seek replay glitch on a normal click or drag release!
        if (_hasDraggedSinceDown)
        {
            _scrubTargetSeconds = GetTimeAtMouse(e);
            if (double.IsNaN(_lastSentSeekTarget) || Math.Abs(_scrubTargetSeconds - _lastSentSeekTarget) >= 0.25)
            {
                _lastSentSeekTarget = _scrubTargetSeconds;
                _engine.SeekAbsolute(_scrubTargetSeconds, forceFast: false);
            }
            UpdateSeekBarVisuals(_scrubTargetSeconds, _engine.Duration, _engine.CacheDuration);
        }
        _hasDraggedSinceDown = false;
    }

    private void SeekTrack_MouseLeave(object sender, MouseEventArgs e)
    {
        if (!_isScrubbing)
        {
            SeekHoverPopup.IsOpen = false;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            SeekBarHeightScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(1.0, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            SeekThumbScale.BeginAnimation(ScaleTransform.ScaleXProperty, new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
            SeekThumbScale.BeginAnimation(ScaleTransform.ScaleYProperty, new DoubleAnimation(0.85, TimeSpan.FromMilliseconds(160)) { EasingFunction = ease });
        }
    }

    private void SeekTrack_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (!_engine.HasMedia || _engine.Duration <= 0) return;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        double step = shift ? 10.0 : (ctrl ? 1.0 : 5.0);
        _engine.SeekRelative(e.Delta > 0 ? step : -step);
        e.Handled = true;
    }

    private void VolumeSlider_MouseWheel(object sender, MouseWheelEventArgs e)
    {
        _engine.AdjustVolume(e.Delta > 0 ? 5 : -5);
        ShowOsd($"Volume {Math.Round(_engine.Volume)}%");
        e.Handled = true;
    }

    // ==================== KEYBOARD SHORTCUTS ====================

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        bool ctrl = (Keyboard.Modifiers & ModifierKeys.Control) != 0;
        bool shift = (Keyboard.Modifiers & ModifierKeys.Shift) != 0;

        switch (e.Key)
        {
            case Key.Space:
                if (_engine.HasMedia)
                {
                    bool willResume = _engine.IsPaused;
                    _engine.TogglePause();
                    ShowOsd(willResume ? "Playing" : "Paused", FormatTime(_engine.Position));
                    e.Handled = true;
                }
                break;

            case Key.Right:
                if (_engine.HasMedia)
                {
                    double step = ctrl ? 60 : (shift ? 3 : 10);
                    _engine.SeekRelative(step);
                    e.Handled = true;
                }
                break;

            case Key.Left:
                if (_engine.HasMedia)
                {
                    double step = ctrl ? -60 : (shift ? -3 : -10);
                    _engine.SeekRelative(step);
                    e.Handled = true;
                }
                break;

            case Key.D0:
            case Key.NumPad0:
            case Key.Home:
                if (_engine.HasMedia)
                {
                    _engine.SeekAbsolute(0, forceFast: true);
                    ShowOsd("Restarted", "00:00");
                    e.Handled = true;
                }
                break;

            case Key.L when ctrl:
                TogglePlaylistDrawer();
                e.Handled = true;
                break;

            case Key.L when !ctrl && _engine.HasMedia:
                _engine.SeekRelative(10);
                e.Handled = true;
                break;

            case Key.J when !ctrl && _engine.HasMedia:
                _engine.SeekRelative(-10);
                e.Handled = true;
                break;

            case Key.Up:
                _engine.AdjustVolume(5);
                ShowOsd($"Volume {Math.Round(_engine.Volume)}%");
                e.Handled = true;
                break;

            case Key.Down:
                _engine.AdjustVolume(-5);
                ShowOsd($"Volume {Math.Round(_engine.Volume)}%");
                e.Handled = true;
                break;

            case Key.M:
                _engine.ToggleMute();
                ShowOsd(_engine.IsMuted ? "Unmuted" : "Muted");
                e.Handled = true;
                break;

            case Key.F:
            case Key.F11:
                ToggleFullscreen();
                e.Handled = true;
                break;

            case Key.Escape when _isFullscreen:
                ToggleFullscreen();
                e.Handled = true;
                break;

            case Key.OemOpenBrackets:
                _engine.SetSpeed(_engine.Speed - 0.1);
                ShowOsd($"Speed {_engine.Speed:0.0#}x");
                e.Handled = true;
                break;

            case Key.OemCloseBrackets:
                _engine.SetSpeed(_engine.Speed + 0.1);
                ShowOsd($"Speed {_engine.Speed:0.0#}x");
                e.Handled = true;
                break;

            case Key.Back:
            case Key.OemPlus when !ctrl:
                _engine.SetSpeed(1.0);
                ShowOsd("Speed 1.0x");
                e.Handled = true;
                break;

            case Key.V when _engine.HasMedia:
                _engine.CycleSubtitleTrack();
                ShowOsd("Subtitle Track Cycled");
                e.Handled = true;
                break;

            case Key.B when _engine.HasMedia:
                _engine.CycleAudioTrack();
                ShowOsd("Audio Track Cycled");
                e.Handled = true;
                break;

            case Key.A when _engine.HasMedia:
                CycleAspectRatio();
                e.Handled = true;
                break;

            case Key.S when ctrl && shift:
            case Key.D when !ctrl:
                OpenSubtitleSearchModal();
                e.Handled = true;
                break;

            case Key.Y when !ctrl && _engine.HasMedia:
            case Key.S when !ctrl && shift && _engine.HasMedia:
                _ = RunSubtitleSyncCheckAsync(isManual: true);
                e.Handled = true;
                break;

            case Key.S when !ctrl && !shift && _engine.HasMedia:
                TakeScreenshot();
                e.Handled = true;
                break;

            case Key.G when _engine.HasMedia:
            {
                double d = _engine.AdjustSubDelay(-0.5);
                ShowOsd($"Subtitle Sync {d:+0.0;-0.0;0.0}s");
                e.Handled = true;
                break;
            }

            case Key.H when _engine.HasMedia:
            {
                double d = _engine.AdjustSubDelay(0.5);
                ShowOsd($"Subtitle Sync {d:+0.0;-0.0;0.0}s");
                e.Handled = true;
                break;
            }

            case Key.T when ctrl:
            case Key.K when !ctrl:
                OpenTrimModal();
                e.Handled = true;
                break;

            case Key.I:
            case Key.T when !ctrl:
                ToggleHud();
                e.Handled = true;
                break;

            case Key.N:
                PlayNext();
                e.Handled = true;
                break;

            case Key.P:
                PlayPrevious();
                e.Handled = true;
                break;

            case Key.O when ctrl:
                OpenFileDialog();
                e.Handled = true;
                break;

            case Key.U when ctrl:
                OpenUrlModal();
                e.Handled = true;
                break;

            case Key.OemPeriod when _engine.HasMedia:
                _engine.FrameStep();
                ShowOsd("+1 Frame");
                e.Handled = true;
                break;

            case Key.OemComma when _engine.HasMedia:
                _engine.FrameBackStep();
                ShowOsd("-1 Frame");
                e.Handled = true;
                break;
        }
    }

    // ==================== UI ACTIONS & MENUS ====================

    private void OpenFile_Click(object sender, RoutedEventArgs e) => OpenFileDialog();
    private void OpenUrl_Click(object sender, RoutedEventArgs e) => OpenUrlModal();

    private void PlayPause_Click(object sender, RoutedEventArgs e)
    {
        if (!_engine.HasMedia)
        {
            if (_playlist.Count > 0) PlayIndex(Math.Max(0, _currentIndex));
            else OpenFileDialog();
            return;
        }
        _engine.TogglePause();
    }

    private void SeekBack10_Click(object sender, RoutedEventArgs e) => _engine.SeekRelative(-10);
    private void SeekForward10_Click(object sender, RoutedEventArgs e) => _engine.SeekRelative(10);
    private void Prev_Click(object sender, RoutedEventArgs e) => PlayPrevious();
    private void Next_Click(object sender, RoutedEventArgs e) => PlayNext();

    private void Stop_Click(object sender, RoutedEventArgs e)
    {
        SaveCurrentResumePosition();
        StateStore.Save(_state);
        CancelSubtitleSyncCheck(resetBadge: true);
        _lastCheckedSubKey = null;
        _engine.Stop();
        ShowStage(StageMode.Empty);
    }

    private void Mute_Click(object sender, RoutedEventArgs e)
    {
        _engine.ToggleMute();
        ShowOsd(_engine.IsMuted ? "Unmuted" : "Muted");
    }

    private void VolumeSlider_ValueChanged(object sender, RoutedPropertyChangedEventArgs<double> e)
    {
        if (_suppressVolumeEvent || !IsLoaded || VolumeText == null) return;
        _engine.SetVolume(e.NewValue);
        VolumeText.Text = $"{Math.Round(e.NewValue)}%";
    }

    private void UpdateMuteIcon()
    {
        if (_engine.IsMuted || _engine.Volume < 0.5) VolumeIconPath.Data = VolMuteGeo;
        else if (_engine.Volume < 50) VolumeIconPath.Data = VolLowGeo;
        else VolumeIconPath.Data = VolHighGeo;
    }

    private void SpeedBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        double[] speeds = [0.5, 0.75, 1.0, 1.25, 1.5, 1.75, 2.0, 2.5, 3.0];
        foreach (double s in speeds)
        {
            var mi = new MenuItem
            {
                Header = $"{s:0.##}x" + (Math.Abs(s - 1.0) < 0.01 ? "  (Normal)" : ""),
                IsCheckable = true,
                IsChecked = Math.Abs(_engine.Speed - s) < 0.03
            };
            double target = s;
            mi.Click += (_, _) =>
            {
                _engine.SetSpeed(target);
                ShowOsd($"Speed {target:0.##}x");
            };
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = SpeedBtn;
        menu.IsOpen = true;
    }

    private void AudioTrackBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var tracks = _engine.GetTracks().Where(t => t.Type == "audio").ToList();

        var boostItem = new MenuItem
        {
            Header = "Dynamic Voice & Loudness Boost (+6 dB)",
            IsCheckable = true,
            IsChecked = _engine.AudioBoostEnabled
        };
        boostItem.Click += (_, _) => ToggleAudioBoost();
        menu.Items.Add(boostItem);

        var resetAudioItem = new MenuItem
        {
            Header = "Reset / Reconnect Audio Output",
            IsEnabled = _engine.HasMedia
        };
        resetAudioItem.Click += (_, _) =>
        {
            _engine.ReloadAudioOutput(bypassCooldown: true);
            ShowOsd("Audio Output Reconnected", "WASAPI refreshed");
        };
        menu.Items.Add(resetAudioItem);
        menu.Items.Add(new Separator());

        var offItem = new MenuItem { Header = "Disable Audio", IsCheckable = true, IsChecked = tracks.All(t => !t.Selected) };
        offItem.Click += (_, _) => { _engine.SetAudioTrack(null); ShowOsd("Audio Disabled"); };
        menu.Items.Add(offItem);

        if (tracks.Count > 0) menu.Items.Add(new Separator());

        foreach (var track in tracks)
        {
            var mi = new MenuItem { Header = track.DisplayName, IsCheckable = true, IsChecked = track.Selected };
            int id = track.Id;
            string name = track.DisplayName;
            mi.Click += (_, _) => { _engine.SetAudioTrack(id); ShowOsd("Audio Track", name); };
            menu.Items.Add(mi);
        }

        menu.PlacementTarget = AudioTrackBtn;
        menu.IsOpen = true;
    }

    private void SubTrackBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        var tracks = _engine.GetTracks().Where(t => t.Type == "sub").ToList();
        var activeSub = tracks.FirstOrDefault(t => t.Selected);

        var checkSyncNow = new MenuItem
        {
            Header = _isSubSyncChecking ? "Cancel Subtitle Sync Check" : "Check & Sync Active Subtitle with Audio",
            InputGestureText = "Y",
            IsEnabled = _engine.HasMedia
        };
        checkSyncNow.Click += (_, _) => _ = RunSubtitleSyncCheckAsync(isManual: true);
        menu.Items.Add(checkSyncNow);

        var autoSyncItem = new MenuItem
        {
            Header = "Auto-Sync Subtitles on Every Video",
            IsCheckable = true,
            IsChecked = _state.AutoSyncSubtitles
        };
        autoSyncItem.Click += (_, _) => ToggleAutoSync();
        menu.Items.Add(autoSyncItem);

        bool canSaveToSrt = activeSub is { External: true }
                         && !string.IsNullOrWhiteSpace(activeSub.ExternalFilename)
                         && activeSub.ExternalFilename.EndsWith(".srt", StringComparison.OrdinalIgnoreCase)
                         && File.Exists(activeSub.ExternalFilename)
                         && (Math.Abs(_engine.SubDelay) >= 0.1 || Math.Abs(_engine.SubSpeed - 1.0) > 0.001);
        if (canSaveToSrt)
        {
            var savePermanent = new MenuItem { Header = "Save Synced Timing Permanently to .srt File" };
            savePermanent.Click += (_, _) => SaveCurrentSyncToSrt();
            menu.Items.Add(savePermanent);
        }

        menu.Items.Add(new Separator());

        var searchOnline = new MenuItem { Header = "Search Subtitles Online...", InputGestureText = "D" };
        searchOnline.Click += (_, _) => OpenSubtitleSearchModal();
        menu.Items.Add(searchOnline);

        var loadExt = new MenuItem { Header = "Load Subtitle File (.srt, .ass, .vtt)..." };
        loadExt.Click += (_, _) => OpenSubtitleDialog();
        menu.Items.Add(loadExt);

        menu.Items.Add(new Separator());

        string speedSuffix = Math.Abs(_engine.SubSpeed - 1.0) > 0.001 ? $" • {_engine.SubSpeed:0.000}x" : "";
        var syncMinus = new MenuItem { Header = $"Subtitle Sync -0.5s  (current: {_engine.SubDelay:+0.0;-0.0;0.0}s{speedSuffix})", InputGestureText = "G" };
        syncMinus.Click += (_, _) => ShowOsd($"Subtitle Sync {_engine.AdjustSubDelay(-0.5):+0.0;-0.0;0.0}s");
        menu.Items.Add(syncMinus);

        var syncPlus = new MenuItem { Header = "Subtitle Sync +0.5s", InputGestureText = "H" };
        syncPlus.Click += (_, _) => ShowOsd($"Subtitle Sync {_engine.AdjustSubDelay(0.5):+0.0;-0.0;0.0}s");
        menu.Items.Add(syncPlus);

        if (Math.Abs(_engine.SubDelay) > 0.01 || Math.Abs(_engine.SubSpeed - 1.0) > 0.001)
        {
            var syncReset = new MenuItem { Header = "Reset Subtitle Sync (0.0s)" };
            syncReset.Click += (_, _) =>
            {
                _engine.ResetSubDelay();
                SetSubSyncButtonState(SubSyncUiState.Idle);
                ShowOsd("Subtitle Sync 0.0s");
            };
            menu.Items.Add(syncReset);
        }

        menu.Items.Add(new Separator());

        var offItem = new MenuItem { Header = "Subtitles Off", IsCheckable = true, IsChecked = tracks.All(t => !t.Selected) };
        offItem.Click += (_, _) => { _engine.SetSubtitleTrack(null); ShowOsd("Subtitles Off"); };
        menu.Items.Add(offItem);

        foreach (var track in tracks)
        {
            var mi = new MenuItem { Header = track.DisplayName, IsCheckable = true, IsChecked = track.Selected };
            int id = track.Id;
            string name = track.DisplayName;
            mi.Click += (_, _) => { _engine.SetSubtitleTrack(id); ShowOsd("Subtitle", name); };
            menu.Items.Add(mi);
        }

        menu.PlacementTarget = SubTrackBtn;
        menu.IsOpen = true;
    }

    private void ShowVideoContextMenu()
    {
        var menu = new ContextMenu();

        var playPause = new MenuItem { Header = _engine.IsPaused ? "Play" : "Pause", InputGestureText = "Space" };
        playPause.Click += (_, _) => _engine.TogglePause();
        menu.Items.Add(playPause);

        var restart = new MenuItem { Header = "Restart from Beginning", InputGestureText = "0" };
        restart.Click += (_, _) => { _engine.SeekAbsolute(0, forceFast: true); ShowOsd("Restarted", "00:00"); };
        menu.Items.Add(restart);

        var openF = new MenuItem { Header = "Open File...", InputGestureText = "Ctrl+O" };
        openF.Click += (_, _) => OpenFileDialog();
        menu.Items.Add(openF);

        var openU = new MenuItem { Header = "Open Stream URL...", InputGestureText = "Ctrl+U" };
        openU.Click += (_, _) => OpenUrlModal();
        menu.Items.Add(openU);

        menu.Items.Add(new Separator());

        var checkSubSync = new MenuItem { Header = "Check & Sync Subtitle with Audio", InputGestureText = "Y" };
        checkSubSync.Click += (_, _) => _ = RunSubtitleSyncCheckAsync(isManual: true);
        menu.Items.Add(checkSubSync);

        var autoSyncOpt = new MenuItem
        {
            Header = "Auto-Sync Subtitles on Every Video",
            IsCheckable = true,
            IsChecked = _state.AutoSyncSubtitles
        };
        autoSyncOpt.Click += (_, _) => ToggleAutoSync();
        menu.Items.Add(autoSyncOpt);

        var searchSub = new MenuItem { Header = "Search Subtitles Online...", InputGestureText = "D" };
        searchSub.Click += (_, _) => OpenSubtitleSearchModal();
        menu.Items.Add(searchSub);

        var loadSub = new MenuItem { Header = "Load External Subtitle..." };
        loadSub.Click += (_, _) => OpenSubtitleDialog();
        menu.Items.Add(loadSub);

        menu.Items.Add(new Separator());

        var audioBoost = new MenuItem
        {
            Header = "Audio & Dialogue Boost (+6 dB)",
            IsCheckable = true,
            IsChecked = _engine.AudioBoostEnabled
        };
        audioBoost.Click += (_, _) => ToggleAudioBoost();
        menu.Items.Add(audioBoost);

        var resumeOpt = new MenuItem
        {
            Header = "Resume from Last Scene on Reopen",
            IsCheckable = true,
            IsChecked = _state.RememberPosition
        };
        resumeOpt.Click += (_, _) => ToggleResume();
        menu.Items.Add(resumeOpt);

        var fastSeek = new MenuItem
        {
            Header = "Zero-Lag Fast Keyframe Seek",
            IsCheckable = true,
            IsChecked = _engine.FastSeekEnabled
        };
        fastSeek.Click += (_, _) => ToggleFastSeek();
        menu.Items.Add(fastSeek);

        var aspect = new MenuItem { Header = $"Aspect Ratio: {AspectModes[_aspectIndex].Label}", InputGestureText = "A" };
        aspect.Click += (_, _) => CycleAspectRatio();
        menu.Items.Add(aspect);

        var trimItem = new MenuItem { Header = "Trim / Cut Video Clip...", InputGestureText = "Ctrl+T" };
        trimItem.Click += (_, _) => OpenTrimModal();
        menu.Items.Add(trimItem);

        var shot = new MenuItem { Header = "Take Screenshot", InputGestureText = "S" };
        shot.Click += (_, _) => TakeScreenshot();
        menu.Items.Add(shot);

        menu.Items.Add(new Separator());

        var updItem = new MenuItem { Header = "Check for Updates (GitHub)..." };
        updItem.Click += (_, _) => OpenUpdateDialog();
        menu.Items.Add(updItem);

        var hud = new MenuItem { Header = "Performance HUD", InputGestureText = "I", IsCheckable = true, IsChecked = _osdOverlay?.IsHudVisible == true };
        hud.Click += (_, _) => ToggleHud();
        menu.Items.Add(hud);

        var fs = new MenuItem { Header = _isFullscreen ? "Exit Fullscreen" : "Fullscreen", InputGestureText = "F" };
        fs.Click += (_, _) => ToggleFullscreen();
        menu.Items.Add(fs);

        menu.IsOpen = true;
    }

    private void SearchSubtitles_Click(object sender, RoutedEventArgs e) => OpenSubtitleSearchModal();

    private void OpenSubtitleSearchModal()
    {
        string? currentPath = (_currentIndex >= 0 && _currentIndex < _playlist.Count)
            ? _playlist[_currentIndex].Path
            : null;
        string? fallbackTitle = !string.IsNullOrWhiteSpace(_engine.CurrentTitle)
            ? _engine.CurrentTitle
            : (_currentIndex >= 0 && _currentIndex < _playlist.Count ? _playlist[_currentIndex].Title : null);

        var probe = _engine.GetMediaProbeInfo();
        var dlg = new SubtitleSearchWindow(currentPath, fallbackTitle, _state.PreferredSubLang, probe) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.DownloadedSubtitlePath))
        {
            _state.PreferredSubLang = dlg.SelectedLanguageCode;
            StateStore.Save(_state);

            if (_engine.HasMedia)
            {
                _engine.ResetSubDelay();
                _engine.AddExternalSubtitle(dlg.DownloadedSubtitlePath);

                // SubtitleSearchWindow already audio-synced this file on disk; cache (0s, 1x) so Auto-Sync skips redundant re-scan
                if (!string.IsNullOrWhiteSpace(currentPath) && File.Exists(currentPath) && File.Exists(dlg.DownloadedSubtitlePath))
                {
                    int aid = _engine.GetActiveAudioTrack()?.Id ?? 0;
                    var pseudoTrack = new MediaTrack
                    {
                        Type = "sub",
                        External = true,
                        ExternalFilename = dlg.DownloadedSubtitlePath,
                        Selected = true
                    };
                    string key = SubtitleAutoSyncService.BuildTrackCacheKey(currentPath, pseudoTrack, aid);
                    _lastCheckedSubKey = key;
                    _state.SubtitleSyncCache[key] = new SavedSubtitleSync { OffsetSeconds = 0, SpeedScale = 1.0, ConfidenceZ = 10.0 };
                    StateStore.Save(_state);
                    SetSubSyncButtonState(SubSyncUiState.InSync);
                }
            }
            string detail = !string.IsNullOrWhiteSpace(dlg.SyncSummaryNote)
                ? dlg.SyncSummaryNote
                : Path.GetFileName(dlg.DownloadedSubtitlePath);
            ShowOsd("Subtitle Synced & Applied", detail, durationMs: 3000);
        }
        else if (!string.IsNullOrWhiteSpace(dlg.SelectedLanguageCode))
        {
            _state.PreferredSubLang = dlg.SelectedLanguageCode;
            StateStore.Save(_state);
        }
    }

    // ==================== PER-VIDEO AUDIO-TO-SUBTITLE SYNC CHECK & AUTO-SYNC ====================

    private enum SubSyncUiState { Idle, Checking, InSync, Synced, Uncertain }

    private void CheckSubSync_Click(object sender, RoutedEventArgs e)
        => _ = RunSubtitleSyncCheckAsync(isManual: true);

    private void ToggleAutoSync_Click(object sender, RoutedEventArgs e) => ToggleAutoSync();

    private void ToggleAutoSync()
    {
        _state.AutoSyncSubtitles = !_state.AutoSyncSubtitles;
        StateStore.Save(_state);
        UpdateAutoSyncBadge(_state.AutoSyncSubtitles);

        ShowOsd(_state.AutoSyncSubtitles ? "Auto-Sync Subtitles: ON" : "Auto-Sync Subtitles: OFF",
                _state.AutoSyncSubtitles ? "Automatically checks & syncs every video" : "Click Sync Check or press Y manually");

        if (_state.AutoSyncSubtitles && _engine.HasMedia && !_isSubSyncChecking)
            _ = RunSubtitleSyncCheckAsync(isManual: false);
    }

    private void UpdateAutoSyncBadge(bool enabled)
    {
        if (enabled)
        {
            AutoSyncBadgeText.Text = "Auto: ON";
            AutoSyncBadgeBtn.Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x24, 0x19));
            AutoSyncBadgeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x5C, 0x3E));
            AutoSyncBadgeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
        }
        else
        {
            AutoSyncBadgeText.Text = "Auto: OFF";
            AutoSyncBadgeBtn.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1B, 0x2A));
            AutoSyncBadgeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x2F, 0x47));
            AutoSyncBadgeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }
    }

    private void SetSubSyncButtonState(SubSyncUiState state, double offsetSeconds = 0)
    {
        switch (state)
        {
            case SubSyncUiState.Checking:
                SubSyncBtnText.Text = "Checking...";
                SubSyncBtn.Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x21, 0x30));
                SubSyncBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x5D, 0x82));
                SubSyncBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
                break;

            case SubSyncUiState.InSync:
                SubSyncBtnText.Text = "In Sync ✓";
                SubSyncBtn.Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x24, 0x19));
                SubSyncBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x5C, 0x3E));
                SubSyncBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                break;

            case SubSyncUiState.Synced:
                SubSyncBtnText.Text = $"Sync {offsetSeconds:+0.0;-0.0;0.0}s ✓";
                SubSyncBtn.Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x24, 0x19));
                SubSyncBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x5C, 0x3E));
                SubSyncBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                break;

            case SubSyncUiState.Uncertain:
                SubSyncBtnText.Text = "Sync ⚠";
                SubSyncBtn.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x1D, 0x10));
                SubSyncBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x4E, 0x22));
                SubSyncBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
                break;

            default:
                SubSyncBtnText.Text = "Sync Check";
                SubSyncBtn.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1B, 0x2A));
                SubSyncBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x2F, 0x47));
                SubSyncBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
                break;
        }
    }

    private void ScheduleAutoSubSync()
    {
        _autoSyncDebounceTimer.Stop();
        _autoSyncDebounceTimer.Start();
    }

    private void CancelSubtitleSyncCheck(bool resetBadge)
    {
        _autoSyncDebounceTimer.Stop();
        var old = _subSyncCts;
        _subSyncCts = null;
        _isSubSyncChecking = false;
        try { old?.Cancel(); } catch { }
        if (resetBadge)
            SetSubSyncButtonState(SubSyncUiState.Idle);
    }

    private async Task RunSubtitleSyncCheckAsync(bool isManual)
    {
        if (_isSubSyncChecking)
        {
            if (isManual)
            {
                CancelSubtitleSyncCheck(resetBadge: true);
                ShowOsd("Sync Check Cancelled");
                return;
            }
            CancelSubtitleSyncCheck(resetBadge: false);
        }

        if (!_engine.HasMedia || _currentIndex < 0 || _currentIndex >= _playlist.Count)
        {
            if (isManual) ShowOsd("Open a Video First", "Play a local video with subtitles");
            return;
        }

        var item = _playlist[_currentIndex];
        if (item.IsStream || !File.Exists(item.Path))
        {
            if (isManual) ShowOsd("Local Videos Only", "Audio sync requires a local video file");
            return;
        }

        if (!_engine.HasVideo && IsAudioOnlyExtension(item.Path))
        {
            if (isManual) ShowOsd("Video Required", "Audio-only files have no subtitles");
            return;
        }

        var activeSub = _engine.GetActiveSubtitleTrack();
        if (activeSub == null)
        {
            SetSubSyncButtonState(SubSyncUiState.Idle);
            if (isManual) ShowOsd("No Subtitle Active", "Press D to search online or drop an .srt file", durationMs: 2400);
            return;
        }

        int activeAudioId = _engine.GetActiveAudioTrack()?.Id ?? 0;
        string cacheKey = SubtitleAutoSyncService.BuildTrackCacheKey(item.Path, activeSub, activeAudioId);

        if (!isManual)
        {
            if (string.Equals(_lastCheckedSubKey, cacheKey, StringComparison.Ordinal))
                return;

            if (_state.SubtitleSyncCache.TryGetValue(cacheKey, out var cached))
            {
                _lastCheckedSubKey = cacheKey;
                _engine.SetSubSync(cached.OffsetSeconds, cached.SpeedScale);
                bool hasShift = Math.Abs(cached.OffsetSeconds) >= 0.25 || Math.Abs(cached.SpeedScale - 1.0) > 0.001;
                SetSubSyncButtonState(hasShift ? SubSyncUiState.Synced : SubSyncUiState.InSync, cached.OffsetSeconds);
                if (hasShift)
                    ShowOsd("Subtitle Sync Restored ✓", $"Offset {cached.OffsetSeconds:+0.0;-0.0;0.0}s (cached)", durationMs: 1800);
                return;
            }
        }

        _lastCheckedSubKey = cacheKey;
        var cts = new CancellationTokenSource();
        _subSyncCts = cts;
        var ct = cts.Token;
        _isSubSyncChecking = true;

        SetSubSyncButtonState(SubSyncUiState.Checking);
        if (isManual)
            ShowOsd("Checking Subtitle Sync...", "Analyzing speech audio (3–5s)", durationMs: 2500);

        string capturedVideoPath = item.Path;
        int capturedSubId = activeSub.Id;
        string capturedSubFile = activeSub.ExternalFilename;

        try
        {
            var result = await SubtitleAutoSyncService.AnalyzeActiveTrackAsync(
                capturedVideoPath,
                _engine.Duration,
                activeSub,
                activeAudioId,
                statusProgress: null,
                ct);

            if (ct.IsCancellationRequested) return;

            // Ensure user didn't switch videos or subtitle tracks while analyzing in background
            bool stillSameMedia = _engine.HasMedia
                               && _currentIndex >= 0 && _currentIndex < _playlist.Count
                               && string.Equals(_playlist[_currentIndex].Path, capturedVideoPath, StringComparison.OrdinalIgnoreCase);
            var currentSub = stillSameMedia ? _engine.GetActiveSubtitleTrack() : null;
            bool stillSameSub = currentSub != null
                             && currentSub.Id == capturedSubId
                             && string.Equals(currentSub.ExternalFilename, capturedSubFile, StringComparison.OrdinalIgnoreCase);

            if (!stillSameMedia || !stillSameSub)
                return;

            if (result.Succeeded)
            {
                _engine.SetSubSync(result.OffsetSeconds, result.SpeedScale);
                _state.SubtitleSyncCache[cacheKey] = new SavedSubtitleSync
                {
                    OffsetSeconds = result.OffsetSeconds,
                    SpeedScale = result.SpeedScale,
                    ConfidenceZ = result.ConfidenceZ
                };
                StateStore.Save(_state);

                if (result.WasModified)
                {
                    SetSubSyncButtonState(SubSyncUiState.Synced, result.OffsetSeconds);
                    ShowOsd(isManual ? "Subtitle Synced ✓" : "Auto-Synced Subtitle ✓", result.Summary, durationMs: 3000);
                }
                else
                {
                    SetSubSyncButtonState(SubSyncUiState.InSync);
                    ShowOsd("Subtitle Already in Sync ✓", result.Summary, durationMs: isManual ? 2400 : 1600);
                }
            }
            else
            {
                SetSubSyncButtonState(isManual ? SubSyncUiState.Uncertain : SubSyncUiState.Idle);
                if (isManual)
                    ShowOsd("Sync Check Uncertain", result.Summary, durationMs: 2800);
            }
        }
        catch (OperationCanceledException)
        {
            // Cancelled cleanly on video switch / stop / manual cancel
        }
        catch (Exception ex)
        {
            if (!ct.IsCancellationRequested)
            {
                SetSubSyncButtonState(isManual ? SubSyncUiState.Uncertain : SubSyncUiState.Idle);
                if (isManual)
                    ShowOsd("Sync Check Failed", ex.Message, durationMs: 2800);
            }
        }
        finally
        {
            if (ReferenceEquals(_subSyncCts, cts))
            {
                _isSubSyncChecking = false;
                _subSyncCts = null;
            }
            cts.Dispose();
        }
    }

    private void SaveCurrentSyncToSrt()
    {
        if (!_engine.HasMedia || _currentIndex < 0 || _currentIndex >= _playlist.Count) return;
        var activeSub = _engine.GetActiveSubtitleTrack();
        if (activeSub is not { External: true } || string.IsNullOrWhiteSpace(activeSub.ExternalFilename) || !File.Exists(activeSub.ExternalFilename))
        {
            ShowOsd("External .srt Required", "Embedded tracks use live sync automatically");
            return;
        }

        try
        {
            SubtitleAutoSyncService.SaveSyncedSrtFile(activeSub.ExternalFilename, _engine.SubSpeed, _engine.SubDelay);
            _engine.ResetSubDelay();
            _engine.ReloadExternalSubtitles();

            int aid = _engine.GetActiveAudioTrack()?.Id ?? 0;
            string newKey = SubtitleAutoSyncService.BuildTrackCacheKey(_playlist[_currentIndex].Path, activeSub, aid);
            _lastCheckedSubKey = newKey;
            _state.SubtitleSyncCache[newKey] = new SavedSubtitleSync { OffsetSeconds = 0, SpeedScale = 1.0, ConfidenceZ = 10.0 };
            StateStore.Save(_state);

            SetSubSyncButtonState(SubSyncUiState.InSync);
            ShowOsd("Saved Sync to .srt ✓", Path.GetFileName(activeSub.ExternalFilename), durationMs: 2400);
        }
        catch (Exception ex)
        {
            ShowOsd("Could Not Save .srt", ex.Message, durationMs: 2800);
        }
    }

    private void ToggleAudioBoost_Click(object sender, RoutedEventArgs e) => ToggleAudioBoost();

    private void ToggleAudioBoost()
    {
        bool next = !_engine.AudioBoostEnabled;
        _engine.SetAudioBoost(next);
        _state.AudioBoost = next;
        StateStore.Save(_state);
        UpdateAudioBoostBadge(next);
        ShowOsd(next ? "Audio Boost: ON" : "Audio Boost: OFF",
                next ? "Louder dialogue & 5.1 normalization" : "Standard +4.5 dB gain");
    }

    private void UpdateAudioBoostBadge(bool enabled)
    {
        if (enabled)
        {
            AudioBoostBadgeText.Text = "Boost: ON";
            AudioBoostBadgeBtn.Background = new SolidColorBrush(Color.FromRgb(0x28, 0x18, 0x0F));
            AudioBoostBadgeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x7A, 0x3D, 0x1D));
            AudioBoostBadgeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFF, 0x8B, 0x42));
        }
        else
        {
            AudioBoostBadgeText.Text = "Boost: OFF";
            AudioBoostBadgeBtn.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1B, 0x2A));
            AudioBoostBadgeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x2F, 0x47));
            AudioBoostBadgeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }
    }

    private void ToggleResume_Click(object sender, RoutedEventArgs e) => ToggleResume();

    private void ToggleResume()
    {
        _state.RememberPosition = !_state.RememberPosition;
        if (_state.RememberPosition)
            SaveCurrentResumePosition();
        StateStore.Save(_state);
        UpdateResumeBadge(_state.RememberPosition);
        RefreshRecentFilesUi();
        ShowOsd(_state.RememberPosition ? "Auto-Resume: ON" : "Auto-Resume: OFF",
                _state.RememberPosition ? "Continues from last scene" : "Always starts at 00:00");
    }

    private void UpdateResumeBadge(bool enabled)
    {
        if (enabled)
        {
            ResumeBadgeText.Text = "Resume: ON";
            ResumeBadgeBtn.Background = new SolidColorBrush(Color.FromRgb(0x10, 0x22, 0x29));
            ResumeBadgeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x22, 0x5A, 0x70));
            ResumeBadgeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8));
        }
        else
        {
            ResumeBadgeText.Text = "Resume: OFF";
            ResumeBadgeBtn.Background = new SolidColorBrush(Color.FromRgb(0x15, 0x1B, 0x2A));
            ResumeBadgeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x2F, 0x47));
            ResumeBadgeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x64, 0x74, 0x8B));
        }
    }

    private void ToggleFastSeek_Click(object sender, RoutedEventArgs e) => ToggleFastSeek();

    private void ToggleFastSeek()
    {
        bool next = !_engine.FastSeekEnabled;
        _engine.SetFastSeek(next);
        UpdateFastSeekBadge(next);
        ShowOsd(next ? "Fast Keyframe Seek" : "Exact Frame Seek",
                next ? "Zero-Lag Mode" : "Frame-Accurate Mode");
    }

    private void UpdateFastSeekBadge(bool fast)
    {
        if (fast)
        {
            FastSeekBadgeText.Text = "Fast Seek";
            FastSeekBadgeBtn.Background = new SolidColorBrush(Color.FromRgb(0x0F, 0x24, 0x19));
            FastSeekBadgeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x26, 0x5C, 0x3E));
            FastSeekBadgeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
        }
        else
        {
            FastSeekBadgeText.Text = "Exact Seek";
            FastSeekBadgeBtn.Background = new SolidColorBrush(Color.FromRgb(0x24, 0x1D, 0x10));
            FastSeekBadgeBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x6B, 0x4E, 0x22));
            FastSeekBadgeBtn.Foreground = new SolidColorBrush(Color.FromRgb(0xFB, 0xBF, 0x24));
        }
    }

    private void ToggleHud_Click(object sender, RoutedEventArgs e) => ToggleHud();

    private void ToggleHud()
    {
        if (_osdOverlay == null) return;
        bool next = !_osdOverlay.IsHudVisible;
        if (next) SyncOverlayBounds();
        _osdOverlay.SetHudVisible(next);
        if (next) _osdOverlay.UpdateHud(_engine.GetStatsSnapshot());

        HudToggleBtn.BorderBrush = next
            ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
            : new SolidColorBrush(Color.FromRgb(0x25, 0x2F, 0x47));
        HudToggleBtn.Foreground = next
            ? new SolidColorBrush(Color.FromRgb(0x38, 0xBD, 0xF8))
            : new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
    }

    private void AspectBtn_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu();
        for (int i = 0; i < AspectModes.Length; i++)
        {
            int idx = i;
            var mode = AspectModes[i];
            var mi = new MenuItem
            {
                Header = mode.Label,
                IsCheckable = true,
                IsChecked = idx == _aspectIndex
            };
            mi.Click += (_, _) => ApplyAspectMode(idx);
            menu.Items.Add(mi);
        }
        menu.PlacementTarget = AspectBtn;
        menu.IsOpen = true;
    }

    private void CycleAspectRatio() => ApplyAspectMode((_aspectIndex + 1) % AspectModes.Length);

    private void ApplyAspectMode(int index)
    {
        _aspectIndex = index;
        var mode = AspectModes[_aspectIndex];
        _engine.SetKeepAspect(mode.KeepAspect);
        _engine.SetAspectRatio(mode.Value);
        _engine.SetPanScan(mode.PanScan);
        AspectBtn.Content = mode.ShortLabel;
        SyncFullscreenDockState();
        ShowOsd($"Video: {mode.Label}");
    }

    private void Screenshot_Click(object sender, RoutedEventArgs e) => TakeScreenshot();

    private void TakeScreenshot()
    {
        string? file = _engine.TakeScreenshot();
        if (file != null)
            ShowOsd("Screenshot Saved", Path.GetFileName(file));
    }

    private void ToggleAlwaysOnTop_Click(object sender, RoutedEventArgs e)
    {
        Topmost = !Topmost;
        PinBtn.Foreground = Topmost
            ? new SolidColorBrush(Color.FromRgb(0xFF, 0x6B, 0x2B))
            : new SolidColorBrush(Color.FromRgb(0xCB, 0xD5, 0xE1));
        ShowOsd(Topmost ? "Always on Top: ON" : "Always on Top: OFF");
    }

    // ==================== SMOOTH ANIMATED PLAYLIST DRAWER ====================

    private void TogglePlaylist_Click(object sender, RoutedEventArgs e) => TogglePlaylistDrawer();

    private void TogglePlaylistDrawer()
    {
        _isPlaylistOpen = !_isPlaylistOpen;
        var ease = new CubicEase { EasingMode = EasingMode.EaseOut };

        if (_isPlaylistOpen)
        {
            PlaylistDrawer.Visibility = Visibility.Visible;
            var wAnim = new DoubleAnimation(PlaylistDrawer.ActualWidth, 310, TimeSpan.FromMilliseconds(220)) { EasingFunction = ease };
            var oAnim = new DoubleAnimation(PlaylistDrawer.Opacity, 1.0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease };
            wAnim.Completed += (_, _) => { if (_osdOverlay?.IsVisible == true) SyncOverlayBounds(); };
            PlaylistDrawer.BeginAnimation(WidthProperty, wAnim);
            PlaylistDrawer.BeginAnimation(OpacityProperty, oAnim);
        }
        else
        {
            var wAnim = new DoubleAnimation(PlaylistDrawer.ActualWidth, 0, TimeSpan.FromMilliseconds(190)) { EasingFunction = ease };
            var oAnim = new DoubleAnimation(PlaylistDrawer.Opacity, 0.0, TimeSpan.FromMilliseconds(150));
            wAnim.Completed += (_, _) =>
            {
                if (!_isPlaylistOpen) PlaylistDrawer.Visibility = Visibility.Collapsed;
                if (_osdOverlay?.IsVisible == true) SyncOverlayBounds();
            };
            PlaylistDrawer.BeginAnimation(WidthProperty, wAnim);
            PlaylistDrawer.BeginAnimation(OpacityProperty, oAnim);
        }
    }

    private void AddToPlaylist_Click(object sender, RoutedEventArgs e) => OpenFileDialog(appendToPlaylist: true);

    private void ClearPlaylist_Click(object sender, RoutedEventArgs e)
    {
        _playlist.Clear();
        _currentIndex = -1;
        UpdatePlaylistHeader();
    }

    private void CycleLoopMode_Click(object sender, RoutedEventArgs e)
    {
        _loopMode = (LoopMode)(((int)_loopMode + 1) % 3);
        LoopBtn.Content = _loopMode switch
        {
            LoopMode.RepeatAll => "Loop: All",
            LoopMode.RepeatOne => "Loop: One",
            _ => "Loop: Off"
        };
        ShowOsd(LoopBtn.Content.ToString() ?? "Loop");
    }

    private void PlaylistListBox_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PlaylistListBox.SelectedIndex >= 0)
            PlayIndex(PlaylistListBox.SelectedIndex);
    }

    private void PlaylistListBox_MouseRightButtonUp(object sender, MouseButtonEventArgs e)
    {
        int idx = PlaylistListBox.SelectedIndex;
        var menu = new ContextMenu();

        if (idx >= 0 && idx < _playlist.Count)
        {
            var playMi = new MenuItem { Header = "Play Now" };
            playMi.Click += (_, _) => PlayIndex(idx);
            menu.Items.Add(playMi);

            var moveUp = new MenuItem { Header = "Move Up", IsEnabled = idx > 0 };
            moveUp.Click += (_, _) => MovePlaylistItem(idx, idx - 1);
            menu.Items.Add(moveUp);

            var moveDown = new MenuItem { Header = "Move Down", IsEnabled = idx < _playlist.Count - 1 };
            moveDown.Click += (_, _) => MovePlaylistItem(idx, idx + 1);
            menu.Items.Add(moveDown);

            var removeMi = new MenuItem { Header = "Remove from Playlist" };
            removeMi.Click += (_, _) => RemovePlaylistItem(idx);
            menu.Items.Add(removeMi);
            menu.Items.Add(new Separator());
        }

        var saveMi = new MenuItem { Header = "Save Playlist (.xspf / .m3u8)...", IsEnabled = _playlist.Count > 0 };
        saveMi.Click += (_, _) => SavePlaylistDialog();
        menu.Items.Add(saveMi);

        var loadMi = new MenuItem { Header = "Load Playlist (.xspf / .m3u8)..." };
        loadMi.Click += (_, _) => LoadPlaylistDialog();
        menu.Items.Add(loadMi);

        menu.IsOpen = true;
    }

    private void MovePlaylistItem(int fromIdx, int toIdx)
    {
        if (fromIdx < 0 || toIdx < 0 || fromIdx >= _playlist.Count || toIdx >= _playlist.Count) return;
        _playlist.Move(fromIdx, toIdx);
        if (_currentIndex == fromIdx) _currentIndex = toIdx;
        else if (_currentIndex == toIdx) _currentIndex = fromIdx;
        PlaylistListBox.SelectedIndex = toIdx;
    }

    private void RemovePlaylistItem(int idx)
    {
        if (idx < 0 || idx >= _playlist.Count) return;
        _playlist.RemoveAt(idx);
        if (_currentIndex == idx) _currentIndex = -1;
        else if (_currentIndex > idx) _currentIndex--;
        UpdatePlaylistHeader();
    }

    private void SavePlaylist_Click(object sender, RoutedEventArgs e) => SavePlaylistDialog();
    private void LoadPlaylist_Click(object sender, RoutedEventArgs e) => LoadPlaylistDialog();

    private void SavePlaylistDialog()
    {
        if (_playlist.Count == 0)
        {
            ShowOsd("Playlist is Empty", "Add files first");
            return;
        }

        var dlg = new SaveFileDialog
        {
            Title = "Save Playlist",
            FileName = "MyPlaylist.xspf",
            Filter = "VLC XSPF Playlist (*.xspf)|*.xspf|M3U8 Playlist (*.m3u8)|*.m3u8|M3U Playlist (*.m3u)|*.m3u"
        };

        if (dlg.ShowDialog(this) == true)
        {
            try
            {
                PlaylistFileService.Save(dlg.FileName, _playlist);
                ShowOsd("Playlist Saved", Path.GetFileName(dlg.FileName), durationMs: 2000);
            }
            catch (Exception ex)
            {
                MessageBox.Show(this, $"Could not save playlist:\n{ex.Message}", "LiteVLC", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }

    private void LoadPlaylistDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Playlist",
            Filter = "Playlists (*.xspf;*.m3u8;*.m3u)|*.xspf;*.m3u8;*.m3u|All Files|*.*"
        };

        if (dlg.ShowDialog(this) == true)
        {
            var entries = PlaylistFileService.Load(dlg.FileName);
            if (entries.Count == 0)
            {
                ShowOsd("Empty Playlist", Path.GetFileName(dlg.FileName));
                return;
            }
            LoadParsedPlaylist(entries, Path.GetFileName(dlg.FileName));
        }
    }

    private void LoadParsedPlaylist(List<(string Path, string Title)> entries, string playlistName)
    {
        SaveCurrentResumePosition();
        _playlist.Clear();
        _currentIndex = -1;

        foreach (var (path, title) in entries)
            _playlist.Add(new PlaylistItem { Path = path, Title = title });

        UpdatePlaylistHeader();
        if (_playlist.Count > 0)
        {
            if (!_isPlaylistOpen && !_isFullscreen)
                TogglePlaylistDrawer();
            PlayIndex(0);
            ShowOsd($"Playlist: {playlistName}", $"{_playlist.Count} items", durationMs: 2000);
        }
    }

    // ==================== VIDEO TRIMMER & AUTO-UPDATER ====================

    private void TrimVideo_Click(object sender, RoutedEventArgs e) => OpenTrimModal();

    private void OpenTrimModal()
    {
        if (!_engine.HasMedia || _currentIndex < 0 || _currentIndex >= _playlist.Count)
        {
            ShowOsd("Open a Video First", "Nothing playing to cut");
            return;
        }

        var current = _playlist[_currentIndex];
        if (current.IsStream || !File.Exists(current.Path))
        {
            ShowOsd("Local Files Only", "Cannot trim network streams");
            return;
        }

        bool wasPlaying = !_engine.IsPaused;
        if (wasPlaying) _engine.SetPause(true);

        var dlg = new TrimVideoDialog(current.Path, _engine.Duration, () => _engine.Position) { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.SavedClipPath))
            ShowOsd("Clip Saved", Path.GetFileName(dlg.SavedClipPath), durationMs: 2200);
    }

    private void CheckUpdate_Click(object sender, RoutedEventArgs e) => OpenUpdateDialog();

    private void OpenUpdateDialog()
    {
        // Always perform a fresh live check so updates always jump straight to the absolute latest release
        var dlg = new UpdateDialog(null) { Owner = this };
        if (dlg.ShowDialog() == true && dlg.UpdateLaunched)
        {
            // Close immediately so silent Inno Setup replaces the app in-place and relaunches the new version cleanly!
            Application.Current.Shutdown();
        }
    }

    private async System.Threading.Tasks.Task CheckForUpdateSilentlyAsync()
    {
        try
        {
            await System.Threading.Tasks.Task.Delay(3500);
            var res = await _updater.CheckForUpdateAsync();
            _cachedUpdateResult = res;
            if (res.UpdateAvailable)
            {
                await Dispatcher.InvokeAsync(() =>
                {
                    UpdateDot.Visibility = Visibility.Visible;
                    UpdateBtnText.Text = $"Update {res.TagName}";
                    UpdateBtn.Background = new SolidColorBrush(Color.FromRgb(0x0E, 0x26, 0x1A));
                    UpdateBtn.BorderBrush = new SolidColorBrush(Color.FromRgb(0x25, 0x5E, 0x3F));
                    UpdateBtn.Foreground = new SolidColorBrush(Color.FromRgb(0x4A, 0xDE, 0x80));
                    UpdateBtn.ToolTip = $"New LiteVLC {res.TagName} available on GitHub — click to update automatically!";
                });
            }
        }
        catch { }
    }

    private void PlayNext()
    {
        if (_playlist.Count == 0) return;
        int next = _currentIndex + 1;
        if (next >= _playlist.Count) next = _loopMode == LoopMode.RepeatAll ? 0 : _playlist.Count - 1;
        if (next != _currentIndex) PlayIndex(next);
    }

    private void PlayPrevious()
    {
        if (_playlist.Count == 0) return;
        if (_engine.Position > 4)
        {
            _engine.SeekAbsolute(0, forceFast: true);
            return;
        }
        int prev = Math.Max(0, _currentIndex - 1);
        PlayIndex(prev);
    }

    private void RecentFile_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button { Tag: string path } && File.Exists(path))
            EnqueueAndPlay([path], replacePlaylist: true);
    }

    // ==================== FLOATING COMPACT VLC-STYLE FULLSCREEN CONTROLLER ====================

    private void InitFullscreenControlsWindow()
    {
        _fsControls = new FullscreenControlsWindow { Owner = this };
        _fsControls.PlayPauseRequested += () => _engine.TogglePause();
        _fsControls.SeekBack10Requested += () => _engine.SeekRelative(-10);
        _fsControls.SeekForward10Requested += () => _engine.SeekRelative(10);
        _fsControls.PrevRequested += PlayPrevious;
        _fsControls.NextRequested += PlayNext;
        _fsControls.MuteRequested += () =>
        {
            _engine.ToggleMute();
            ShowOsd(_engine.IsMuted ? "Unmuted" : "Muted");
        };
        _fsControls.VolumeRequested += v => _engine.SetVolume(v);
        _fsControls.SeekAbsoluteRequested += (t, fast) => _engine.SeekAbsolute(t, forceFast: fast);
        _fsControls.SeekRelativeRequested += d => _engine.SeekRelative(d);
        _fsControls.SpeedCycleRequested += () =>
        {
            double[] cycle = [1.0, 1.25, 1.5, 1.75, 2.0, 0.5, 0.75];
            int cur = Array.FindIndex(cycle, s => Math.Abs(s - _engine.Speed) < 0.04);
            double next = cycle[(cur + 1) % cycle.Length];
            _engine.SetSpeed(next);
            ShowOsd($"Speed {next:0.##}x");
        };
        _fsControls.AspectCycleRequested += CycleAspectRatio;
        _fsControls.ExitFullscreenRequested += ToggleFullscreen;
        _fsControls.HoverEntered += () =>
        {
            _fsHideTimer.Stop();
            _videoHost.SetCursorHidden(false);
        };
        _fsControls.HoverLeft += () =>
        {
            if (_isFullscreen && !_engine.IsPaused)
            {
                _fsHideTimer.Stop();
                _fsHideTimer.Start();
            }
        };
        _fsControls.KeyForwarded += e => Window_PreviewKeyDown(this, e);
    }

    private void SyncFullscreenDockState()
    {
        _fsControls?.SyncState(
            _engine.Position,
            _engine.Duration,
            _engine.CacheDuration,
            _engine.IsPaused,
            _engine.Volume,
            _engine.IsMuted,
            _engine.Speed,
            AspectModes[_aspectIndex].ShortLabel);
    }

    private void PositionFullscreenDock()
    {
        if (_fsControls == null || !IsLoaded || StageContainer.ActualWidth <= 1) return;
        try
        {
            var source = PresentationSource.FromVisual(this);
            if (source?.CompositionTarget == null) return;
            Point topLeftPx = StageContainer.PointToScreen(new Point(0, 0));
            Point topLeftDip = source.CompositionTarget.TransformFromDevice.Transform(topLeftPx);
            _fsControls.PositionCenteredBottom(new Rect(topLeftDip.X, topLeftDip.Y, StageContainer.ActualWidth, StageContainer.ActualHeight));
        }
        catch { }
    }

    private void ShowFullscreenControls()
    {
        if (!_isFullscreen || _fsControls == null) return;
        _videoHost.SetCursorHidden(false);
        PositionFullscreenDock();
        SyncFullscreenDockState();
        _fsControls.ShowAnimated();
    }

    // ==================== TRUE WIN32 MONITOR FULLSCREEN (100% LOCKED EDGES + COVERS TASKBAR!) ====================

    private void ToggleFullscreen_Click(object sender, RoutedEventArgs e) => ToggleFullscreen();

    private void ToggleFullscreen()
    {
        IntPtr hwnd = new WindowInteropHelper(this).Handle;

        if (!_isFullscreen)
        {
            _preFsWindowState = WindowState;
            GetWindowRect(hwnd, out _preFsWinRect);
            _isFullscreen = true;

            // Keep TopBar, Playlist, AND docked BottomDeck collapsed for the ENTIRE Fullscreen session!
            // VideoHost stays 100% locked to full monitor size (e.g. 1920x1080) — ZERO edge shift on mouse move!
            TopBar.Visibility = Visibility.Collapsed;
            BottomDeck.Visibility = Visibility.Collapsed;
            if (_isPlaylistOpen)
            {
                _isPlaylistOpen = false;
                PlaylistDrawer.BeginAnimation(WidthProperty, null);
                PlaylistDrawer.Width = 0;
                PlaylistDrawer.Visibility = Visibility.Collapsed;
            }
            RootBorder.BorderThickness = new Thickness(0);

            // Set WindowStyle=None BEFORE detaching WindowChrome so Windows NEVER flashes a white native titlebar!
            WindowStyle = WindowStyle.None;
            ResizeMode = ResizeMode.NoResize;
            WindowChrome.SetWindowChrome(this, null);
            if (WindowState != WindowState.Normal)
                WindowState = WindowState.Normal;

            // Cover full physical monitor bounds (rcMonitor includes Windows Taskbar)
            IntPtr hMon = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
            var mi = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
            if (GetMonitorInfo(hMon, ref mi))
            {
                int w = mi.rcMonitor.Right - mi.rcMonitor.Left;
                int h = mi.rcMonitor.Bottom - mi.rcMonitor.Top;
                SetWindowPos(hwnd, HWND_TOP, mi.rcMonitor.Left, mi.rcMonitor.Top, w, h, SWP_FRAMECHANGED | SWP_SHOWWINDOW);
            }
            else
            {
                WindowState = WindowState.Maximized;
            }

            FullscreenIconPath.Data = FsExitGeo;

            // Snapshot physical cursor & start lightweight fullscreen cursor watcher + mpv native cursor autohide
            if (GetCursorPos(out POINT pt)) { _fsLastCursorX = pt.X; _fsLastCursorY = pt.Y; }
            _engine.SetCursorAutoHide(2000);
            _fsMousePollTimer.Start();

            // Keep compact floating bar hidden on entering Fullscreen until mouse actually moves
            HideFullscreenControls(instant: true, cooldownMs: 400);
        }
        else
        {
            _isFullscreen = false;
            _fsHideTimer.Stop();
            _fsMousePollTimer.Stop();
            _fsControls?.HideAnimated(instant: true);
            _engine.SetCursorAutoHide(0);
            _videoHost.SetCursorHidden(false);

            TopBar.Visibility = Visibility.Visible;
            BottomDeck.Opacity = 1;
            BottomDeck.Visibility = Visibility.Visible;

            // Re-attach WindowChrome BEFORE restoring SingleBorderWindow so no white native titlebar ever flashes!
            if (_savedWindowChrome != null)
                WindowChrome.SetWindowChrome(this, _savedWindowChrome);
            WindowStyle = WindowStyle.SingleBorderWindow;
            ResizeMode = ResizeMode.CanResize;

            int rw = Math.Max(820, _preFsWinRect.Right - _preFsWinRect.Left);
            int rh = Math.Max(520, _preFsWinRect.Bottom - _preFsWinRect.Top);
            SetWindowPos(hwnd, HWND_TOP, _preFsWinRect.Left, _preFsWinRect.Top, rw, rh, SWP_FRAMECHANGED | SWP_NOZORDER);

            WindowState = _preFsWindowState;
            RootBorder.BorderThickness = WindowState == WindowState.Maximized ? new Thickness(0) : new Thickness(1);
            FullscreenIconPath.Data = FsEnterGeo;
        }

        Dispatcher.BeginInvoke(() =>
        {
            if (_osdOverlay?.IsVisible == true) SyncOverlayBounds();
            if (_isFullscreen) PositionFullscreenDock();
        }, DispatcherPriority.Loaded);
    }

    private void HideFullscreenControls(bool instant = false, int cooldownMs = 250)
    {
        _fsHideTimer.Stop();
        _fsIgnoreMoveUntil = Environment.TickCount64 + cooldownMs;
        _videoHost.SyncCursorPosition();
        _videoHost.SetCursorHidden(true);
        _fsControls?.HideAnimated(instant);
    }

    private void OnFullscreenActivity()
    {
        if (!_isFullscreen) return;
        if (Environment.TickCount64 < _fsIgnoreMoveUntil) return;

        ShowFullscreenControls();

        _fsHideTimer.Stop();
        if (_fsControls is not { IsPointerOverDock: true } && !_engine.IsPaused)
            _fsHideTimer.Start();
    }

    private void BottomDeck_MouseEnter(object sender, MouseEventArgs e) { }
    private void BottomDeck_MouseLeave(object sender, MouseEventArgs e) { }

    private void Minimize_Click(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;

    private void MaxRestore_Click(object sender, RoutedEventArgs e)
        => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;

    private void Close_Click(object sender, RoutedEventArgs e) => Close();

    // ==================== DIALOGS & DRAG-DROP ====================

    private void OpenFileDialog(bool appendToPlaylist = false)
    {
        var dlg = new OpenFileDialog
        {
            Title = "Open Media File(s) or Playlist",
            Multiselect = true,
            Filter = "Media & Playlists|*.mp4;*.mkv;*.avi;*.webm;*.mov;*.wmv;*.flv;*.m4v;*.ts;*.m2ts;*.mpg;*.mpeg;*.mp3;*.flac;*.wav;*.ogg;*.opus;*.aac;*.m4a;*.xspf;*.m3u8;*.m3u|Playlists (*.xspf;*.m3u8;*.m3u)|*.xspf;*.m3u8;*.m3u|All Files|*.*"
        };

        if (dlg.ShowDialog(this) == true && dlg.FileNames.Length > 0)
        {
            if (dlg.FileNames.Length == 1 && PlaylistFileService.IsPlaylistFile(dlg.FileNames[0]))
            {
                var loaded = PlaylistFileService.Load(dlg.FileNames[0]);
                if (loaded.Count > 0)
                {
                    LoadParsedPlaylist(loaded, Path.GetFileName(dlg.FileNames[0]));
                    return;
                }
            }
            EnqueueAndPlay(dlg.FileNames, replacePlaylist: !appendToPlaylist);
        }
    }

    private void OpenSubtitleDialog()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Load External Subtitle",
            Filter = "Subtitles|*.srt;*.ass;*.ssa;*.vtt;*.sub;*.sup|All Files|*.*"
        };
        if (dlg.ShowDialog(this) == true)
        {
            _engine.AddExternalSubtitle(dlg.FileName);
            ShowOsd("Subtitle Loaded", Path.GetFileName(dlg.FileName));
        }
    }

    private void OpenUrlModal()
    {
        var dlg = new OpenUrlDialog { Owner = this };
        if (dlg.ShowDialog() == true && !string.IsNullOrWhiteSpace(dlg.StreamUrl))
            EnqueueAndPlay([dlg.StreamUrl], replacePlaylist: true);
    }

    private void Window_DragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void Window_Drop(object sender, DragEventArgs e)
    {
        if (e.Data.GetDataPresent(DataFormats.FileDrop) && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            HandleDroppedPaths(files);
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
