using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using LiteVLC.Models;
using LiteVLC.Services;
using Microsoft.Win32;

namespace LiteVLC.Windows;

public partial class SubtitleSearchWindow : Window
{
    private readonly SubtitleSearchService _service = new();
    private readonly string? _currentVideoPath;
    private readonly ParsedMediaQuery _parsed;
    private CancellationTokenSource? _cts;
    private List<SubtitleCandidate> _lastResults = new();
    private bool _isBusy;

    public string? DownloadedSubtitlePath { get; private set; }
    public string? SyncSummaryNote { get; private set; }
    public string SelectedLanguageCode { get; private set; } = "ara";

    public SubtitleSearchWindow(string? currentVideoPath, string? fallbackTitle, string preferredLang = "ara", MediaProbeInfo? probe = null)
    {
        _currentVideoPath = currentVideoPath;
        _parsed = SubtitleSearchService.ParseMediaFile(currentVideoPath, fallbackTitle, probe);
        SelectedLanguageCode = string.IsNullOrWhiteSpace(preferredLang) ? "ara" : preferredLang;

        InitializeComponent();

        QueryBox.Text = _parsed.Title;
        if (_parsed.Season.HasValue) SeasonBox.Text = _parsed.Season.Value.ToString(CultureInfo.InvariantCulture);
        if (_parsed.Episode.HasValue) EpisodeBox.Text = _parsed.Episode.Value.ToString(CultureInfo.InvariantCulture);

        foreach (ComboBoxItem item in LangCombo.Items)
        {
            if (string.Equals(item.Tag?.ToString(), SelectedLanguageCode, StringComparison.OrdinalIgnoreCase))
            {
                LangCombo.SelectedItem = item;
                break;
            }
        }
        if (LangCombo.SelectedItem == null)
            LangCombo.SelectedIndex = 0;

        HashBadgeText.Text = !string.IsNullOrEmpty(_parsed.MovieHash)
            ? $"Hash: {_parsed.MovieHash}"
            : "Title/IMDB Mode";

        if (probe is { HasAnySignal: true })
        {
            string summary = probe.BuildSummaryLine();
            if (!string.IsNullOrWhiteSpace(summary))
            {
                VideoProfileText.Text = summary;
                VideoProfileStrip.Visibility = Visibility.Visible;
            }
        }

        if (string.IsNullOrWhiteSpace(_currentVideoPath) || !File.Exists(_currentVideoPath))
            SyncLocalBtn.IsEnabled = false;
    }

    private string GetSelectedLangCode()
        => (LangCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ara";

    private void SetBusy(bool busy, string? status = null, Color? color = null)
    {
        _isBusy = busy;
        AutoFindSyncBtn.IsEnabled = !busy;
        ToggleManualBtn.IsEnabled = !busy;
        SyncLocalBtn.IsEnabled = !busy && !string.IsNullOrWhiteSpace(_currentVideoPath) && File.Exists(_currentVideoPath);
        ApplyBtn.IsEnabled = !busy && ResultsList.SelectedItem is SubtitleCandidate;
        if (status != null)
        {
            StatusText.Text = status;
            StatusText.Foreground = new SolidColorBrush(color ?? Color.FromRgb(0xCB, 0xD5, 0xE1));
        }
    }

    private async Task<(List<SubtitleCandidate> Results, string? ImdbInfo)> FetchCandidatesAsync(CancellationToken ct)
    {
        string title = QueryBox.Text.Trim();
        int? season = int.TryParse(SeasonBox.Text.Trim(), out int s) && s > 0 ? s : null;
        int? episode = int.TryParse(EpisodeBox.Text.Trim(), out int ep) && ep > 0 ? ep : null;
        string lang = GetSelectedLangCode();
        SelectedLanguageCode = lang;

        bool titleChanged = !string.IsNullOrWhiteSpace(title)
                         && !string.IsNullOrWhiteSpace(_parsed.Title)
                         && !string.Equals(title, _parsed.Title, StringComparison.OrdinalIgnoreCase);

        ParsedMediaQuery? scoringContext = _parsed;
        if (titleChanged)
        {
            scoringContext = new ParsedMediaQuery { Probe = _parsed.Probe };
            foreach (var t in _parsed.ReleaseTokens) scoringContext.ReleaseTokens.Add(t);
        }

        var res = await _service.SearchAsync(title, season, episode, _parsed.Year, lang, scoringContext, ct);
        _lastResults = res.Results;
        ResultsList.ItemsSource = _lastResults;
        if (_lastResults.Count > 0) ResultsList.SelectedIndex = 0;
        return res;
    }

    /// <summary>
    /// 1-Click Automatic Flow:
    /// Searches -> Downloads top candidates -> Correlates against REAL video speech audio ->
    /// Picks the single highest-confidence synced subtitle -> Rewrites timestamps -> Applies immediately!
    /// </summary>
    private async void AutoFindSync_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SetBusy(true, "Step 1/3: Searching best matching subtitles...");

        try
        {
            var (results, imdbInfo) = await FetchCandidatesAsync(ct);
            if (ct.IsCancellationRequested) return;

            if (results.Count == 0)
            {
                SetBusy(false, "No subtitles found. Try simplifying the title or changing language.", Color.FromRgb(0xFB, 0xBF, 0x24));
                return;
            }

            bool hasLocalVideo = !string.IsNullOrWhiteSpace(_currentVideoPath) && File.Exists(_currentVideoPath);
            // Filter out obvious FPS mismatches if we have better options; prefer SRT for exact timestamp rewriting
            var topCandidates = results
                .OrderByDescending(r => r.IsExactHash)
                .ThenBy(r => r.FpsMismatched)
                .ThenByDescending(r => r.Format.Equals("srt", StringComparison.OrdinalIgnoreCase))
                .ThenByDescending(r => r.Score)
                .Take(hasLocalVideo ? 3 : 1)
                .ToList();

            // Fast path: if we have an exact file-hash match OR no local file (stream), apply directly
            if (!hasLocalVideo)
            {
                var best = topCandidates[0];
                SetBusy(true, $"Downloading {best.DisplayTitle}...");
                string saved = await _service.DownloadAndSaveAsync(best, _currentVideoPath, ct);
                DownloadedSubtitlePath = saved;
                SyncSummaryNote = best.MatchBadge;
                DialogResult = true;
                Close();
                return;
            }

            // Step 2: Download top 3 candidates in memory AND extract video's speech fingerprint in parallel!
            SetBusy(true, "Step 2/3: Analyzing video speech & downloading top matches...");

            var statusProgress = new Progress<string>(msg => StatusText.Text = $"Step 2/3: {msg}");
            double vidDur = _parsed.Probe?.DurationSeconds ?? 0;

            var vadTask = SubtitleAutoSyncService.GetOrExtractVoiceFingerprintAsync(
                _currentVideoPath!, vidDur, statusProgress, ct);

            var downloadTasks = topCandidates.Select(async cand =>
            {
                try
                {
                    var (text, ext) = await _service.DownloadTextAsync(cand, ct);
                    return (Candidate: cand, Text: text, Ext: ext, Ok: true);
                }
                catch
                {
                    return (Candidate: cand, Text: "", Ext: "srt", Ok: false);
                }
            }).ToList();

            await Task.WhenAll(vadTask, Task.WhenAll(downloadTasks));
            if (ct.IsCancellationRequested) return;

            var fingerprint = await vadTask;
            var downloaded = downloadTasks.Select(t => t.Result).Where(x => x.Ok && !string.IsNullOrWhiteSpace(x.Text)).ToList();
            if (downloaded.Count == 0)
            {
                SetBusy(false, "Could not download candidates. Try 'Browse All Results'.", Color.FromRgb(0xF8, 0x71, 0x71));
                return;
            }

            // Step 3: Cross-correlate every downloaded candidate against REAL video speech (<50ms total!)
            SetBusy(true, "Step 3/3: Aligning subtitle timing to video speech...");

            var evaluated = await Task.Run(() =>
            {
                return downloaded.Select(d =>
                {
                    var sync = d.Ext.Equals("srt", StringComparison.OrdinalIgnoreCase)
                        ? SubtitleAutoSyncService.AnalyzeSubtitleText(d.Text, fingerprint)
                        : new SubtitleSyncResult { Succeeded = false, Summary = "Non-SRT (direct)" };
                    // Combine speech-sync Z-score with exact-hash bonus
                    double totalRank = sync.ConfidenceZ + (d.Candidate.IsExactHash ? 10.0 : 0.0);
                    return (d.Candidate, d.Text, d.Ext, Sync: sync, Rank: totalRank);
                }).OrderByDescending(x => x.Rank).ToList();
            }, ct);

            var winner = evaluated[0];
            string finalText = winner.Text;

            // Only rewrite if audio correlation found a real offset/FPS shift (and skip tiny changes on exact hash)
            if (winner.Sync.WasModified && (!winner.Candidate.IsExactHash || Math.Abs(winner.Sync.OffsetSeconds) >= 0.6))
            {
                finalText = SubtitleAutoSyncService.TransformSrtTimestamps(
                    winner.Text, winner.Sync.SpeedScale, winner.Sync.OffsetSeconds);
            }

            string savedPath = await _service.SaveSubtitleTextAsync(finalText, winner.Ext, winner.Candidate, _currentVideoPath, ct);
            DownloadedSubtitlePath = savedPath;
            SyncSummaryNote = winner.Candidate.IsExactHash && !winner.Sync.WasModified
                ? "Exact Hash Sync ✓"
                : winner.Sync.Summary;

            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Auto-sync error: {ex.Message}", Color.FromRgb(0xF8, 0x71, 0x71));
        }
    }

    /// <summary>
    /// Lets the user select any existing local .srt file and auto-syncs it permanently to the playing video's audio!
    /// </summary>
    private async void SyncLocalSrt_Click(object sender, RoutedEventArgs e)
    {
        if (_isBusy || string.IsNullOrWhiteSpace(_currentVideoPath) || !File.Exists(_currentVideoPath)) return;

        var dlg = new OpenFileDialog
        {
            Title = "Select Subtitle (.srt) to Auto-Sync to Video Audio",
            InitialDirectory = Path.GetDirectoryName(_currentVideoPath),
            Filter = "SRT Subtitles (*.srt)|*.srt|All Files|*.*"
        };
        if (dlg.ShowDialog(this) != true) return;

        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SetBusy(true, "Analyzing video speech audio to sync your subtitle...");
        try
        {
            var progress = new Progress<string>(msg => StatusText.Text = msg);
            var fp = await SubtitleAutoSyncService.GetOrExtractVoiceFingerprintAsync(
                _currentVideoPath, _parsed.Probe?.DurationSeconds ?? 0, progress, ct);

            var syncRes = await Task.Run(() => SubtitleAutoSyncService.SyncAndRewriteSrtFile(dlg.FileName, fp), ct);

            DownloadedSubtitlePath = dlg.FileName;
            SyncSummaryNote = syncRes.Summary;
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Sync failed: {ex.Message}", Color.FromRgb(0xF8, 0x71, 0x71));
        }
    }

    private async void ToggleManual_Click(object sender, RoutedEventArgs e)
    {
        bool show = ManualResultsContainer.Visibility != Visibility.Visible;
        ManualResultsContainer.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ApplyBtn.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        AutoSyncManualCheck.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        ToggleManualBtn.Content = show ? "Hide Results" : "Browse All Results";

        if (show && _lastResults.Count == 0 && !_isBusy)
        {
            _cts?.Cancel();
            _cts = new CancellationTokenSource();
            SetBusy(true, "Searching all matching subtitles...");
            try
            {
                var (results, imdbInfo) = await FetchCandidatesAsync(_cts.Token);
                string prefix = !string.IsNullOrEmpty(imdbInfo) ? $"{imdbInfo}  •  " : "";
                SetBusy(false, results.Count > 0 ? $"{prefix}{results.Count} subtitles found" : "No subtitles found.");
            }
            catch (Exception ex)
            {
                SetBusy(false, $"Search error: {ex.Message}", Color.FromRgb(0xF8, 0x71, 0x71));
            }
        }
    }

    private async Task DownloadSelectedManualAsync()
    {
        if (_isBusy || ResultsList.SelectedItem is not SubtitleCandidate selected) return;
        _cts?.Cancel();
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;

        SetBusy(true, $"Downloading {selected.DisplayTitle}...");
        try
        {
            var (text, ext) = await _service.DownloadTextAsync(selected, ct);
            string syncNote = selected.MatchBadge;

            bool shouldSync = AutoSyncManualCheck.IsChecked == true
                           && ext.Equals("srt", StringComparison.OrdinalIgnoreCase)
                           && !string.IsNullOrWhiteSpace(_currentVideoPath)
                           && File.Exists(_currentVideoPath);

            if (shouldSync)
            {
                SetBusy(true, "Auto-syncing subtitle to video speech audio...");
                var progress = new Progress<string>(msg => StatusText.Text = msg);
                var fp = await SubtitleAutoSyncService.GetOrExtractVoiceFingerprintAsync(
                    _currentVideoPath!, _parsed.Probe?.DurationSeconds ?? 0, progress, ct);
                var sync = await Task.Run(() => SubtitleAutoSyncService.AnalyzeSubtitleText(text, fp), ct);
                if (sync.WasModified)
                    text = SubtitleAutoSyncService.TransformSrtTimestamps(text, sync.SpeedScale, sync.OffsetSeconds);
                syncNote = sync.Summary;
            }

            string savedPath = await _service.SaveSubtitleTextAsync(text, ext, selected, _currentVideoPath, ct);
            DownloadedSubtitlePath = savedPath;
            SyncSummaryNote = syncNote;
            SelectedLanguageCode = GetSelectedLangCode();
            DialogResult = true;
            Close();
        }
        catch (OperationCanceledException)
        {
            SetBusy(false, "Cancelled.");
        }
        catch (Exception ex)
        {
            SetBusy(false, $"Download failed: {ex.Message}", Color.FromRgb(0xF8, 0x71, 0x71));
        }
    }

    private async void Apply_Click(object sender, RoutedEventArgs e) => await DownloadSelectedManualAsync();

    private async void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is SubtitleCandidate)
            await DownloadSelectedManualAsync();
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplyBtn.IsEnabled = !_isBusy && ResultsList.SelectedItem is SubtitleCandidate;

    private void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            AutoFindSync_Click(sender, e);
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            Close_Click(sender, e);
            e.Handled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        DialogResult = false;
        Close();
    }
}
