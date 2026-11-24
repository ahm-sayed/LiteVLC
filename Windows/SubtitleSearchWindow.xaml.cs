using System;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using LiteVLC.Models;
using LiteVLC.Services;

namespace LiteVLC.Windows;

public partial class SubtitleSearchWindow : Window
{
    private readonly SubtitleSearchService _service = new();
    private readonly string? _currentVideoPath;
    private readonly ParsedMediaQuery _parsed;
    private CancellationTokenSource? _searchCts;

    public string? DownloadedSubtitlePath { get; private set; }
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

        // Select preferred language in ComboBox
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

        if (!string.IsNullOrEmpty(_parsed.MovieHash))
            HashBadgeText.Text = $"Hash: {_parsed.MovieHash}";
        else
            HashBadgeText.Text = "Title/IMDB Mode";

        // Display real probed video fingerprint (Resolution, FPS, Codec, Duration, Source Hint)
        if (probe is { HasAnySignal: true })
        {
            string summary = probe.BuildSummaryLine();
            if (!string.IsNullOrWhiteSpace(summary))
            {
                VideoProfileText.Text = summary;
                VideoProfileStrip.Visibility = Visibility.Visible;
            }
        }

        Loaded += async (_, _) =>
        {
            if (!string.IsNullOrWhiteSpace(QueryBox.Text) || !string.IsNullOrEmpty(_parsed.MovieHash))
                await RunSearchAsync();
            else
                QueryBox.Focus();
        };
    }

    private string GetSelectedLangCode()
        => (LangCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "ara";

    private async Task RunSearchAsync()
    {
        _searchCts?.Cancel();
        _searchCts = new CancellationTokenSource();
        var ct = _searchCts.Token;

        string title = QueryBox.Text.Trim();
        int? season = int.TryParse(SeasonBox.Text.Trim(), out int s) && s > 0 ? s : null;
        int? episode = int.TryParse(EpisodeBox.Text.Trim(), out int ep) && ep > 0 ? ep : null;
        string lang = GetSelectedLangCode();
        SelectedLanguageCode = lang;

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrEmpty(_parsed.MovieHash))
        {
            StatusText.Text = "Enter a movie or series title to search.";
            return;
        }

        SearchBtn.IsEnabled = false;
        ApplyBtn.IsEnabled = false;
        ResultsList.ItemsSource = null;
        StatusText.Text = "Searching exact hash, IMDB, and OpenSubtitles...";

        try
        {
            // Always keep probed FPS/resolution/duration for accurate sync scoring;
            // only skip exact file hash if user typed a completely different title!
            bool titleChanged = !string.IsNullOrWhiteSpace(title)
                             && !string.IsNullOrWhiteSpace(_parsed.Title)
                             && !string.Equals(title, _parsed.Title, StringComparison.OrdinalIgnoreCase);

            ParsedMediaQuery? scoringContext = _parsed;
            if (titleChanged)
            {
                scoringContext = new ParsedMediaQuery { Probe = _parsed.Probe };
                foreach (var t in _parsed.ReleaseTokens) scoringContext.ReleaseTokens.Add(t);
            }

            var (results, imdbInfo) = await _service.SearchAsync(
                title,
                season,
                episode,
                _parsed.Year,
                lang,
                scoringContext,
                ct);

            if (ct.IsCancellationRequested) return;

            ResultsList.ItemsSource = results;
            if (results.Count > 0)
            {
                ResultsList.SelectedIndex = 0;
                ApplyBtn.IsEnabled = true;
                int exactCount = results.Count(r => r.IsExactHash);
                string prefix = !string.IsNullOrEmpty(imdbInfo) ? $"{imdbInfo}  •  " : "";
                string exactNote = exactCount > 0 ? $" ({exactCount} exact sync!)" : "";
                StatusText.Text = $"{prefix}{results.Count} subtitles found{exactNote}";
            }
            else
            {
                StatusText.Text = "No subtitles found. Try simplifying the title, checking Season/Episode, or selecting 'All Languages'.";
            }
        }
        catch (OperationCanceledException)
        {
            // Superseded by newer search
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Search error: {ex.Message}";
        }
        finally
        {
            SearchBtn.IsEnabled = true;
        }
    }

    private async Task DownloadSelectedAsync()
    {
        if (ResultsList.SelectedItem is not SubtitleCandidate selected) return;

        ApplyBtn.IsEnabled = false;
        SearchBtn.IsEnabled = false;
        StatusText.Text = $"Downloading {selected.DisplayTitle}...";

        try
        {
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            string savedPath = await _service.DownloadAndSaveAsync(selected, _currentVideoPath, cts.Token);
            DownloadedSubtitlePath = savedPath;
            SelectedLanguageCode = GetSelectedLangCode();
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            StatusText.Text = $"Download failed: {ex.Message} — try another result.";
            ApplyBtn.IsEnabled = true;
            SearchBtn.IsEnabled = true;
        }
    }

    private async void Search_Click(object sender, RoutedEventArgs e) => await RunSearchAsync();
    private async void Apply_Click(object sender, RoutedEventArgs e) => await DownloadSelectedAsync();

    private async void ResultsList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ResultsList.SelectedItem is SubtitleCandidate)
            await DownloadSelectedAsync();
    }

    private void ResultsList_SelectionChanged(object sender, SelectionChangedEventArgs e)
        => ApplyBtn.IsEnabled = ResultsList.SelectedItem is SubtitleCandidate;

    private async void Input_KeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Enter)
        {
            e.Handled = true;
            await RunSearchAsync();
        }
    }

    private void Window_PreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            _searchCts?.Cancel();
            DialogResult = false;
            Close();
            e.Handled = true;
        }
    }

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.LeftButton == MouseButtonState.Pressed) DragMove();
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _searchCts?.Cancel();
        DialogResult = false;
        Close();
    }
}
