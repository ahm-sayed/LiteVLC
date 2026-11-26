using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.Json;
using System.Threading;
using LiteVLC.Models;

namespace LiteVLC.Engine;

public sealed class MpvEngine : IDisposable
{
    private IntPtr _handle;
    private Thread? _eventThread;
    private volatile bool _running;
    private volatile bool _disposed;
    private readonly object _lock = new();

    // Live throttled seek state (leading-edge immediate visual preview + trailing accuracy)
    private readonly object _seekLock = new();
    private double _osdAccumulatedDelta;
    private double _liveSeekTarget;
    private double _lastDispatchedSeekTarget = double.NaN;
    private long _lastSeekDispatchMs;
    private long _lastSeekInputMs;
    private bool _hasTrailingSeek;
    private readonly Timer _seekCoalesceTimer;
    private long _seekStartTicks;

    // WASAPI audio health & long-pause / sleep / device-change recovery state
    private long _pausedAtTicks;
    private long _lastAudioReloadTicks;
    private volatile bool _audioNeedsReload;
    private readonly Timer _deviceReloadDebounceTimer;

    // Live cached playback state
    public double Position { get; private set; }
    public double Duration { get; private set; }
    public double CacheDuration { get; private set; }
    public bool IsPaused { get; private set; } = true;
    public bool IsMuted { get; private set; }
    public double Volume { get; private set; } = 100;
    public double Speed { get; private set; } = 1.0;
    public bool FastSeekEnabled { get; private set; } = true;
    public bool AudioBoostEnabled { get; private set; } = true;
    public double SubDelay { get; private set; }
    public double SubSpeed { get; private set; } = 1.0;
    public bool HasMedia { get; private set; }
    public bool HasVideo { get; private set; }
    public string CurrentTitle { get; private set; } = "";
    public double LastSeekMs { get; private set; }

    public string ScreenshotDirectory { get; }

    // Events (invoked from event pump thread — UI marshals via Dispatcher)
    public event Action<double>? PositionChanged;
    public event Action<double>? DurationChanged;
    public event Action<double>? CacheDurationChanged;
    public event Action<bool>? PauseChanged;
    public event Action<double>? VolumeChanged;
    public event Action<bool>? MuteChanged;
    public event Action<double>? SpeedChanged;
    public event Action<string>? MediaTitleChanged;
    public event Action? FileLoaded;
    public event Action<bool>? VideoOutputChanged;
    public event Action? FileLoadFailed;
    public event Action<bool>? FileEnded; // true = normal EOF
    public event Action? TracksChanged;
    public event Action? SubtitleTrackChanged;
    public event Action<double, double>? SeekPreview; // (accumulatedDelta, predictedPos)
    public event Action<double>? SeekCompleted; // elapsedMs

    private const ulong PropTimePos = 1;
    private const ulong PropDuration = 2;
    private const ulong PropPause = 3;
    private const ulong PropVolume = 4;
    private const ulong PropMute = 5;
    private const ulong PropSpeed = 6;
    private const ulong PropMediaTitle = 7;
    private const ulong PropCacheDuration = 8;
    private const ulong PropTrackListCount = 9;
    private const ulong PropSid = 10;

    public MpvEngine()
    {
        ScreenshotDirectory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.MyPictures),
            "LiteVLC");
        Directory.CreateDirectory(ScreenshotDirectory);

        _seekCoalesceTimer = new Timer(_ => FlushPendingSeek(), null, Timeout.Infinite, Timeout.Infinite);
        _deviceReloadDebounceTimer = new Timer(_ =>
        {
            if (!_disposed && HasMedia && !IsPaused)
                ReloadAudioOutput();
        }, null, Timeout.Infinite, Timeout.Infinite);
    }

    public void Initialize(IntPtr hwnd, double initialVolume = 100, bool fastSeek = true, bool audioBoost = true)
    {
        lock (_lock)
        {
            if (_handle != IntPtr.Zero) return;

            _handle = MpvNative.mpv_create();
            if (_handle == IntPtr.Zero)
                throw new InvalidOperationException("Failed to create libmpv instance. Ensure libmpv-2.dll is present.");

            FastSeekEnabled = fastSeek;
            AudioBoostEnabled = audioBoost;
            Volume = Math.Clamp(initialVolume, 0, 200);

            // Bind directly to Win32 HWND for zero-copy hardware rendering
            long wid = hwnd.ToInt64();
            Check(MpvNative.mpv_set_option(_handle, "wid", MpvFormat.Int64, ref wid), "wid");

            // --- Core Performance & Zero-Lag / Zero-Flicker Profile ---
            SetOpt("vo", "gpu");
            SetOpt("gpu-api", "d3d11");
            SetOpt("d3d11-flip", "yes");
            SetOpt("d3d11-sync-refresh", "no");
            SetOpt("framedrop", "vo");
            SetOpt("hwdec", "auto-safe");
            SetOpt("hwdec-codecs", "all");

            // Loud, clear VLC-parity WASAPI audio (fixes quiet 5.1->stereo downmix & ReplayGain attenuation).
            // Keep audio-stream-silence=no + audio-fallback-to-null=yes so long pauses / monitor sleep / BT idle
            // NEVER permanently invalidate the WASAPI IAudioClient session!
            SetOpt("ao", "wasapi");
            SetOpt("audio-stream-silence", "no");
            SetOpt("audio-fallback-to-null", "yes");
            SetOpt("audio-wait-open", "0");
            SetOpt("video-sync", "audio");
            SetOpt("audio-pitch-correction", "yes");
            SetOpt("audio-normalize-downmix", "yes");
            SetOpt("ad-lavc-downmix", "yes");
            SetOpt("replaygain", "no");
            SetOpt("volume-max", "200");
            SetOpt("volume-gain", audioBoost ? "6.0" : "4.5");
            if (audioBoost)
                SetOpt("af", "lavfi=[dynaudnorm=f=150:g=15:p=0.92:m=8]");

            // Smart RAM demuxer cache (forward + backward) so ±10s seeks hit RAM instantly while keeping RAM lean
            SetOpt("cache", "yes");
            SetOpt("demuxer-max-bytes", "48MiB");
            SetOpt("demuxer-max-back-bytes", "20MiB");
            SetOpt("demuxer-readahead-secs", "15");

            // Keyframe fast seeking by default (zero intermediate-frame stall)
            SetOpt("hr-seek", fastSeek ? "no" : "yes");
            SetOpt("hr-seek-framedrop", "yes");

            // Clean embedded behavior (we provide our own sleek WPF UI + OSD)
            SetOpt("idle", "yes");
            SetOpt("keep-open", "yes");
            SetOpt("osc", "no");
            SetOpt("osd-level", "0");
            SetOpt("input-default-bindings", "no");
            SetOpt("input-vo-keyboard", "no");
            SetOpt("cursor-autohide", "no");

            // VLC-parity features: volume up to 200%, fuzzy subtitle loading, clean subtitle styling
            SetOpt("volume", Volume.ToString(CultureInfo.InvariantCulture));
            SetOpt("sub-auto", "fuzzy");
            SetOpt("sub-font", "Segoe UI");
            SetOpt("sub-font-size", "44");
            SetOpt("sub-margin-y", "38");
            SetOpt("sub-border-size", "2.5");
            SetOpt("sub-shadow-offset", "1");
            SetOpt("sub-codepage", "auto");
            SetOpt("screenshot-directory", ScreenshotDirectory);
            SetOpt("screenshot-format", "png");
            SetOpt("screenshot-template", "LiteVLC_%F_%P");

            // Avoid heavy unnecessary startup probes
            SetOpt("config", "no");
            SetOpt("load-scripts", "no");
            SetOpt("ytdl", "no");

            Check(MpvNative.mpv_initialize(_handle), "mpv_initialize");

            // Observe essential properties reactively (no UI polling!)
            MpvNative.mpv_observe_property(_handle, PropTimePos, "time-pos", MpvFormat.Double);
            MpvNative.mpv_observe_property(_handle, PropDuration, "duration", MpvFormat.Double);
            MpvNative.mpv_observe_property(_handle, PropPause, "pause", MpvFormat.Flag);
            MpvNative.mpv_observe_property(_handle, PropVolume, "volume", MpvFormat.Double);
            MpvNative.mpv_observe_property(_handle, PropMute, "mute", MpvFormat.Flag);
            MpvNative.mpv_observe_property(_handle, PropSpeed, "speed", MpvFormat.Double);
            MpvNative.mpv_observe_property(_handle, PropMediaTitle, "media-title", MpvFormat.String);
            MpvNative.mpv_observe_property(_handle, PropCacheDuration, "demuxer-cache-duration", MpvFormat.Double);
            MpvNative.mpv_observe_property(_handle, PropTrackListCount, "track-list/count", MpvFormat.Int64);
            MpvNative.mpv_observe_property(_handle, PropSid, "sid", MpvFormat.String);

            _running = true;
            _eventThread = new Thread(EventLoop)
            {
                Name = "LiteVLC-MpvEventPump",
                IsBackground = true
            };
            _eventThread.Start();
        }
    }

    private void SetOpt(string name, string value)
    {
        if (_handle == IntPtr.Zero) return;
        MpvNative.mpv_set_option_string(_handle, name, value);
    }

    private static void Check(int status, string context)
    {
        if (status < 0)
            throw new InvalidOperationException($"{context} failed: {MpvNative.GetErrorMessage(status)}");
    }

    public void LoadFile(string path, double? resumeSeconds = null)
    {
        if (_handle == IntPtr.Zero || string.IsNullOrWhiteSpace(path)) return;
        CancelPendingSeek();
        ResetSubDelay();
        HasMedia = true;

        if (resumeSeconds is > 2.0)
        {
            string startOpt = $"start={resumeSeconds.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
            MpvNative.CommandUtf8(_handle, true, "loadfile", path, "replace", "-1", startOpt);
        }
        else
        {
            MpvNative.CommandUtf8(_handle, true, "loadfile", path, "replace");
        }
        SetPause(false);
    }

    public void Stop()
    {
        if (_handle == IntPtr.Zero) return;
        CancelPendingSeek();
        ResetSubDelay();
        HasMedia = false;
        Position = 0;
        Duration = 0;
        CacheDuration = 0;
        MpvNative.CommandUtf8(_handle, true, "stop");
    }

    public void TogglePause()
    {
        if (_handle == IntPtr.Zero || !HasMedia) return;
        SetPause(!IsPaused);
    }

    public void SetPause(bool paused)
    {
        if (_handle == IntPtr.Zero) return;
        if (paused)
        {
            if (_pausedAtTicks == 0)
                _pausedAtTicks = Environment.TickCount64;
        }
        int flag = paused ? 1 : 0;
        MpvNative.mpv_set_property(_handle, "pause", MpvFormat.Flag, ref flag);
    }

    /// <summary>
    /// Marks WASAPI audio for clean reload (e.g., after PC sleep/wake or headphone/HDMI plug/unplug).
    /// Reloads immediately if playing, or right upon unpausing if currently paused.
    /// </summary>
    public void MarkAudioNeedsReload()
    {
        if (_disposed || _handle == IntPtr.Zero || !HasMedia) return;
        _audioNeedsReload = true;
        if (!IsPaused)
            _deviceReloadDebounceTimer.Change(450, Timeout.Infinite);
    }

    /// <summary>
    /// Cleanly re-initializes the WASAPI audio output without interrupting video playback or losing position.
    /// </summary>
    public void ReloadAudioOutput(bool bypassCooldown = false)
    {
        if (_disposed || _handle == IntPtr.Zero || !HasMedia) return;
        long now = Environment.TickCount64;
        if (!bypassCooldown && now - _lastAudioReloadTicks < 1500)
            return;
        _lastAudioReloadTicks = now;
        _audioNeedsReload = false;

        MpvNative.CommandUtf8(_handle, true, "ao-reload");

        // Re-assert volume & verify WASAPI re-attached cleanly after 450ms (e.g. waking HDMI/BT endpoint)
        double vol = Volume;
        MpvNative.mpv_set_property_async(_handle, 0, "volume", MpvFormat.Double, ref vol);

        _ = System.Threading.Tasks.Task.Run(async () =>
        {
            await System.Threading.Tasks.Task.Delay(450).ConfigureAwait(false);
            if (_disposed || _handle == IntPtr.Zero || !HasMedia || IsPaused) return;
            string? ao = GetStringProperty("current-ao");
            if ((string.IsNullOrEmpty(ao) || ao.Equals("null", StringComparison.OrdinalIgnoreCase)) &&
                GetActiveAudioTrack() != null)
            {
                MpvNative.CommandUtf8(_handle, true, "ao-reload");
            }
        });
    }

    /// <summary>
    /// Live visual relative seek (e.g. +10s / -10s or seekbar wheel).
    /// Seeks immediately on first press and updates frames live every ~70ms while holding/tapping
    /// so you see the scene move in real time without choking the decoder.
    /// </summary>
    public void SeekRelative(double deltaSeconds)
    {
        if (_handle == IntPtr.Zero || !HasMedia) return;

        double accumulated;
        double target;
        bool dispatchNow;

        lock (_seekLock)
        {
            long now = Environment.TickCount64;
            bool isContinuingBurst = (now - _lastSeekInputMs) <= 650 && !double.IsNaN(_lastDispatchedSeekTarget);
            _lastSeekInputMs = now;

            if (!isContinuingBurst)
            {
                _osdAccumulatedDelta = 0;
                _liveSeekTarget = Position;
            }

            _osdAccumulatedDelta += deltaSeconds;
            _liveSeekTarget = Duration > 0
                ? Math.Clamp(_liveSeekTarget + deltaSeconds, 0, Duration)
                : Math.Max(0, _liveSeekTarget + deltaSeconds);

            accumulated = _osdAccumulatedDelta;
            target = _liveSeekTarget;

            // Leading-edge dispatch: show the new scene immediately if >= 70ms passed since last seek!
            if (now - _lastSeekDispatchMs >= 70)
            {
                _lastSeekDispatchMs = now;
                _lastDispatchedSeekTarget = target;
                _hasTrailingSeek = false;
                _seekCoalesceTimer.Change(Timeout.Infinite, Timeout.Infinite);
                dispatchNow = true;
            }
            else
            {
                // Queue a trailing flush so the final target always lands accurately
                _hasTrailingSeek = true;
                int remaining = (int)Math.Clamp(70 - (now - _lastSeekDispatchMs), 20, 70);
                _seekCoalesceTimer.Change(remaining, Timeout.Infinite);
                dispatchNow = false;
            }
        }

        SeekPreview?.Invoke(accumulated, target);

        if (dispatchNow)
            DispatchAbsoluteSeek(target, forceFast: true);
    }

    private void FlushPendingSeek()
    {
        double target;
        lock (_seekLock)
        {
            if (!_hasTrailingSeek || _disposed || _handle == IntPtr.Zero) return;
            target = _liveSeekTarget;
            _hasTrailingSeek = false;
            if (!double.IsNaN(_lastDispatchedSeekTarget) && Math.Abs(target - _lastDispatchedSeekTarget) < 0.05)
                return;
            _lastSeekDispatchMs = Environment.TickCount64;
            _lastDispatchedSeekTarget = target;
        }

        DispatchAbsoluteSeek(target, forceFast: false);
    }

    private void DispatchAbsoluteSeek(double targetSeconds, bool forceFast)
    {
        if (_handle == IntPtr.Zero) return;
        Interlocked.Exchange(ref _seekStartTicks, Stopwatch.GetTimestamp());
        string mode = (forceFast || FastSeekEnabled) ? "absolute+keyframes" : "absolute+exact";
        MpvNative.CommandUtf8(_handle, true, "seek", targetSeconds.ToString("0.###", CultureInfo.InvariantCulture), mode);
    }

    private void CancelPendingSeek()
    {
        lock (_seekLock)
        {
            _seekCoalesceTimer.Change(Timeout.Infinite, Timeout.Infinite);
            _osdAccumulatedDelta = 0;
            _hasTrailingSeek = false;
            _lastDispatchedSeekTarget = double.NaN;
        }
    }

    /// <summary>
    /// Direct absolute seek (used by timeline slider click/drag).
    /// </summary>
    public void SeekAbsolute(double targetSeconds, bool forceFast = false)
    {
        if (_handle == IntPtr.Zero || !HasMedia) return;
        CancelPendingSeek();
        double clamped = Duration > 0 ? Math.Clamp(targetSeconds, 0, Duration) : Math.Max(0, targetSeconds);
        Interlocked.Exchange(ref _seekStartTicks, Stopwatch.GetTimestamp());
        string mode = (forceFast || FastSeekEnabled) ? "absolute+keyframes" : "absolute+exact";
        MpvNative.CommandUtf8(_handle, true, "seek", clamped.ToString("0.###", CultureInfo.InvariantCulture), mode);
    }

    public void FrameStep()
    {
        if (_handle == IntPtr.Zero || !HasMedia) return;
        MpvNative.CommandUtf8(_handle, true, "frame-step");
    }

    public void FrameBackStep()
    {
        if (_handle == IntPtr.Zero || !HasMedia) return;
        MpvNative.CommandUtf8(_handle, true, "frame-back-step");
    }

    public void SetFastSeek(bool enabled)
    {
        FastSeekEnabled = enabled;
        if (_handle == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_handle, "hr-seek", enabled ? "no" : "yes");
    }

    public void SetVolume(double volume)
    {
        double clamped = Math.Clamp(volume, 0, 200);
        Volume = clamped;
        if (_handle == IntPtr.Zero) return;
        MpvNative.mpv_set_property_async(_handle, 0, "volume", MpvFormat.Double, ref clamped);
    }

    public void SetAudioBoost(bool enabled)
    {
        AudioBoostEnabled = enabled;
        if (_handle == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_handle, "volume-gain", enabled ? "6.0" : "4.5");
        MpvNative.mpv_set_property_string(_handle, "af", enabled ? "lavfi=[dynaudnorm=f=150:g=15:p=0.92:m=8]" : "");
    }

    public double AdjustSubDelay(double deltaSeconds)
    {
        SubDelay = Math.Round(SubDelay + deltaSeconds, 2);
        if (_handle != IntPtr.Zero)
        {
            double val = SubDelay;
            MpvNative.mpv_set_property(_handle, "sub-delay", MpvFormat.Double, ref val);
        }
        return SubDelay;
    }

    public void SetSubSync(double delaySeconds, double speedScale = 1.0)
    {
        SubDelay = Math.Round(delaySeconds, 2);
        SubSpeed = Math.Clamp(speedScale, 0.5, 2.0);
        if (_handle != IntPtr.Zero)
        {
            double spd = SubSpeed;
            double dly = SubDelay;
            MpvNative.mpv_set_property(_handle, "sub-speed", MpvFormat.Double, ref spd);
            MpvNative.mpv_set_property(_handle, "sub-delay", MpvFormat.Double, ref dly);
        }
    }

    public void ResetSubDelay()
    {
        SetSubSync(0.0, 1.0);
    }

    public void ReloadExternalSubtitles()
    {
        if (_handle == IntPtr.Zero) return;
        MpvNative.CommandUtf8(_handle, true, "sub-reload");
    }

    public MediaTrack? GetActiveSubtitleTrack()
    {
        foreach (var t in GetTracks())
        {
            if (t.Type == "sub" && t.Selected)
                return t;
        }
        return null;
    }

    public MediaTrack? GetActiveAudioTrack()
    {
        foreach (var t in GetTracks())
        {
            if (t.Type == "audio" && t.Selected)
                return t;
        }
        return null;
    }

    public void AdjustVolume(double delta) => SetVolume(Volume + delta);

    public void ToggleMute()
    {
        if (_handle == IntPtr.Zero) return;
        int flag = IsMuted ? 0 : 1;
        MpvNative.mpv_set_property(_handle, "mute", MpvFormat.Flag, ref flag);
    }

    public void SetSpeed(double speed)
    {
        double clamped = Math.Round(Math.Clamp(speed, 0.25, 4.0), 2);
        Speed = clamped;
        if (_handle == IntPtr.Zero) return;
        MpvNative.mpv_set_property_async(_handle, 0, "speed", MpvFormat.Double, ref clamped);
    }

    public void SetAudioTrack(int? trackId)
    {
        if (_handle == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_handle, "aid", trackId.HasValue ? trackId.Value.ToString(CultureInfo.InvariantCulture) : "no");
        TracksChanged?.Invoke();
    }

    public void SetSubtitleTrack(int? trackId)
    {
        if (_handle == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_handle, "sid", trackId.HasValue ? trackId.Value.ToString(CultureInfo.InvariantCulture) : "no");
        TracksChanged?.Invoke();
    }

    public void AddExternalSubtitle(string subtitlePath)
    {
        if (_handle == IntPtr.Zero || !File.Exists(subtitlePath)) return;
        MpvNative.CommandUtf8(_handle, false, "sub-add", subtitlePath, "select");
        TracksChanged?.Invoke();
    }

    public void CycleAudioTrack()
    {
        if (_handle == IntPtr.Zero || !HasMedia) return;
        MpvNative.CommandUtf8(_handle, true, "cycle", "audio");
    }

    public void CycleSubtitleTrack()
    {
        if (_handle == IntPtr.Zero || !HasMedia) return;
        MpvNative.CommandUtf8(_handle, true, "cycle", "sub");
    }

    public void SetAspectRatio(string ratio)
    {
        if (_handle == IntPtr.Zero) return;
        // ratio: "-1" (Auto), "16:9", "4:3", "21:9", "1:1"
        MpvNative.mpv_set_property_string(_handle, "video-aspect-override", ratio);
    }

    public void SetKeepAspect(bool keep)
    {
        if (_handle == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_handle, "keepaspect", keep ? "yes" : "no");
    }

    public void SetPanScan(double panscan)
    {
        if (_handle == IntPtr.Zero) return;
        double clamped = Math.Clamp(panscan, 0.0, 1.0);
        MpvNative.mpv_set_property(_handle, "panscan", MpvFormat.Double, ref clamped);
    }

    public void SetCursorAutoHide(int milliseconds)
    {
        if (_handle == IntPtr.Zero) return;
        MpvNative.mpv_set_property_string(_handle, "cursor-autohide",
            milliseconds <= 0 ? "no" : milliseconds.ToString(CultureInfo.InvariantCulture));
    }

    public string? TakeScreenshot()
    {
        if (_handle == IntPtr.Zero || !HasMedia) return null;
        string file = Path.Combine(ScreenshotDirectory, $"LiteVLC_{DateTime.Now:yyyyMMdd_HHmmss}.png");
        int rc = MpvNative.CommandUtf8(_handle, false, "screenshot-to-file", file, "subtitles");
        return rc >= 0 ? file : null;
    }

    public List<MediaTrack> GetTracks()
    {
        var list = new List<MediaTrack>();
        if (_handle == IntPtr.Zero) return list;

        string? json = GetStringProperty("track-list");
        if (string.IsNullOrWhiteSpace(json)) return list;

        try
        {
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;
            foreach (var el in doc.RootElement.EnumerateArray())
            {
                int id = el.TryGetProperty("id", out var idProp) ? idProp.GetInt32() : 0;
                string type = el.TryGetProperty("type", out var typeProp) ? (typeProp.GetString() ?? "") : "";
                string title = el.TryGetProperty("title", out var tProp) ? (tProp.GetString() ?? "") : "";
                string lang = el.TryGetProperty("lang", out var lProp) ? (lProp.GetString() ?? "") : "";
                string codec = el.TryGetProperty("codec", out var cProp) ? (cProp.GetString() ?? "") : "";
                bool selected = el.TryGetProperty("selected", out var sProp) && sProp.ValueKind == JsonValueKind.True;
                bool external = el.TryGetProperty("external", out var eProp) && eProp.ValueKind == JsonValueKind.True;
                string extFile = el.TryGetProperty("external-filename", out var efProp) && efProp.ValueKind == JsonValueKind.String
                    ? (efProp.GetString() ?? "")
                    : "";
                int ffIndex = el.TryGetProperty("ff-index", out var ffProp) && ffProp.ValueKind == JsonValueKind.Number
                    ? ffProp.GetInt32()
                    : -1;

                list.Add(new MediaTrack
                {
                    Id = id,
                    Type = type,
                    Title = title,
                    Lang = lang,
                    Codec = codec,
                    Selected = selected,
                    External = external,
                    ExternalFilename = extFile,
                    FfIndex = ffIndex
                });
            }
        }
        catch
        {
            // Ignore malformed track-list transitions during load
        }
        return list;
    }

    public PlaybackStats GetStatsSnapshot()
    {
        var stats = new PlaybackStats
        {
            CacheSeconds = CacheDuration,
            LastSeekMs = LastSeekMs,
            FastSeekEnabled = FastSeekEnabled
        };

        if (_handle == IntPtr.Zero || !HasMedia) return stats;

        stats.VideoCodec = GetStringProperty("video-codec-name") ?? GetStringProperty("video-format") ?? "—";
        stats.AudioCodec = GetStringProperty("audio-codec-name") ?? "—";
        stats.HwDec = GetStringProperty("hwdec-current") ?? "software";
        if (string.IsNullOrWhiteSpace(stats.HwDec) || stats.HwDec == "no")
            stats.HwDec = "software";

        long w = GetInt64Property("width");
        long h = GetInt64Property("height");
        stats.Resolution = (w > 0 && h > 0) ? $"{w}×{h}" : "—";

        stats.Fps = GetDoubleProperty("estimated-vf-fps");
        stats.AvSyncMs = GetDoubleProperty("avsync") * 1000.0;
        stats.DroppedFrames = GetInt64Property("frame-drop-count");
        return stats;
    }

    /// <summary>
    /// Probes real stream quality, exact container FPS, codec, bitrate, duration, and hidden container metadata
    /// directly from libmpv so subtitle search stays accurate even when a file has been renamed.
    /// </summary>
    public MediaProbeInfo GetMediaProbeInfo()
    {
        if (_handle == IntPtr.Zero || !HasMedia)
            return new MediaProbeInfo();

        double fps = GetDoubleProperty("container-fps");
        if (fps <= 1.0) fps = GetDoubleProperty("estimated-vf-fps");

        string titleMeta = GetStringProperty("metadata/by-key/title") ?? "";
        string encoderMeta = GetStringProperty("metadata/by-key/encoder")
                          ?? GetStringProperty("metadata/by-key/comment")
                          ?? "";

        return new MediaProbeInfo
        {
            Width = GetInt64Property("width"),
            Height = GetInt64Property("height"),
            Fps = fps > 0 ? Math.Round(fps, 3) : 0,
            DurationSeconds = Duration > 0 ? Duration : GetDoubleProperty("duration"),
            VideoCodec = GetStringProperty("video-codec-name") ?? GetStringProperty("video-format") ?? "",
            VideoBitrateKbps = GetDoubleProperty("video-bitrate") / 1000.0,
            AudioChannels = GetInt64Property("audio-params/channel-count"),
            EmbeddedTitle = titleMeta.Trim(),
            EmbeddedEncoder = encoderMeta.Trim()
        };
    }

    private string? GetStringProperty(string name)
    {
        if (_handle == IntPtr.Zero) return null;
        IntPtr ptr = MpvNative.mpv_get_property_string(_handle, name);
        if (ptr == IntPtr.Zero) return null;
        try
        {
            return MpvNative.PtrToUtf8(ptr);
        }
        finally
        {
            MpvNative.mpv_free(ptr);
        }
    }

    private double GetDoubleProperty(string name)
    {
        if (_handle == IntPtr.Zero) return 0;
        return MpvNative.mpv_get_property(_handle, name, MpvFormat.Double, out double val) >= 0 ? val : 0;
    }

    private long GetInt64Property(string name)
    {
        if (_handle == IntPtr.Zero) return 0;
        return MpvNative.mpv_get_property(_handle, name, MpvFormat.Int64, out long val) >= 0 ? val : 0;
    }

    private void EventLoop()
    {
        long lastTimeUiPush = 0;

        while (_running && _handle != IntPtr.Zero)
        {
            IntPtr evPtr = MpvNative.mpv_wait_event(_handle, 0.25);
            if (evPtr == IntPtr.Zero) continue;

            var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
            switch (ev.EventId)
            {
                case MpvEventId.None:
                    break;

                case MpvEventId.Shutdown:
                    _running = false;
                    break;

                case MpvEventId.FileLoaded:
                {
                    HasMedia = true;
                    bool hasVid = GetInt64Property("width") > 0 && GetInt64Property("height") > 0;
                    HasVideo = hasVid;
                    FileLoaded?.Invoke();
                    VideoOutputChanged?.Invoke(hasVid);
                    TracksChanged?.Invoke();
                    break;
                }

                case MpvEventId.VideoReconfig:
                {
                    bool hasVid = GetInt64Property("width") > 0 && GetInt64Property("height") > 0;
                    if (HasVideo != hasVid)
                    {
                        HasVideo = hasVid;
                        VideoOutputChanged?.Invoke(hasVid);
                    }
                    break;
                }

                case MpvEventId.EndFile:
                    HasVideo = false;
                    if (ev.Data != IntPtr.Zero)
                    {
                        var endData = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                        bool isEof = endData.Reason == MpvEndFileReason.Eof;
                        if (endData.Reason == MpvEndFileReason.Stop || endData.Reason == MpvEndFileReason.Error)
                            HasMedia = false;
                        if (endData.Reason == MpvEndFileReason.Error)
                            FileLoadFailed?.Invoke();
                        FileEnded?.Invoke(isEof);
                    }
                    break;

                case MpvEventId.PlaybackRestart:
                {
                    long start = Interlocked.Exchange(ref _seekStartTicks, 0);
                    if (start > 0)
                    {
                        double ms = (Stopwatch.GetTimestamp() - start) * 1000.0 / Stopwatch.Frequency;
                        LastSeekMs = Math.Round(ms, 1);
                        SeekCompleted?.Invoke(LastSeekMs);
                    }
                    break;
                }

                case MpvEventId.PropertyChange:
                    if (ev.Data != IntPtr.Zero)
                        HandlePropertyChange(ev.ReplyUserdata, ev.Data, ref lastTimeUiPush);
                    break;
            }
        }
    }

    private void HandlePropertyChange(ulong userdata, IntPtr propPtr, ref long lastTimeUiPush)
    {
        var prop = Marshal.PtrToStructure<MpvEventProperty>(propPtr);
        if (prop.Data == IntPtr.Zero || prop.Format == MpvFormat.None) return;

        switch (userdata)
        {
            case PropTimePos when prop.Format == MpvFormat.Double:
            {
                double val = Marshal.PtrToStructure<double>(prop.Data);
                Position = val;
                // Throttle UI updates to ~15 Hz so WPF stays feather-light
                long now = Environment.TickCount64;
                if (now - lastTimeUiPush >= 65)
                {
                    lastTimeUiPush = now;
                    PositionChanged?.Invoke(val);
                }
                break;
            }

            case PropDuration when prop.Format == MpvFormat.Double:
                Duration = Marshal.PtrToStructure<double>(prop.Data);
                DurationChanged?.Invoke(Duration);
                break;

            case PropCacheDuration when prop.Format == MpvFormat.Double:
                CacheDuration = Marshal.PtrToStructure<double>(prop.Data);
                CacheDurationChanged?.Invoke(CacheDuration);
                break;

            case PropPause when prop.Format == MpvFormat.Flag:
            {
                bool nowPaused = Marshal.ReadInt32(prop.Data) != 0;
                bool wasPaused = IsPaused;
                IsPaused = nowPaused;

                if (nowPaused)
                {
                    if (_pausedAtTicks == 0)
                        _pausedAtTicks = Environment.TickCount64;
                }
                else if (wasPaused && HasMedia)
                {
                    long pausedMs = _pausedAtTicks > 0 ? (Environment.TickCount64 - _pausedAtTicks) : 0;
                    _pausedAtTicks = 0;

                    string? currentAo = GetStringProperty("current-ao");
                    bool aoDead = string.IsNullOrEmpty(currentAo) ||
                                  currentAo.Equals("null", StringComparison.OrdinalIgnoreCase);

                    // After a long pause (>=15s), Windows sleep, device switch, or dead WASAPI endpoint,
                    // transparently reload WASAPI (~12ms) so audio NEVER stays silent on resume!
                    if (_audioNeedsReload || aoDead || pausedMs >= 15_000)
                        ReloadAudioOutput();
                }
                else
                {
                    _pausedAtTicks = 0;
                }

                PauseChanged?.Invoke(IsPaused);
                break;
            }

            case PropVolume when prop.Format == MpvFormat.Double:
                Volume = Marshal.PtrToStructure<double>(prop.Data);
                VolumeChanged?.Invoke(Volume);
                break;

            case PropMute when prop.Format == MpvFormat.Flag:
                IsMuted = Marshal.ReadInt32(prop.Data) != 0;
                MuteChanged?.Invoke(IsMuted);
                break;

            case PropSpeed when prop.Format == MpvFormat.Double:
                Speed = Marshal.PtrToStructure<double>(prop.Data);
                SpeedChanged?.Invoke(Speed);
                break;

            case PropMediaTitle when prop.Format == MpvFormat.String:
                IntPtr strPtr = Marshal.ReadIntPtr(prop.Data);
                CurrentTitle = MpvNative.PtrToUtf8(strPtr) ?? "";
                MediaTitleChanged?.Invoke(CurrentTitle);
                break;

            case PropTrackListCount:
                TracksChanged?.Invoke();
                break;

            case PropSid:
                SubtitleTrackChanged?.Invoke();
                break;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _running = false;
        _seekCoalesceTimer.Dispose();
        _deviceReloadDebounceTimer.Dispose();

        IntPtr h;
        lock (_lock)
        {
            h = _handle;
            _handle = IntPtr.Zero;
        }

        if (h != IntPtr.Zero)
        {
            // Terminate libmpv off the UI thread so any VO SendMessage to the child HWND never deadlocks.
            var cleanup = new System.Threading.Tasks.Task(() =>
            {
                try
                {
                    MpvNative.mpv_wakeup(h);
                    _eventThread?.Join(500);
                }
                catch { }

                try
                {
                    MpvNative.mpv_terminate_destroy(h);
                }
                catch { }
            });
            cleanup.Start();
            cleanup.Wait(600);
        }
    }
}
