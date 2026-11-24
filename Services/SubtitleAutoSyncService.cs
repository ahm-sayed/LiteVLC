using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LiteVLC.Engine;
using LiteVLC.Models;

namespace LiteVLC.Services;

public sealed class VoiceFingerprint
{
    public string VideoPath { get; init; } = "";
    public long FileSize { get; init; }
    public int AudioTrackId { get; init; }
    public double BinSeconds { get; init; } = 0.1; // 10 Hz (100ms bins)
    public double AnalyzedDurationSeconds { get; init; }
    public float[] SpeechActivity { get; init; } = Array.Empty<float>(); // zero-mean normalized [-1..+1]
}

public sealed class SubtitleSyncResult
{
    public bool Succeeded { get; init; }
    public double OffsetSeconds { get; init; }
    public double SpeedScale { get; init; } = 1.0;
    public double Correlation { get; init; }
    public double ConfidenceZ { get; init; }
    public int CueCount { get; init; }
    public bool WasModified { get; init; }
    public string Summary { get; init; } = "";
}

public sealed class SubtitleCue
{
    public int Index { get; set; }
    public double StartSec { get; set; }
    public double EndSec { get; set; }
}

/// <summary>
/// Automatic Audio-Speech Subtitle Synchronizer (ffsubsync-inspired cross-correlation engine).
/// Uses bundled libmpv-2.dll to extract a 4kHz human-voice-band (300-1800 Hz) speech activity envelope,
/// supports external (.srt/.vtt/.ass/.ssa) AND embedded (MKV/MP4) subtitle tracks,
/// and cross-correlates cue intervals across [-90s..+90s] and common FPS ratios (23.976 <-> 25.0).
/// </summary>
public static class SubtitleAutoSyncService
{
    private const int SampleRate = 4000;
    private const double BinSeconds = 0.1; // 100ms resolution
    private const int SamplesPerBin = (int)(SampleRate * BinSeconds); // 400 Int16 samples
    private const double MaxAnalyzeSeconds = 900.0; // Up to first 15 minutes (~2.5-4.5s extraction)
    private const double MaxSearchOffsetSeconds = 90.0;

    private static readonly object CacheLock = new();
    private static VoiceFingerprint? _cachedFingerprint;

    private static readonly Regex SrtTimeRegex = new(
        @"(\d{1,2}):(\d{2}):(\d{2})[,\.](\d{1,3})\s*-->\s*(\d{1,2}):(\d{2}):(\d{2})[,\.](\d{1,3})",
        RegexOptions.Compiled);

    private static readonly Regex VttShortTimeRegex = new(
        @"(?<!\d:)(\d{1,2}):(\d{2})\.(\d{1,3})\s*-->\s*(\d{1,2}):(\d{2})\.(\d{1,3})",
        RegexOptions.Compiled);

    private static readonly Regex AssDialogueRegex = new(
        @"^Dialogue:\s*\d+\s*,\s*(\d{1,2}):(\d{2}):(\d{2})\.(\d{1,2})\s*,\s*(\d{1,2}):(\d{2}):(\d{2})\.(\d{1,2})\s*,",
        RegexOptions.Compiled | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    private static readonly HashSet<string> BitmapSubtitleCodecs = new(StringComparer.OrdinalIgnoreCase)
    {
        "hdmv_pgs_subtitle", "pgssub", "dvd_subtitle", "vobsub", "dvb_subtitle", "dvbsub", "xsub"
    };

    static SubtitleAutoSyncService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    public static string BuildTrackCacheKey(string videoPath, MediaTrack subTrack, int activeAudioTrackId)
    {
        long vidSize = 0;
        try { if (File.Exists(videoPath)) vidSize = new FileInfo(videoPath).Length; } catch { }

        if (subTrack.External && !string.IsNullOrWhiteSpace(subTrack.ExternalFilename))
        {
            long subSize = 0;
            long subTicks = 0;
            try
            {
                if (File.Exists(subTrack.ExternalFilename))
                {
                    var fi = new FileInfo(subTrack.ExternalFilename);
                    subSize = fi.Length;
                    subTicks = fi.LastWriteTimeUtc.Ticks;
                }
            }
            catch { }
            return $"{videoPath}|{vidSize}|aid:{activeAudioTrackId}|ext:{subTrack.ExternalFilename}|{subSize}|{subTicks}";
        }

        return $"{videoPath}|{vidSize}|aid:{activeAudioTrackId}|emb:{subTrack.Id}:{subTrack.FfIndex}:{subTrack.Codec}";
    }

    /// <summary>
    /// High-level check for the currently playing video's active subtitle track (external OR embedded).
    /// </summary>
    public static async Task<SubtitleSyncResult> AnalyzeActiveTrackAsync(
        string videoPath,
        double videoDurationSeconds,
        MediaTrack subTrack,
        int activeAudioTrackId,
        IProgress<string>? statusProgress,
        CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            throw new FileNotFoundException("Local video file is required for audio auto-sync.", videoPath);

        if (subTrack.External && !string.IsNullOrWhiteSpace(subTrack.ExternalFilename) && File.Exists(subTrack.ExternalFilename))
        {
            var fp = await GetOrExtractVoiceFingerprintAsync(videoPath, videoDurationSeconds, statusProgress, ct, activeAudioTrackId)
                .ConfigureAwait(false);
            ct.ThrowIfCancellationRequested();

            statusProgress?.Report("Aligning external subtitle to speech...");
            return await Task.Run(() =>
            {
                string text = ReadSubtitleTextSmart(subTrack.ExternalFilename);
                var cues = ParseAnySubtitleCues(text, Path.GetExtension(subTrack.ExternalFilename));
                return AnalyzeSubtitleCues(cues, fp);
            }, ct).ConfigureAwait(false);
        }

        // Embedded track inside MKV / MP4 / etc. — run voice analysis + embedded subtitle extraction in parallel!
        double analyzeEnd = videoDurationSeconds > 5
            ? Math.Min(videoDurationSeconds, MaxAnalyzeSeconds)
            : MaxAnalyzeSeconds;

        var fpTask = GetOrExtractVoiceFingerprintAsync(videoPath, videoDurationSeconds, statusProgress, ct, activeAudioTrackId);
        var cueTask = ExtractEmbeddedSubtitleCuesAsync(videoPath, subTrack, analyzeEnd, ct);

        await Task.WhenAll(fpTask, cueTask).ConfigureAwait(false);
        ct.ThrowIfCancellationRequested();

        var fingerprint = await fpTask.ConfigureAwait(false);
        var cues = await cueTask.ConfigureAwait(false);

        if (cues.Count < 4 && BitmapSubtitleCodecs.Contains(subTrack.Codec))
        {
            return new SubtitleSyncResult
            {
                Succeeded = false,
                CueCount = cues.Count,
                Summary = "Image subtitle (PGS/VobSub) cannot be auto-synced"
            };
        }

        statusProgress?.Report("Aligning embedded subtitle to speech...");
        return await Task.Run(() => AnalyzeSubtitleCues(cues, fingerprint), ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Extracts (or returns cached) 10Hz human-voice activity envelope directly from the video using libmpv-2.dll.
    /// </summary>
    public static Task<VoiceFingerprint> GetOrExtractVoiceFingerprintAsync(
        string videoPath,
        double videoDurationSeconds,
        IProgress<string>? statusProgress,
        CancellationToken ct,
        int audioTrackId = 0)
    {
        if (string.IsNullOrWhiteSpace(videoPath) || !File.Exists(videoPath))
            throw new FileNotFoundException("Local video file is required for audio auto-sync.", videoPath);

        long size = new FileInfo(videoPath).Length;
        lock (CacheLock)
        {
            if (_cachedFingerprint != null &&
                string.Equals(_cachedFingerprint.VideoPath, videoPath, StringComparison.OrdinalIgnoreCase) &&
                _cachedFingerprint.FileSize == size &&
                _cachedFingerprint.AudioTrackId == audioTrackId)
            {
                return Task.FromResult(_cachedFingerprint);
            }
        }

        return Task.Run(() =>
        {
            statusProgress?.Report("Analyzing video speech audio (300–1800 Hz)...");
            var fp = ExtractVoiceFingerprintBlocking(videoPath, size, videoDurationSeconds, audioTrackId, ct);
            lock (CacheLock)
            {
                _cachedFingerprint = fp;
            }
            return fp;
        }, ct);
    }

    private static VoiceFingerprint ExtractVoiceFingerprintBlocking(
        string videoPath,
        long fileSize,
        double videoDurationSeconds,
        int audioTrackId,
        CancellationToken ct)
    {
        double analyzeEnd = videoDurationSeconds > 5
            ? Math.Min(videoDurationSeconds, MaxAnalyzeSeconds)
            : MaxAnalyzeSeconds;

        string tempPcm = Path.Combine(Path.GetTempPath(), $"litevlc_vad_{Guid.NewGuid():N}.pcm");
        IntPtr handle = MpvNative.mpv_create();
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException("Failed to initialize libmpv audio analyzer.");

        try
        {
            MpvNative.mpv_set_option_string(handle, "vid", "no");
            MpvNative.mpv_set_option_string(handle, "vo", "null");
            if (audioTrackId > 0)
                MpvNative.mpv_set_option_string(handle, "aid", audioTrackId.ToString(CultureInfo.InvariantCulture));
            MpvNative.mpv_set_option_string(handle, "ao", "pcm");
            MpvNative.mpv_set_option_string(handle, "ao-pcm-file", tempPcm);
            MpvNative.mpv_set_option_string(handle, "ao-pcm-waveheader", "no");
            MpvNative.mpv_set_option_string(handle, "ao-pcm-append", "no");
            MpvNative.mpv_set_option_string(handle, "audio-channels", "mono");
            MpvNative.mpv_set_option_string(handle, "audio-samplerate", SampleRate.ToString(CultureInfo.InvariantCulture));
            MpvNative.mpv_set_option_string(handle, "audio-format", "s16");
            // Isolate human speech band (300-1800 Hz) and compress dynamics so quiet dialogue stands out
            MpvNative.mpv_set_option_string(handle, "af", "lavfi=[highpass=f=300,lowpass=f=1800,acompressor=threshold=-26dB:ratio=4]");
            MpvNative.mpv_set_option_string(handle, "untimed", "yes");
            MpvNative.mpv_set_option_string(handle, "end", analyzeEnd.ToString("0.###", CultureInfo.InvariantCulture));
            MpvNative.mpv_set_option_string(handle, "config", "no");
            MpvNative.mpv_set_option_string(handle, "load-scripts", "no");

            int initRc = MpvNative.mpv_initialize(handle);
            if (initRc < 0)
                throw new InvalidOperationException($"Audio analysis init failed: {MpvNative.GetErrorMessage(initRc)}");

            MpvNative.CommandUtf8(handle, false, "loadfile", videoPath, "replace");

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    MpvNative.CommandUtf8(handle, true, "quit");
                    ct.ThrowIfCancellationRequested();
                }

                IntPtr evPtr = MpvNative.mpv_wait_event(handle, 0.1);
                if (evPtr == IntPtr.Zero) continue;

                var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
                if (ev.EventId == MpvEventId.EndFile || ev.EventId == MpvEventId.Shutdown)
                    break;
            }
        }
        finally
        {
            MpvNative.mpv_terminate_destroy(handle);
        }

        try
        {
            if (!File.Exists(tempPcm))
                throw new InvalidOperationException("No audio track could be extracted from this video.");

            byte[] rawBytes = File.ReadAllBytes(tempPcm);
            int totalSamples = rawBytes.Length / 2;
            int binCount = totalSamples / SamplesPerBin;
            if (binCount < 30)
                throw new InvalidOperationException("Audio track is too short for automatic synchronization.");

            // Compute log-RMS energy per 100ms bin
            double[] logEnergy = new double[binCount];
            for (int b = 0; b < binCount; b++)
            {
                int byteStart = b * SamplesPerBin * 2;
                double sumSq = 0;
                for (int s = 0; s < SamplesPerBin; s++)
                {
                    short sample = (short)(rawBytes[byteStart + s * 2] | (rawBytes[byteStart + s * 2 + 1] << 8));
                    double norm = sample / 32768.0;
                    sumSq += norm * norm;
                }
                double rms = Math.Sqrt(sumSq / SamplesPerBin);
                logEnergy[b] = Math.Log10(rms + 1e-5);
            }

            // Adaptive local noise floor over +/- 15s window -> clean soft VAD [-1..+1]
            float[] activity = BuildAdaptiveSpeechActivity(logEnergy);

            return new VoiceFingerprint
            {
                VideoPath = videoPath,
                FileSize = fileSize,
                AudioTrackId = audioTrackId,
                BinSeconds = BinSeconds,
                AnalyzedDurationSeconds = binCount * BinSeconds,
                SpeechActivity = activity
            };
        }
        finally
        {
            try { if (File.Exists(tempPcm)) File.Delete(tempPcm); } catch { }
        }
    }

    private static Task<List<SubtitleCue>> ExtractEmbeddedSubtitleCuesAsync(
        string videoPath,
        MediaTrack subTrack,
        double maxAnalyzeSeconds,
        CancellationToken ct)
    {
        return Task.Run(() =>
        {
            // Fast Path: if system ffmpeg is available and this is a text subtitle with ff-index, extract in ~60-120ms without decoding video!
            if (subTrack.FfIndex >= 0 && !BitmapSubtitleCodecs.Contains(subTrack.Codec) && VideoTrimService.IsSystemFfmpegAvailable())
            {
                try
                {
                    var fastCues = ExtractEmbeddedWithFfmpeg(videoPath, subTrack.FfIndex, maxAnalyzeSeconds, ct);
                    if (fastCues.Count >= 4)
                        return fastCues;
                }
                catch (OperationCanceledException) { throw; }
                catch { /* fall back cleanly to bundled libmpv */ }
            }

            // Universal Fallback: extract embedded subtitle timestamps natively using bundled libmpv-2.dll (works on EVERY PC!)
            return ExtractEmbeddedWithLibMpv(videoPath, subTrack.Id, maxAnalyzeSeconds, ct);
        }, ct);
    }

    private static List<SubtitleCue> ExtractEmbeddedWithFfmpeg(
        string videoPath,
        int ffIndex,
        double maxAnalyzeSeconds,
        CancellationToken ct)
    {
        string dur = (maxAnalyzeSeconds + 100.0).ToString("0.###", CultureInfo.InvariantCulture);
        var psi = new ProcessStartInfo
        {
            FileName = "ffmpeg",
            Arguments = $"-v error -t {dur} -i \"{videoPath}\" -map 0:{ffIndex} -f srt -",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            CreateNoWindow = true
        };

        using var proc = Process.Start(psi);
        if (proc == null) return new List<SubtitleCue>();

        using var reg = ct.Register(() => { try { if (!proc.HasExited) proc.Kill(true); } catch { } });
        string output = proc.StandardOutput.ReadToEnd();
        proc.WaitForExit(15000);
        ct.ThrowIfCancellationRequested();

        return ParseAnySubtitleCues(output, ".srt");
    }

    private static List<SubtitleCue> ExtractEmbeddedWithLibMpv(
        string videoPath,
        int subTrackId,
        double maxAnalyzeSeconds,
        CancellationToken ct)
    {
        var cues = new List<SubtitleCue>();
        var seen = new HashSet<long>();

        IntPtr handle = MpvNative.mpv_create();
        if (handle == IntPtr.Zero) return cues;

        try
        {
            MpvNative.mpv_set_option_string(handle, "vo", "null");
            MpvNative.mpv_set_option_string(handle, "ao", "null");
            MpvNative.mpv_set_option_string(handle, "aid", "no");
            MpvNative.mpv_set_option_string(handle, "sid", Math.Max(1, subTrackId).ToString(CultureInfo.InvariantCulture));
            MpvNative.mpv_set_option_string(handle, "untimed", "yes");
            MpvNative.mpv_set_option_string(handle, "vd-lavc-fast", "yes");
            MpvNative.mpv_set_option_string(handle, "vd-lavc-skiploopfilter", "all");
            MpvNative.mpv_set_option_string(handle, "vd-lavc-skipframe", "nonref");
            MpvNative.mpv_set_option_string(handle, "end", (maxAnalyzeSeconds + 95.0).ToString("0.###", CultureInfo.InvariantCulture));
            MpvNative.mpv_set_option_string(handle, "config", "no");
            MpvNative.mpv_set_option_string(handle, "load-scripts", "no");

            if (MpvNative.mpv_initialize(handle) < 0)
                return cues;

            MpvNative.mpv_observe_property(handle, 10, "sub-start", MpvFormat.Double);
            MpvNative.CommandUtf8(handle, false, "loadfile", videoPath, "replace");

            while (true)
            {
                if (ct.IsCancellationRequested)
                {
                    MpvNative.CommandUtf8(handle, true, "quit");
                    ct.ThrowIfCancellationRequested();
                }

                IntPtr evPtr = MpvNative.mpv_wait_event(handle, 0.1);
                if (evPtr == IntPtr.Zero) continue;

                var ev = Marshal.PtrToStructure<MpvEvent>(evPtr);
                if (ev.EventId == MpvEventId.PropertyChange && ev.ReplyUserdata == 10)
                {
                    if (MpvNative.mpv_get_property(handle, "sub-start", MpvFormat.Double, out double s) >= 0 && s >= 0)
                    {
                        double e = s + 2.5;
                        if (MpvNative.mpv_get_property(handle, "sub-end", MpvFormat.Double, out double endProp) >= 0 && endProp > s)
                            e = endProp;

                        if (e - s < 45.0)
                        {
                            long key = ((long)Math.Round(s * 100) << 24) ^ (long)Math.Round(e * 100);
                            if (seen.Add(key))
                            {
                                cues.Add(new SubtitleCue
                                {
                                    Index = cues.Count + 1,
                                    StartSec = s,
                                    EndSec = e
                                });
                            }
                        }
                    }
                }
                else if (ev.EventId == MpvEventId.EndFile || ev.EventId == MpvEventId.Shutdown)
                {
                    break;
                }
            }
        }
        finally
        {
            MpvNative.mpv_terminate_destroy(handle);
        }

        cues.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return cues;
    }

    private static float[] BuildAdaptiveSpeechActivity(double[] logEnergy)
    {
        int n = logEnergy.Length;
        double[] sorted = (double[])logEnergy.Clone();
        Array.Sort(sorted);
        double globalFloor = sorted[(int)(n * 0.35)];
        double globalHigh = sorted[(int)(n * 0.85)];
        double globalSpan = Math.Max(0.25, globalHigh - globalFloor);

        double[] prefix = new double[n + 1];
        for (int i = 0; i < n; i++) prefix[i + 1] = prefix[i] + logEnergy[i];

        float[] rawVad = new float[n];
        const int halfWin = 150; // +/- 15s
        double mean = 0;

        for (int i = 0; i < n; i++)
        {
            int lo = Math.Max(0, i - halfWin);
            int hi = Math.Min(n, i + halfWin + 1);
            double localAvg = (prefix[hi] - prefix[lo]) / (hi - lo);
            double threshold = (localAvg * 0.55) + ((globalFloor + globalSpan * 0.38) * 0.45);
            double delta = (logEnergy[i] - threshold) / (globalSpan * 0.22);
            double prob = 1.0 / (1.0 + Math.Exp(-Math.Clamp(delta, -8, 8)));
            rawVad[i] = (float)prob;
            mean += prob;
        }

        mean /= n;
        float[] zeroMean = new float[n];
        for (int i = 0; i < n; i++)
        {
            float v = rawVad[i];
            if (i > 0 && i < n - 1)
                v = (rawVad[i - 1] + rawVad[i] * 2f + rawVad[i + 1]) * 0.25f;
            zeroMean[i] = v - (float)mean;
        }

        return zeroMean;
    }

    /// <summary>
    /// Reads a local subtitle file with BOM + strict UTF-8 + Arabic Windows-1256 auto-detection.
    /// </summary>
    public static string ReadSubtitleTextSmart(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch
        {
            // Legacy 8-bit encoding: distinguish Arabic CP1256 (letters 0xC1..0xED) vs Western CP1252
            int highCount = 0;
            int arabicRange = 0;
            foreach (byte b in bytes)
            {
                if (b >= 0x80)
                {
                    highCount++;
                    if (b is >= 0xC1 and <= 0xED)
                        arabicRange++;
                }
            }
            int cp = (highCount > 0 && (double)arabicRange / highCount >= 0.45) ? 1256 : 1252;
            return Encoding.GetEncoding(cp).GetString(bytes);
        }
    }

    /// <summary>
    /// Evaluates how well a subtitle file aligns to the video's speech fingerprint,
    /// finding the optimal offset (-90s..+90s) and FPS stretch ratio (1.0, 23.976/25, 25/23.976).
    /// </summary>
    public static SubtitleSyncResult AnalyzeSubtitleSync(string subtitlePath, VoiceFingerprint fingerprint)
    {
        string text = ReadSubtitleTextSmart(subtitlePath);
        return AnalyzeSubtitleText(text, fingerprint, Path.GetExtension(subtitlePath));
    }

    public static SubtitleSyncResult AnalyzeSubtitleText(string subtitleText, VoiceFingerprint fingerprint, string? extensionHint = null)
    {
        var cues = ParseAnySubtitleCues(subtitleText, extensionHint);
        return AnalyzeSubtitleCues(cues, fingerprint);
    }

    public static SubtitleSyncResult AnalyzeSubtitleCues(List<SubtitleCue> cues, VoiceFingerprint fingerprint)
    {
        if (cues.Count < 4)
        {
            return new SubtitleSyncResult
            {
                Succeeded = false,
                CueCount = cues.Count,
                Summary = "Too few subtitle cues to sync."
            };
        }

        float[] audio = fingerprint.SpeechActivity;
        int nAudio = audio.Length;
        double audioNorm = 0;
        for (int i = 0; i < nAudio; i++) audioNorm += audio[i] * audio[i];
        if (audioNorm < 1e-6)
        {
            return new SubtitleSyncResult
            {
                Succeeded = false,
                CueCount = cues.Count,
                Summary = "Video audio is silent."
            };
        }

        // Candidate speed scales:
        // 1.0 = exact FPS (constant offset only — most common!)
        // 23.976/25.0 = fixes a 25fps subtitle on a 23.976fps movie
        // 25.0/23.976 = fixes a 23.976fps subtitle on a 25fps movie
        double[] scales = [1.0, 23.976 / 25.0, 25.0 / 23.976];
        int maxLagBins = (int)(MaxSearchOffsetSeconds / BinSeconds);

        var scaleResults = new List<(double Scale, double OffsetSec, double Corr, double ZScore)>();

        foreach (double scale in scales)
        {
            float[] subVec = BuildSubtitleSpeechVector(cues, scale, nAudio);
            double subNorm = 0;
            for (int i = 0; i < subVec.Length; i++) subNorm += subVec[i] * subVec[i];
            if (subNorm < 1e-6) continue;

            double denom = Math.Sqrt(audioNorm * subNorm);

            // Stage 1: Coarse scan every 3 bins (0.3s) across [-90s..+90s]
            var coarseScores = new List<(int Lag, double Corr)>();
            double sum = 0, sumSq = 0;

            for (int lag = -maxLagBins; lag <= maxLagBins; lag += 3)
            {
                double r = ComputeCorrelation(audio, subVec, lag, denom);
                if (r <= -0.99) continue; // skip insufficient-overlap sentinels so Z-score stays sharp
                coarseScores.Add((lag, r));
                sum += r;
                sumSq += r * r;
            }

            if (coarseScores.Count == 0) continue;
            double mean = sum / coarseScores.Count;
            double variance = Math.Max(1e-9, (sumSq / coarseScores.Count) - (mean * mean));
            double std = Math.Sqrt(variance);

            // Stage 2: Fine 1-bin (0.1s) scan around the top 4 coarse peaks
            var topPeaks = coarseScores.OrderByDescending(x => x.Corr).Take(4).ToList();
            int bestLag = 0;
            double bestCorr = double.NegativeInfinity;

            foreach (var pk in topPeaks)
            {
                int lo = Math.Max(-maxLagBins, pk.Lag - 5);
                int hi = Math.Min(maxLagBins, pk.Lag + 5);
                for (int lag = lo; lag <= hi; lag++)
                {
                    double r = ComputeCorrelation(audio, subVec, lag, denom);
                    if (r > bestCorr)
                    {
                        bestCorr = r;
                        bestLag = lag;
                    }
                }
            }

            // Parabolic sub-bin refinement (~0.05s accuracy)
            double refinedLag = bestLag;
            if (bestLag > -maxLagBins && bestLag < maxLagBins)
            {
                double rL = ComputeCorrelation(audio, subVec, bestLag - 1, denom);
                double rC = bestCorr;
                double rR = ComputeCorrelation(audio, subVec, bestLag + 1, denom);
                double d = (rL - 2 * rC + rR);
                if (Math.Abs(d) > 1e-9)
                {
                    double shift = Math.Clamp(0.5 * (rL - rR) / d, -0.5, 0.5);
                    refinedLag = bestLag + shift;
                }
            }

            double zScore = (bestCorr - mean) / std;
            scaleResults.Add((scale, refinedLag * BinSeconds, bestCorr, zScore));
        }

        if (scaleResults.Count == 0)
            return new SubtitleSyncResult { Succeeded = false, CueCount = cues.Count, Summary = "Could not correlate subtitle." };

        // Prefer scale=1.0 unless an FPS stretch clearly outperforms it (prevents false stretching!)
        var unit = scaleResults.First(x => Math.Abs(x.Scale - 1.0) < 0.001);
        var chosen = unit;
        foreach (var cand in scaleResults)
        {
            if (Math.Abs(cand.Scale - 1.0) > 0.001 &&
                cand.Corr > unit.Corr * 1.18 &&
                cand.ZScore >= 4.5)
            {
                if (cand.Corr > chosen.Corr)
                    chosen = cand;
            }
        }

        bool confident = chosen.ZScore >= 3.2 && chosen.Corr >= 0.07;
        double offsetRounded = Math.Round(chosen.OffsetSec, 2);
        bool needsModify = confident && (Math.Abs(offsetRounded) >= 0.25 || Math.Abs(chosen.Scale - 1.0) > 0.001);

        string summary;
        if (!confident)
        {
            summary = $"Low speech match (Z={chosen.ZScore:F1}) — kept original timing";
        }
        else if (!needsModify)
        {
            summary = $"Already in sync ✓ (Z={chosen.ZScore:F1})";
        }
        else
        {
            string fpsNote = Math.Abs(chosen.Scale - 1.0) > 0.001
                ? (chosen.Scale < 1.0 ? " + FPS 25→23.976" : " + FPS 23.976→25")
                : "";
            summary = $"Auto-Synced {offsetRounded:+0.0;-0.0;0.0}s{fpsNote} ✓ (Z={chosen.ZScore:F1})";
        }

        return new SubtitleSyncResult
        {
            Succeeded = confident,
            OffsetSeconds = needsModify ? offsetRounded : 0,
            SpeedScale = needsModify ? chosen.Scale : 1.0,
            Correlation = chosen.Corr,
            ConfidenceZ = chosen.ZScore,
            CueCount = cues.Count,
            WasModified = needsModify,
            Summary = summary
        };
    }

    /// <summary>
    /// Permanently shifts/scales all timestamps in an SRT file and saves clean UTF-8 (with BOM).
    /// </summary>
    public static SubtitleSyncResult SyncAndRewriteSrtFile(string srtPath, VoiceFingerprint fingerprint)
    {
        string text = ReadSubtitleTextSmart(srtPath);
        var result = AnalyzeSubtitleText(text, fingerprint, ".srt");
        if (result.WasModified)
        {
            string syncedText = TransformSrtTimestamps(text, result.SpeedScale, result.OffsetSeconds);
            File.WriteAllText(srtPath, syncedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
        }
        return result;
    }

    public static void SaveSyncedSrtFile(string srtPath, double speedScale, double offsetSeconds)
    {
        string text = ReadSubtitleTextSmart(srtPath);
        string syncedText = TransformSrtTimestamps(text, speedScale, offsetSeconds);
        File.WriteAllText(srtPath, syncedText, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    public static string TransformSrtTimestamps(string srtText, double scale, double offsetSeconds)
    {
        return SrtTimeRegex.Replace(srtText, m =>
        {
            double s = ParseMatchTime(m, 1);
            double e = ParseMatchTime(m, 5);
            double ns = Math.Max(0, s * scale + offsetSeconds);
            double ne = Math.Max(ns + 0.1, e * scale + offsetSeconds);
            return $"{FormatSrtTime(ns)} --> {FormatSrtTime(ne)}";
        });
    }

    private static float[] BuildSubtitleSpeechVector(List<SubtitleCue> cues, double scale, int lengthBins)
    {
        float[] vec = new float[lengthBins];
        double activeCount = 0;

        foreach (var cue in cues)
        {
            double s = cue.StartSec * scale;
            double e = cue.EndSec * scale;
            if (e <= 0) continue;
            if (s >= lengthBins * BinSeconds) break;

            int b0 = Math.Clamp((int)Math.Floor(s / BinSeconds), 0, lengthBins - 1);
            int b1 = Math.Clamp((int)Math.Ceiling(e / BinSeconds), b0 + 1, lengthBins);
            for (int b = b0; b < b1; b++)
            {
                if (vec[b] == 0f)
                {
                    vec[b] = 1f;
                    activeCount++;
                }
            }
        }

        float mean = (float)(activeCount / lengthBins);
        for (int i = 0; i < lengthBins; i++)
            vec[i] -= mean;

        return vec;
    }

    /// <summary>
    /// Positive lag means subtitle needs to be shifted FORWARD (+lag bins) to match audio:
    /// audio[i] corresponds to sub[i - lag].
    /// </summary>
    private static double ComputeCorrelation(float[] audio, float[] sub, int lag, double denom)
    {
        int n = audio.Length;
        int startA = Math.Max(0, lag);
        int endA = Math.Min(n, n + lag);
        if (endA - startA < 50) return -1.0;

        double dot = 0;
        for (int i = startA; i < endA; i++)
            dot += audio[i] * sub[i - lag];

        return dot / denom;
    }

    public static List<SubtitleCue> ParseAnySubtitleCues(string text, string? extensionHint = null)
    {
        var list = new List<SubtitleCue>();
        if (string.IsNullOrWhiteSpace(text)) return list;

        bool preferAss = extensionHint != null &&
                         (extensionHint.Equals(".ass", StringComparison.OrdinalIgnoreCase) ||
                          extensionHint.Equals(".ssa", StringComparison.OrdinalIgnoreCase));

        if (!preferAss)
        {
            foreach (Match m in SrtTimeRegex.Matches(text))
            {
                double start = ParseMatchTime(m, 1);
                double end = ParseMatchTime(m, 5);
                if (end > start && end - start < 45.0)
                    list.Add(new SubtitleCue { Index = list.Count + 1, StartSec = start, EndSec = end });
            }

            if (list.Count < 4)
            {
                foreach (Match m in VttShortTimeRegex.Matches(text))
                {
                    double start = ParseShortVttTime(m, 1);
                    double end = ParseShortVttTime(m, 4);
                    if (end > start && end - start < 45.0)
                        list.Add(new SubtitleCue { Index = list.Count + 1, StartSec = start, EndSec = end });
                }
            }
        }

        if (list.Count < 4)
        {
            foreach (Match m in AssDialogueRegex.Matches(text))
            {
                double start = ParseAssTime(m, 1);
                double end = ParseAssTime(m, 5);
                if (end > start && end - start < 45.0)
                    list.Add(new SubtitleCue { Index = list.Count + 1, StartSec = start, EndSec = end });
            }
        }

        list.Sort((a, b) => a.StartSec.CompareTo(b.StartSec));
        return list;
    }

    private static double ParseMatchTime(Match m, int groupOffset)
    {
        int h = int.Parse(m.Groups[groupOffset].Value, CultureInfo.InvariantCulture);
        int min = int.Parse(m.Groups[groupOffset + 1].Value, CultureInfo.InvariantCulture);
        int sec = int.Parse(m.Groups[groupOffset + 2].Value, CultureInfo.InvariantCulture);
        string msStr = m.Groups[groupOffset + 3].Value.PadRight(3, '0');
        int ms = int.Parse(msStr[..3], CultureInfo.InvariantCulture);
        return h * 3600.0 + min * 60.0 + sec + ms / 1000.0;
    }

    private static double ParseShortVttTime(Match m, int groupOffset)
    {
        int min = int.Parse(m.Groups[groupOffset].Value, CultureInfo.InvariantCulture);
        int sec = int.Parse(m.Groups[groupOffset + 1].Value, CultureInfo.InvariantCulture);
        string msStr = m.Groups[groupOffset + 2].Value.PadRight(3, '0');
        int ms = int.Parse(msStr[..3], CultureInfo.InvariantCulture);
        return min * 60.0 + sec + ms / 1000.0;
    }

    private static double ParseAssTime(Match m, int groupOffset)
    {
        int h = int.Parse(m.Groups[groupOffset].Value, CultureInfo.InvariantCulture);
        int min = int.Parse(m.Groups[groupOffset + 1].Value, CultureInfo.InvariantCulture);
        int sec = int.Parse(m.Groups[groupOffset + 2].Value, CultureInfo.InvariantCulture);
        string csStr = m.Groups[groupOffset + 3].Value.PadRight(2, '0');
        int cs = int.Parse(csStr[..2], CultureInfo.InvariantCulture);
        return h * 3600.0 + min * 60.0 + sec + cs / 100.0;
    }

    private static string FormatSrtTime(double seconds)
    {
        var ts = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return $"{(int)ts.TotalHours:D2}:{ts.Minutes:D2}:{ts.Seconds:D2},{ts.Milliseconds:D3}";
    }
}
