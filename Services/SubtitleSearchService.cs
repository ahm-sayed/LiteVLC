using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LiteVLC.Models;

namespace LiteVLC.Services;

public sealed class ParsedMediaQuery
{
    public string Title { get; set; } = "";
    public int? Season { get; set; }
    public int? Episode { get; set; }
    public int? Year { get; set; }
    public bool IsSeries => Season.HasValue || Episode.HasValue;
    public HashSet<string> ReleaseTokens { get; } = new(StringComparer.OrdinalIgnoreCase);
    public string? MovieHash { get; set; }
    public long? FileSize { get; set; }
    public MediaProbeInfo? Probe { get; set; }
}

public sealed class SubtitleCandidate
{
    public string Id { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ReleaseName { get; init; } = "";
    public string LanguageCode { get; init; } = "";
    public string LanguageName { get; init; } = "";
    public string Format { get; init; } = "SRT";
    public string DownloadUrl { get; init; } = "";
    public string EncodingHint { get; init; } = "";
    public string Source { get; init; } = "";
    public double MovieFps { get; init; }
    public long MovieTimeMs { get; init; }
    public bool FpsMatched { get; set; }
    public bool FpsMismatched { get; set; }
    public bool QualityMatched { get; set; }
    public string MatchBadge { get; set; } = "MATCH";
    public string MatchBadgeBg { get; set; } = "#1A2236";
    public string MatchBadgeFg { get; set; } = "#94A3B8";
    public int Downloads { get; init; }
    public double Rating { get; init; }
    public bool IsExactHash { get; init; }
    public int Score { get; set; }

    public string DisplayTitle => !string.IsNullOrWhiteSpace(FileName) ? FileName : ReleaseName;
    public string MetaLine
    {
        get
        {
            var parts = new List<string> { LanguageName, Format.ToUpperInvariant() };
            if (MovieFps > 1)
            {
                string fpsTag = FpsMatched ? $"{MovieFps:0.###} fps ✓"
                              : FpsMismatched ? $"{MovieFps:0.###} fps ⚠"
                              : $"{MovieFps:0.###} fps";
                parts.Add(fpsTag);
            }
            if (QualityMatched) parts.Add("Quality ✓");
            if (Downloads > 0) parts.Add($"{Downloads:N0} downloads");
            if (Rating > 0) parts.Add($"★ {Rating:0.0}");
            if (!string.IsNullOrWhiteSpace(Source)) parts.Add(Source);
            return string.Join("  •  ", parts);
        }
    }
}

public sealed class SubtitleSearchService
{
    private static readonly HttpClient Http = CreateHttpClient();

    private static readonly string[] KnownReleaseTags =
    [
        "2160p", "4k", "1080p", "720p", "480p", "bluray", "bdrip", "brrip", "remux",
        "web-dl", "webdl", "webrip", "web", "hdrip", "hdtv", "dvdrip", "cam", "ts",
        "x264", "h264", "x265", "h265", "hevc", "av1", "10bit", "hdr", "dv",
        "yts", "yify", "rarbg", "psa", "ntb", "flux", "ion10", "evo", "sparks", "geckos",
        "nf", "netflix", "amzn", "amazon", "dsnp", "hmax", "atvp", "extended", "unrated", "directors", "proper", "repack"
    ];

    static SubtitleSearchService()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    private static HttpClient CreateHttpClient()
    {
        var handler = new HttpClientHandler
        {
            AutomaticDecompression = System.Net.DecompressionMethods.All
        };
        var client = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(12)
        };
        return client;
    }

    /// <summary>
    /// Computes the official 64-bit OpenSubtitles moviehash (file size + first 64KB + last 64KB).
    /// </summary>
    public static (string? Hash, long? Size) ComputeOpenSubtitlesHash(string filePath)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return (null, null);

            var fi = new FileInfo(filePath);
            long fileSize = fi.Length;
            if (fileSize < 131072) return (null, fileSize);

            ulong hash = (ulong)fileSize;
            const int chunkSize = 65536;
            byte[] buffer = new byte[chunkSize];

            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            int read = fs.Read(buffer, 0, chunkSize);
            for (int i = 0; i + 8 <= read; i += 8)
                hash += BitConverter.ToUInt64(buffer, i);

            fs.Seek(-chunkSize, SeekOrigin.End);
            read = fs.Read(buffer, 0, chunkSize);
            for (int i = 0; i + 8 <= read; i += 8)
                hash += BitConverter.ToUInt64(buffer, i);

            return (hash.ToString("x16", CultureInfo.InvariantCulture), fileSize);
        }
        catch
        {
            return (null, null);
        }
    }

    /// <summary>
    /// Intelligently extracts clean Title, Season, Episode, Year, Release Tags, File Hash,
    /// AND merges real stream properties (Resolution, FPS, Codec, Duration, hidden MKV/MP4 metadata).
    /// </summary>
    public static ParsedMediaQuery ParseMediaFile(string? mediaPath, string? fallbackTitle = null, MediaProbeInfo? probe = null)
    {
        var parsed = new ParsedMediaQuery { Probe = probe };
        string rawName = "";
        string parentName = "";
        string grandParentName = "";

        if (!string.IsNullOrWhiteSpace(mediaPath) && File.Exists(mediaPath))
        {
            rawName = Path.GetFileNameWithoutExtension(mediaPath);
            var dir = Directory.GetParent(mediaPath);
            if (dir != null)
            {
                parentName = dir.Name;
                if (dir.Parent != null) grandParentName = dir.Parent.Name;
            }
            var (hash, size) = ComputeOpenSubtitlesHash(mediaPath);
            parsed.MovieHash = hash;
            parsed.FileSize = size;
        }
        else
        {
            rawName = fallbackTitle ?? mediaPath ?? "";
        }

        // If the filename was renamed to something generic (e.g. "1.mp4", "video.mkv") but the container
        // carries a real embedded release title in its header, mine it too!
        string embeddedTitle = probe?.EmbeddedTitle ?? "";
        string embeddedEncoder = probe?.EmbeddedEncoder ?? "";
        string tagCorpus = $"{rawName} {embeddedTitle} {embeddedEncoder}".ToLowerInvariant();

        foreach (string tag in KnownReleaseTags)
        {
            if (Regex.IsMatch(tagCorpus, $@"(^|[^a-z0-9]){Regex.Escape(tag)}([^a-z0-9]|$)"))
                parsed.ReleaseTokens.Add(tag);
        }

        // Inject real probed resolution & codec from libmpv even if stripped from filename
        if (probe != null)
        {
            if (!string.IsNullOrEmpty(probe.QualityLabel))
            {
                parsed.ReleaseTokens.Add(probe.QualityLabel);
                if (probe.QualityLabel == "2160p") parsed.ReleaseTokens.Add("4k");
            }
            if (!string.IsNullOrEmpty(probe.CodecToken))
            {
                parsed.ReleaseTokens.Add(probe.CodecToken);
                if (probe.CodecToken == "x265") parsed.ReleaseTokens.Add("hevc");
            }
        }

        // If filename is purely generic ("1", "movie", "video", "film") and embeddedTitle looks meaningful, prefer embeddedTitle
        if ((rawName.Length <= 2 || Regex.IsMatch(rawName, @"(?i)^(movie|video|film|clip|ep|episode|\d+)$"))
            && embeddedTitle.Length >= 3)
        {
            rawName = embeddedTitle;
        }

        string normalized = Regex.Replace(rawName, @"[\._]+", " ");
        normalized = Regex.Replace(normalized, @"\[[^\]]*\]", " ");

        // Detect S01E02 / S1 E2 / 1x02 / Season 1 Episode 2
        var mSe = Regex.Match(normalized, @"(?i)\bS(?<s>\d{1,2})\s*\.?\s*E(?<e>\d{1,3})\b");
        if (!mSe.Success)
            mSe = Regex.Match(normalized, @"(?i)\b(?<s>\d{1,2})x(?<e>\d{1,3})\b");
        if (!mSe.Success)
            mSe = Regex.Match(normalized, @"(?i)\bSeason\s*(?<s>\d{1,2})\s*[-–]?\s*Episode\s*(?<e>\d{1,3})\b");

        string titleSource = normalized;
        if (mSe.Success)
        {
            parsed.Season = int.Parse(mSe.Groups["s"].Value, CultureInfo.InvariantCulture);
            parsed.Episode = int.Parse(mSe.Groups["e"].Value, CultureInfo.InvariantCulture);
            titleSource = normalized[..mSe.Index];
        }
        else
        {
            // Check standalone Episode / Ep 05
            var mEp = Regex.Match(normalized, @"(?i)\b(?:Episode|Ep|E)\s*(?<e>\d{1,3})\b");
            if (mEp.Success)
            {
                parsed.Episode = int.Parse(mEp.Groups["e"].Value, CultureInfo.InvariantCulture);
                titleSource = normalized[..mEp.Index];
                // Try finding season from parent folder ("Season 2" / "S02")
                var mParentS = Regex.Match(parentName, @"(?i)(?:Season\s*|S)(?<s>\d{1,2})\b");
                parsed.Season = mParentS.Success ? int.Parse(mParentS.Groups["s"].Value, CultureInfo.InvariantCulture) : 1;
            }
        }

        // Extract Year (1930..2035)
        var mYear = Regex.Match(titleSource, @"\b(19[3-9]\d|20[0-3]\d)\b");
        if (mYear.Success)
        {
            parsed.Year = int.Parse(mYear.Groups[1].Value, CultureInfo.InvariantCulture);
            if (mYear.Index > 1)
                titleSource = titleSource[..mYear.Index];
        }

        // Cut off at first resolution/source tag
        var mCut = Regex.Match(titleSource, @"(?i)\b(2160p|1080p|720p|480p|4k|bluray|bdrip|brrip|web-?dl|webrip|hdrip|hdtv|dvdrip|x264|x265|hevc|10bit|remux)\b");
        if (mCut.Success && mCut.Index > 1)
            titleSource = titleSource[..mCut.Index];

        string cleanTitle = Regex.Replace(titleSource, @"[\(\)\-\–_]+$", " ").Trim();
        cleanTitle = Regex.Replace(cleanTitle, @"\s{2,}", " ").Trim();

        // If filename was generic like "S01E01.mkv" or "Episode 1.mkv", borrow clean show name from parent folder
        if (cleanTitle.Length < 2 && !string.IsNullOrWhiteSpace(parentName))
        {
            string folderCandidate = Regex.IsMatch(parentName, @"(?i)^(Season\s*\d+|S\d{1,2})$")
                ? grandParentName
                : parentName;
            folderCandidate = Regex.Replace(folderCandidate, @"[\._]+", " ");
            var fCut = Regex.Match(folderCandidate, @"(?i)\b(S\d{1,2}|Season\s*\d+|2160p|1080p|720p|bluray|web-?dl|webrip|19[3-9]\d|20[0-3]\d)\b");
            if (fCut.Success && fCut.Index > 1)
                folderCandidate = folderCandidate[..fCut.Index];
            cleanTitle = Regex.Replace(folderCandidate, @"\s{2,}", " ").Trim(' ', '-', '(', ')');
        }

        parsed.Title = cleanTitle;
        return parsed;
    }

    /// <summary>
    /// Searches across OpenSubtitles Hash, Cinemeta+IMDB (OpenSubtitles REST + v3), and Fulltext Query in parallel,
    /// then deduplicates and ranks by exact sync & release accuracy.
    /// </summary>
    public async Task<(List<SubtitleCandidate> Results, string? ResolvedImdbInfo)> SearchAsync(
        string queryTitle,
        int? season,
        int? episode,
        int? year,
        string langCode, // "ara", "eng", "ara,eng", "fre", "spa", "ger", "tur", "all"
        ParsedMediaQuery? localFileInfo,
        CancellationToken ct)
    {
        var allCandidates = new List<SubtitleCandidate>();
        string? resolvedImdbId = null;
        string? resolvedImdbInfo = null;

        bool isSeries = season.HasValue || episode.HasValue;
        string[] langs = langCode == "all"
            ? ["all"]
            : langCode.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        var tasks = new List<Task<List<SubtitleCandidate>>>();

        // Tier 1: Exact 64-bit OpenSubtitles File Hash (Highest accuracy — frame-exact sync!)
        if (localFileInfo is { MovieHash.Length: 16, FileSize: > 0 })
        {
            foreach (string lang in langs)
            {
                string l = lang;
                tasks.Add(SearchOpenSubtitlesRestHashAsync(localFileInfo.MovieHash, localFileInfo.FileSize.Value, l, ct));
            }
        }

        // Tier 2: Resolve exact IMDB ID via Cinemeta (handles movies AND TV series accurately!)
        if (!string.IsNullOrWhiteSpace(queryTitle))
        {
            var imdb = await ResolveImdbAsync(queryTitle, isSeries, year, ct).ConfigureAwait(false);
            if (imdb.ImdbId != null)
            {
                resolvedImdbId = imdb.ImdbId;
                resolvedImdbInfo = $"{imdb.Name}" + (string.IsNullOrEmpty(imdb.Year) ? "" : $" ({imdb.Year})") + $"  [{imdb.ImdbId}]";

                foreach (string lang in langs)
                {
                    string l = lang;
                    tasks.Add(SearchOpenSubtitlesRestByImdbAsync(resolvedImdbId, season, episode, l, ct));
                }
                tasks.Add(SearchOpenSubtitlesV3Async(resolvedImdbId, season, episode, langs, localFileInfo, ct));
            }

            // Tier 3: Smart Query Search on OpenSubtitles REST (catches indie / Arabic / YouTube / non-IMDB content)
            foreach (string lang in langs)
            {
                string l = lang;
                tasks.Add(SearchOpenSubtitlesRestQueryAsync(queryTitle, season, episode, l, ct));
            }
        }

        var completed = await Task.WhenAll(tasks).ConfigureAwait(false);
        foreach (var batch in completed)
            allCandidates.AddRange(batch);

        // Deduplicate & score by accuracy
        var deduped = new Dictionary<string, SubtitleCandidate>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in allCandidates)
        {
            if (string.IsNullOrWhiteSpace(item.DownloadUrl)) continue;
            ScoreCandidate(item, localFileInfo, season, episode);

            string key = !string.IsNullOrWhiteSpace(item.Id)
                ? $"{item.Id}_{item.LanguageCode}"
                : $"{item.FileName}_{item.LanguageCode}";

            if (!deduped.TryGetValue(key, out var existing) || item.Score > existing.Score)
                deduped[key] = item;
        }

        var ranked = deduped.Values
            .OrderByDescending(c => c.Score)
            .ThenByDescending(c => c.Downloads)
            .Take(60)
            .ToList();

        return (ranked, resolvedImdbInfo);
    }

    private static void ScoreCandidate(SubtitleCandidate c, ParsedMediaQuery? local, int? season, int? episode)
    {
        int score = 0;
        string combined = $"{c.FileName} {c.ReleaseName}".ToLowerInvariant();
        var probe = local?.Probe;

        if (c.IsExactHash)
            score += 1200;

        if (c.Source.Contains("IMDB", StringComparison.OrdinalIgnoreCase) || c.Source.Contains("v3", StringComparison.OrdinalIgnoreCase))
            score += 240;

        // --- 1. Exact FPS Synchronization Check (prevents gradual subtitle drift!) ---
        if (probe is { Fps: > 1.0 } && c.MovieFps > 1.0)
        {
            double diff = Math.Abs(probe.Fps - c.MovieFps);
            if (diff <= 0.15)
            {
                c.FpsMatched = true;
                score += 220;
            }
            else if (diff >= 0.8)
            {
                // e.g. 25.0 fps PAL subtitle on a 23.976 fps BluRay/WEB movie will drift out of sync!
                c.FpsMismatched = true;
                score -= 320;
            }
        }

        // --- 2. Video Duration / Cut Accuracy (Theatrical vs Extended / Director's Cut) ---
        if (probe is { DurationSeconds: > 60 } && c.MovieTimeMs > 60_000)
        {
            double subSec = c.MovieTimeMs / 1000.0;
            double deltaSec = Math.Abs(probe.DurationSeconds - subSec);
            if (deltaSec <= 20) score += 150;
            else if (deltaSec <= 90) score += 50;
            else if (deltaSec > 180) score -= 180;
        }

        // --- 3. Real Probed Resolution, Codec, and Source/Release Token Matching ---
        int tokenMatches = 0;
        if (probe != null)
        {
            if (!string.IsNullOrEmpty(probe.QualityLabel) &&
                (combined.Contains(probe.QualityLabel, StringComparison.OrdinalIgnoreCase) ||
                 (probe.QualityLabel == "2160p" && combined.Contains("4k", StringComparison.OrdinalIgnoreCase))))
            {
                c.QualityMatched = true;
                score += 75;
            }

            if (!string.IsNullOrEmpty(probe.CodecToken) &&
                (combined.Contains(probe.CodecToken, StringComparison.OrdinalIgnoreCase) ||
                 (probe.CodecToken == "x265" && combined.Contains("hevc", StringComparison.OrdinalIgnoreCase))))
            {
                score += 35;
            }

            // Gently prefer BluRay/WEB-DL over CAM/TS when playing crisp 1080p/4K 23.976fps video
            if ((probe.QualityLabel is "1080p" or "2160p") && probe.Fps is >= 23.5 and <= 24.2)
            {
                if (combined.Contains("bluray") || combined.Contains("web-dl") || combined.Contains("webrip") || combined.Contains("bdrip"))
                    score += 45;
                if (Regex.IsMatch(combined, @"\b(cam|hdcam|ts|telesync|hdts)\b"))
                    score -= 160;
            }
        }

        if (local != null)
        {
            foreach (string tok in local.ReleaseTokens)
            {
                if (combined.Contains(tok, StringComparison.OrdinalIgnoreCase))
                {
                    tokenMatches++;
                    score += 55;
                }
            }
        }

        // --- 4. Confirm Season & Episode numbers match when searching a series ---
        if (season.HasValue && episode.HasValue)
        {
            string s2 = $"s{season.Value:D2}e{episode.Value:D2}";
            string s1 = $"s{season.Value}e{episode.Value:D2}";
            string xFmt = $"{season.Value}x{episode.Value:D2}";
            if (combined.Contains(s2) || combined.Contains(s1) || combined.Contains(xFmt))
                score += 160;
        }

        // --- 5. Popularity & quality bonus ---
        if (c.Downloads > 0)
            score += (int)(Math.Log10(c.Downloads + 1) * 28);
        if (c.Rating >= 8.0)
            score += 25;
        if (c.Format.Equals("srt", StringComparison.OrdinalIgnoreCase))
            score += 15;

        c.Score = score;

        // Assign clear accuracy / sync badges
        if (c.IsExactHash)
        {
            c.MatchBadge = "★ EXACT SYNC";
            c.MatchBadgeBg = "#0E291C";
            c.MatchBadgeFg = "#4ADE80";
        }
        else if (c.FpsMismatched)
        {
            c.MatchBadge = "FPS MISMATCH";
            c.MatchBadgeBg = "#301A14";
            c.MatchBadgeFg = "#F87171";
        }
        else if (c.FpsMatched && (c.QualityMatched || tokenMatches >= 2))
        {
            c.MatchBadge = "SYNC MATCH";
            c.MatchBadgeBg = "#0D2926";
            c.MatchBadgeFg = "#2DD4BF";
        }
        else if (c.FpsMatched || tokenMatches >= 2)
        {
            c.MatchBadge = "HIGH MATCH";
            c.MatchBadgeBg = "#0F2430";
            c.MatchBadgeFg = "#38BDF8";
        }
        else if (c.Source.Contains("IMDB", StringComparison.OrdinalIgnoreCase) || c.Source.Contains("v3", StringComparison.OrdinalIgnoreCase))
        {
            c.MatchBadge = "IMDB MATCH";
            c.MatchBadgeBg = "#1C1B33";
            c.MatchBadgeFg = "#A78BFA";
        }
        else
        {
            c.MatchBadge = "TEXT MATCH";
            c.MatchBadgeBg = "#1A2236";
            c.MatchBadgeFg = "#94A3B8";
        }
    }

    private static async Task<(string? ImdbId, string Name, string Year)> ResolveImdbAsync(
        string title, bool isSeries, int? year, CancellationToken ct)
    {
        try
        {
            string primaryType = isSeries ? "series" : "movie";
            var res = await QueryCinemetaAsync(primaryType, title, year, ct).ConfigureAwait(false);
            if (res.ImdbId != null) return res;

            // Try the opposite catalog in case user searched a series without S/E
            string fallbackType = isSeries ? "movie" : "series";
            return await QueryCinemetaAsync(fallbackType, title, year, ct).ConfigureAwait(false);
        }
        catch
        {
            return (null, "", "");
        }
    }

    private static async Task<(string? ImdbId, string Name, string Year)> QueryCinemetaAsync(
        string type, string title, int? year, CancellationToken ct)
    {
        string url = $"https://v3-cinemeta.strem.io/catalog/{type}/top/search={Uri.EscapeDataString(title)}.json";
        using var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", "LiteVLC/1.2");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode) return (null, "", "");

        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("metas", out var metas) || metas.ValueKind != JsonValueKind.Array)
            return (null, "", "");

        (string? id, string name, string relYear) best = (null, "", "");
        int bestScore = -1;

        foreach (var m in metas.EnumerateArray().Take(8))
        {
            string id = m.TryGetProperty("imdb_id", out var idP) ? (idP.GetString() ?? "") : "";
            if (string.IsNullOrWhiteSpace(id) && m.TryGetProperty("id", out var id2))
                id = id2.GetString() ?? "";
            if (!id.StartsWith("tt", StringComparison.OrdinalIgnoreCase)) continue;

            string name = m.TryGetProperty("name", out var nP) ? (nP.GetString() ?? "") : "";
            string rel = m.TryGetProperty("releaseInfo", out var rP) ? (rP.GetString() ?? "") : "";

            int sc = 0;
            if (string.Equals(name, title, StringComparison.OrdinalIgnoreCase)) sc += 100;
            else if (name.Contains(title, StringComparison.OrdinalIgnoreCase)) sc += 50;
            if (year.HasValue && rel.Contains(year.Value.ToString(CultureInfo.InvariantCulture))) sc += 80;

            if (sc > bestScore)
            {
                bestScore = sc;
                best = (id, name, rel);
            }
        }

        return best;
    }

    private static async Task<List<SubtitleCandidate>> SearchOpenSubtitlesRestHashAsync(
        string movieHash, long fileSize, string lang, CancellationToken ct)
    {
        string langSeg = lang == "all" ? "" : $"/sublanguageid-{Uri.EscapeDataString(lang)}";
        string url = $"https://rest.opensubtitles.org/search/moviebytesize-{fileSize}/moviehash-{movieHash}{langSeg}";
        return await FetchOpenSubtitlesRestAsync(url, isHashMatch: true, sourceLabel: "OS Hash", ct).ConfigureAwait(false);
    }

    private static async Task<List<SubtitleCandidate>> SearchOpenSubtitlesRestByImdbAsync(
        string imdbId, int? season, int? episode, string lang, CancellationToken ct)
    {
        string numericImdb = imdbId.TrimStart('t', 'T');
        // OpenSubtitles REST requires alphabetically sorted URL segments!
        var segments = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["imdbid"] = numericImdb
        };
        if (episode.HasValue) segments["episode"] = episode.Value.ToString(CultureInfo.InvariantCulture);
        if (season.HasValue) segments["season"] = season.Value.ToString(CultureInfo.InvariantCulture);
        if (lang != "all") segments["sublanguageid"] = lang;

        string path = string.Join("/", segments.Select(kv => $"{kv.Key}-{Uri.EscapeDataString(kv.Value)}"));
        string url = $"https://rest.opensubtitles.org/search/{path}";
        return await FetchOpenSubtitlesRestAsync(url, isHashMatch: false, sourceLabel: "OS IMDB", ct).ConfigureAwait(false);
    }

    private static async Task<List<SubtitleCandidate>> SearchOpenSubtitlesRestQueryAsync(
        string query, int? season, int? episode, string lang, CancellationToken ct)
    {
        var segments = new SortedDictionary<string, string>(StringComparer.Ordinal)
        {
            ["query"] = query.ToLowerInvariant()
        };
        if (episode.HasValue) segments["episode"] = episode.Value.ToString(CultureInfo.InvariantCulture);
        if (season.HasValue) segments["season"] = season.Value.ToString(CultureInfo.InvariantCulture);
        if (lang != "all") segments["sublanguageid"] = lang;

        string path = string.Join("/", segments.Select(kv => $"{kv.Key}-{Uri.EscapeDataString(kv.Value)}"));
        string url = $"https://rest.opensubtitles.org/search/{path}";
        return await FetchOpenSubtitlesRestAsync(url, isHashMatch: false, sourceLabel: "OS Search", ct).ConfigureAwait(false);
    }

    private static async Task<List<SubtitleCandidate>> FetchOpenSubtitlesRestAsync(
        string url, bool isHashMatch, string sourceLabel, CancellationToken ct)
    {
        var list = new List<SubtitleCandidate>();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "VLSub 0.11.1");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Array) return list;

            foreach (var el in doc.RootElement.EnumerateArray().Take(40))
            {
                string dl = GetStr(el, "SubDownloadLink");
                if (string.IsNullOrWhiteSpace(dl)) continue;

                string matchedBy = GetStr(el, "MatchedBy");
                bool exactHash = isHashMatch || matchedBy.Equals("moviehash", StringComparison.OrdinalIgnoreCase);

                int.TryParse(GetStr(el, "SubDownloadsCnt"), NumberStyles.Any, CultureInfo.InvariantCulture, out int downloads);
                double.TryParse(GetStr(el, "SubRating"), NumberStyles.Any, CultureInfo.InvariantCulture, out double rating);
                double.TryParse(GetStr(el, "MovieFPS"), NumberStyles.Any, CultureInfo.InvariantCulture, out double movieFps);
                long.TryParse(GetStr(el, "MovieTimeMS"), NumberStyles.Any, CultureInfo.InvariantCulture, out long movieTimeMs);

                list.Add(new SubtitleCandidate
                {
                    Id = GetStr(el, "IDSubtitleFile"),
                    FileName = GetStr(el, "SubFileName"),
                    ReleaseName = GetStr(el, "MovieReleaseName"),
                    LanguageCode = GetStr(el, "SubLanguageID"),
                    LanguageName = GetStr(el, "LanguageName"),
                    Format = string.IsNullOrWhiteSpace(GetStr(el, "SubFormat")) ? "srt" : GetStr(el, "SubFormat"),
                    DownloadUrl = dl,
                    EncodingHint = GetStr(el, "SubEncoding"),
                    Source = sourceLabel,
                    MovieFps = movieFps,
                    MovieTimeMs = movieTimeMs,
                    Downloads = downloads,
                    Rating = rating,
                    IsExactHash = exactHash
                });
            }
        }
        catch { }
        return list;
    }

    private static async Task<List<SubtitleCandidate>> SearchOpenSubtitlesV3Async(
        string imdbId, int? season, int? episode, string[] langs, ParsedMediaQuery? local, CancellationToken ct)
    {
        var list = new List<SubtitleCandidate>();
        try
        {
            string mediaPath = (season.HasValue && episode.HasValue)
                ? $"series/{imdbId}:{season.Value}:{episode.Value}"
                : $"movie/{imdbId}";

            string extra = "";
            if (local is { MovieHash.Length: 16, FileSize: > 0 })
                extra = $"/videoHash={local.MovieHash}&videoSize={local.FileSize.Value}";

            string url = $"https://opensubtitles-v3.strem.io/subtitles/{mediaPath}{extra}.json";
            using var req = new HttpRequestMessage(HttpMethod.Get, url);
            req.Headers.TryAddWithoutValidation("User-Agent", "LiteVLC/1.2");
            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode) return list;

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("subtitles", out var subs) || subs.ValueKind != JsonValueKind.Array)
                return list;

            var langFilter = new HashSet<string>(langs, StringComparer.OrdinalIgnoreCase);
            bool acceptAll = langFilter.Contains("all");

            foreach (var el in subs.EnumerateArray())
            {
                string lang = GetStr(el, "lang");
                if (!acceptAll && !langFilter.Contains(lang)) continue;

                string dl = GetStr(el, "url");
                if (string.IsNullOrWhiteSpace(dl)) continue;

                string matchFlag = GetStr(el, "m");
                bool isHash = matchFlag.Equals("h", StringComparison.OrdinalIgnoreCase);

                list.Add(new SubtitleCandidate
                {
                    Id = GetStr(el, "id"),
                    FileName = GetStr(el, "subtitleFileName"),
                    ReleaseName = GetStr(el, "movieReleaseName"),
                    LanguageCode = lang,
                    LanguageName = LangCodeToName(lang),
                    Format = "srt",
                    DownloadUrl = dl,
                    EncodingHint = "UTF-8", // Stremio v3 endpoint pre-converts to UTF-8
                    Source = "OS v3",
                    IsExactHash = isHash
                });
            }
        }
        catch { }
        return list;
    }

    /// <summary>
    /// Downloads and decodes subtitle bytes to clean UTF-8 text in memory (auto-gunzip + Arabic CP1256 fix).
    /// </summary>
    public async Task<(string Text, string Ext)> DownloadTextAsync(SubtitleCandidate sub, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(HttpMethod.Get, sub.DownloadUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", "VLSub 0.11.1");
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        byte[] raw = await resp.Content.ReadAsByteArrayAsync(ct).ConfigureAwait(false);

        // Auto-gunzip if compressed (magic bytes 1F 8B)
        if (raw.Length >= 2 && raw[0] == 0x1F && raw[1] == 0x8B)
        {
            using var ms = new MemoryStream(raw);
            using var gz = new GZipStream(ms, CompressionMode.Decompress);
            using var outMs = new MemoryStream();
            await gz.CopyToAsync(outMs, ct).ConfigureAwait(false);
            raw = outMs.ToArray();
        }

        string text = DecodeSubtitleBytesToText(raw, sub.LanguageCode, sub.EncodingHint);
        string ext = string.IsNullOrWhiteSpace(sub.Format) ? "srt" : sub.Format.TrimStart('.').ToLowerInvariant();
        if (ext != "srt" && ext != "ass" && ext != "ssa" && ext != "vtt" && ext != "sub")
            ext = "srt";

        return (text, ext);
    }

    public async Task<string> SaveSubtitleTextAsync(string text, string ext, SubtitleCandidate sub, string? currentVideoPath, CancellationToken ct)
    {
        string targetPath = BuildOutputSubtitlePath(currentVideoPath, sub, ext);
        await File.WriteAllTextAsync(targetPath, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true), ct).ConfigureAwait(false);
        return targetPath;
    }

    /// <summary>
    /// Downloads subtitle, auto-decompresses gzip, fixes Arabic CP1256 / legacy encodings to clean UTF-8,
    /// and saves right next to the video file (or in %LocalAppData%\LiteVLC\Subtitles if read-only/stream).
    /// </summary>
    public async Task<string> DownloadAndSaveAsync(SubtitleCandidate sub, string? currentVideoPath, CancellationToken ct)
    {
        var (text, ext) = await DownloadTextAsync(sub, ct).ConfigureAwait(false);
        return await SaveSubtitleTextAsync(text, ext, sub, currentVideoPath, ct).ConfigureAwait(false);
    }

    private static string DecodeSubtitleBytesToText(byte[] bytes, string langCode, string encodingHint)
    {
        // UTF-8 BOM
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF)
            return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        // UTF-16 LE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE)
            return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        // UTF-16 BE BOM
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF)
            return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);

        // Try strict UTF-8 validation first (if it's already valid UTF-8, never corrupt it!)
        try
        {
            var strictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
            return strictUtf8.GetString(bytes);
        }
        catch
        {
            // Not UTF-8 -> decode using Arabic Windows-1256 or declared codepage!
        }

        if (langCode.Equals("ara", StringComparison.OrdinalIgnoreCase) ||
            encodingHint.Contains("1256", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.GetEncoding(1256).GetString(bytes);
        }

        if (langCode.Equals("tur", StringComparison.OrdinalIgnoreCase) ||
            encodingHint.Contains("1254", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.GetEncoding(1254).GetString(bytes);
        }

        if (langCode.Equals("rus", StringComparison.OrdinalIgnoreCase) ||
            encodingHint.Contains("1251", StringComparison.OrdinalIgnoreCase))
        {
            return Encoding.GetEncoding(1251).GetString(bytes);
        }

        return Encoding.GetEncoding(1252).GetString(bytes);
    }

    private static string BuildOutputSubtitlePath(string? videoPath, SubtitleCandidate sub, string ext)
    {
        string langTag = string.IsNullOrWhiteSpace(sub.LanguageCode) ? "sub" : sub.LanguageCode.ToLowerInvariant();

        if (!string.IsNullOrWhiteSpace(videoPath) && File.Exists(videoPath))
        {
            try
            {
                string? dir = Path.GetDirectoryName(videoPath);
                string baseName = Path.GetFileNameWithoutExtension(videoPath);
                if (!string.IsNullOrWhiteSpace(dir))
                {
                    string candidate = Path.Combine(dir, $"{baseName}.{langTag}.{ext}");
                    // Test writeability safely
                    using (File.Create(candidate)) { }
                    return candidate;
                }
            }
            catch
            {
                // Directory might be read-only — fall back cleanly to AppData
            }
        }

        string subDir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "LiteVLC", "Subtitles");
        Directory.CreateDirectory(subDir);

        string safeName = Regex.Replace(sub.DisplayTitle, @"[^\w\-\.]+", "_").Trim('_');
        if (string.IsNullOrWhiteSpace(safeName)) safeName = $"subtitle_{DateTime.Now:yyyyMMdd_HHmmss}";
        if (!safeName.EndsWith("." + ext, StringComparison.OrdinalIgnoreCase))
            safeName += "." + ext;

        return Path.Combine(subDir, safeName);
    }

    private static string GetStr(JsonElement el, string prop)
    {
        if (!el.TryGetProperty(prop, out var v)) return "";
        return v.ValueKind == JsonValueKind.String ? (v.GetString() ?? "") : v.ToString();
    }

    private static string LangCodeToName(string code) => code.ToLowerInvariant() switch
    {
        "ara" => "Arabic",
        "eng" => "English",
        "fre" or "fra" => "French",
        "spa" => "Spanish",
        "ger" or "deu" => "German",
        "tur" => "Turkish",
        "ita" => "Italian",
        "rus" => "Russian",
        "por" => "Portuguese",
        _ => code.ToUpperInvariant()
    };
}
