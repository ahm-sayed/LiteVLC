using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using System.Threading.Tasks;
using LiteVLC.Engine;

namespace LiteVLC.Services;

/// <summary>
/// Cuts/trims video & audio clips natively using the bundled libmpv-2.dll (works on EVERY PC with zero external tools)
/// or optional lossless ffmpeg stream copy when ffmpeg is installed on PATH.
/// </summary>
public static class VideoTrimService
{
    public static bool IsSystemFfmpegAvailable()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo
            {
                FileName = "ffmpeg",
                Arguments = "-version",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            });
            if (p == null) return false;
            p.WaitForExit(1500);
            return p.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public static Task TrimAsync(
        string inputPath,
        string outputPath,
        double startSeconds,
        double endSeconds,
        bool losslessCopyIfAvailable,
        IProgress<double> progress,
        CancellationToken ct)
    {
        if (endSeconds <= startSeconds + 0.1)
            throw new ArgumentException("End time must be greater than start time.");

        if (losslessCopyIfAvailable && IsSystemFfmpegAvailable())
            return TrimWithFfmpegCopyAsync(inputPath, outputPath, startSeconds, endSeconds, progress, ct);

        return Task.Run(() => TrimWithLibMpv(inputPath, outputPath, startSeconds, endSeconds, progress, ct), ct);
    }

    private static void TrimWithLibMpv(
        string inputPath,
        string outputPath,
        double startSeconds,
        double endSeconds,
        IProgress<double> progress,
        CancellationToken ct)
    {
        IntPtr handle = MpvNative.mpv_create();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to initialize libmpv trim worker.");

        try
        {
            double clipLen = Math.Max(0.1, endSeconds - startSeconds);
            bool isAudioOnly = outputPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase)
                            || outputPath.EndsWith(".aac", StringComparison.OrdinalIgnoreCase)
                            || outputPath.EndsWith(".m4a", StringComparison.OrdinalIgnoreCase)
                            || outputPath.EndsWith(".wav", StringComparison.OrdinalIgnoreCase)
                            || outputPath.EndsWith(".flac", StringComparison.OrdinalIgnoreCase);

            MpvNative.mpv_set_option_string(handle, "start", startSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            MpvNative.mpv_set_option_string(handle, "end", endSeconds.ToString("0.###", CultureInfo.InvariantCulture));
            MpvNative.mpv_set_option_string(handle, "o", outputPath);
            MpvNative.mpv_set_option_string(handle, "oac", outputPath.EndsWith(".mp3", StringComparison.OrdinalIgnoreCase) ? "libmp3lame" : "aac");

            if (isAudioOnly)
            {
                MpvNative.mpv_set_option_string(handle, "vid", "no");
            }
            else
            {
                MpvNative.mpv_set_option_string(handle, "ovc", "libx264");
                MpvNative.mpv_set_option_string(handle, "ovcopts", "preset=veryfast,crf=18");
            }

            MpvNative.mpv_set_option_string(handle, "config", "no");
            MpvNative.mpv_set_option_string(handle, "load-scripts", "no");

            int initRc = MpvNative.mpv_initialize(handle);
            if (initRc < 0)
                throw new InvalidOperationException($"Trim init failed: {MpvNative.GetErrorMessage(initRc)}");

            MpvNative.mpv_observe_property(handle, 1, "time-pos", MpvFormat.Double);
            MpvNative.CommandUtf8(handle, false, "loadfile", inputPath, "replace");

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    MpvNative.CommandUtf8(handle, true, "quit");
                    ct.ThrowIfCancellationRequested();
                }

                IntPtr evPtr = MpvNative.mpv_wait_event(handle, 0.15);
                if (evPtr == IntPtr.Zero) continue;

                var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
                if (ev.EventId == MpvEventId.PropertyChange && ev.ReplyUserdata == 1 && ev.Data != IntPtr.Zero)
                {
                    var prop = Marshal.PtrToStructure<MpvEventProperty>(ev.Data);
                    if (prop.Format == MpvFormat.Double && prop.Data != IntPtr.Zero)
                    {
                        double t = Marshal.PtrToStructure<double>(prop.Data);
                        double pct = Math.Clamp((t - startSeconds) / clipLen, 0.0, 0.99);
                        progress.Report(pct);
                    }
                }
                else if (ev.EventId == MpvEventId.EndFile)
                {
                    if (ev.Data != IntPtr.Zero)
                    {
                        var endData = Marshal.PtrToStructure<MpvEventEndFile>(ev.Data);
                        if (endData.Reason == MpvEndFileReason.Error)
                            throw new InvalidOperationException($"Trim failed: {MpvNative.GetErrorMessage(endData.Error)}");
                    }
                    progress.Report(1.0);
                    break;
                }
                else if (ev.EventId == MpvEventId.Shutdown)
                {
                    break;
                }
            }
        }
        finally
        {
            MpvNative.mpv_terminate_destroy(handle);
        }

        if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
            throw new InvalidOperationException("Output file was not created.");
    }

    private static async Task TrimWithFfmpegCopyAsync(
        string inputPath,
        string outputPath,
        double startSeconds,
        double endSeconds,
        IProgress<double> progress,
        CancellationToken ct)
    {
        progress.Report(0.2);
        string startStr = startSeconds.ToString("0.###", CultureInfo.InvariantCulture);
        string durStr = (endSeconds - startSeconds).ToString("0.###", CultureInfo.InvariantCulture);

        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-y -ss {startStr} -i \"{inputPath}\" -t {durStr} -c copy -avoid_negative_ts make_zero \"{outputPath}\"",
            UseShellExecute = false,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi) ?? throw new InvalidOperationException("Failed to start ffmpeg.");
        string stderr = await proc.StandardError.ReadToEndAsync(ct).ConfigureAwait(false);
        await proc.WaitForExitAsync(ct).ConfigureAwait(false);

        if (proc.ExitCode != 0 || !File.Exists(outputPath))
            throw new InvalidOperationException($"Lossless trim failed (exit {proc.ExitCode}). Try Frame-Exact mode.");

        progress.Report(1.0);
    }
}
