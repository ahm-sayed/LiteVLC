using System;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LiteVLC.Services;

public sealed class UpdateCheckResult
{
    public bool UpdateAvailable { get; init; }
    public Version CurrentVersion { get; init; } = new(1, 0, 0);
    public Version LatestVersion { get; init; } = new(1, 0, 0);
    public string TagName { get; init; } = "";
    public string ReleaseNotes { get; init; } = "";
    public string InstallerUrl { get; init; } = "";
    public string InstallerFileName { get; init; } = "";
    public long InstallerSizeBytes { get; init; }
    public string ReleasePageUrl { get; init; } = "";
    public string? ErrorMessage { get; init; }
}

/// <summary>
/// Checks GitHub Releases for ahm-sayed/LiteVLC, downloads the newest Inno Setup installer with progress,
/// and applies the update silently with automatic app relaunch.
/// </summary>
public sealed class AutoUpdaterService
{
    private const string RepoOwner = "ahm-sayed";
    private const string RepoName = "LiteVLC";
    private static readonly string LatestApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public static Version GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v == null) return new Version(1, 3, 0);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        Version current = GetCurrentVersion();
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Get, LatestApiUrl);
            req.Headers.TryAddWithoutValidation("User-Agent", $"LiteVLC/{current}");
            req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");

            using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
            if (!resp.IsSuccessStatusCode)
            {
                return new UpdateCheckResult
                {
                    CurrentVersion = current,
                    LatestVersion = current,
                    ErrorMessage = $"GitHub returned HTTP {(int)resp.StatusCode}"
                };
            }

            string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            string tag = root.TryGetProperty("tag_name", out var tP) ? (tP.GetString() ?? "") : "";
            string notes = root.TryGetProperty("body", out var bP) ? (bP.GetString() ?? "") : "";
            string htmlUrl = root.TryGetProperty("html_url", out var hP) ? (hP.GetString() ?? "") : "";

            Version latest = ParseTagVersion(tag) ?? current;
            string installerUrl = "";
            string installerName = "";
            long installerSize = 0;

            if (root.TryGetProperty("assets", out var assets) && assets.ValueKind == JsonValueKind.Array)
            {
                foreach (var a in assets.EnumerateArray())
                {
                    string name = a.TryGetProperty("name", out var nP) ? (nP.GetString() ?? "") : "";
                    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
                    {
                        installerName = name;
                        installerUrl = a.TryGetProperty("browser_download_url", out var uP) ? (uP.GetString() ?? "") : "";
                        installerSize = a.TryGetProperty("size", out var sP) ? sP.GetInt64() : 0;
                        break;
                    }
                }
            }

            return new UpdateCheckResult
            {
                UpdateAvailable = latest > current && !string.IsNullOrWhiteSpace(installerUrl),
                CurrentVersion = current,
                LatestVersion = latest,
                TagName = tag,
                ReleaseNotes = notes,
                InstallerUrl = installerUrl,
                InstallerFileName = installerName,
                InstallerSizeBytes = installerSize,
                ReleasePageUrl = htmlUrl
            };
        }
        catch (Exception ex)
        {
            return new UpdateCheckResult
            {
                CurrentVersion = current,
                LatestVersion = current,
                ErrorMessage = ex.Message
            };
        }
    }

    public async Task<string> DownloadInstallerAsync(
        UpdateCheckResult update,
        IProgress<(long Downloaded, long Total)> progress,
        CancellationToken ct)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LiteVLC_Update");
        Directory.CreateDirectory(tempDir);

        string fileName = string.IsNullOrWhiteSpace(update.InstallerFileName)
            ? $"LiteVLC-Setup-{update.LatestVersion}.exe"
            : update.InstallerFileName;
        string destPath = Path.Combine(tempDir, fileName);

        using var req = new HttpRequestMessage(HttpMethod.Get, update.InstallerUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", $"LiteVLC/{update.CurrentVersion}");
        using var resp = await Http.SendAsync(req, HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
        resp.EnsureSuccessStatusCode();

        long total = resp.Content.Headers.ContentLength ?? update.InstallerSizeBytes;
        await using var src = await resp.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920, useAsync: true);

        byte[] buffer = new byte[81920];
        long downloaded = 0;
        long lastReport = 0;
        int read;
        while ((read = await src.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
        {
            await dst.WriteAsync(buffer.AsMemory(0, read), ct).ConfigureAwait(false);
            downloaded += read;
            long now = Environment.TickCount64;
            if (now - lastReport >= 80 || (total > 0 && downloaded >= total))
            {
                lastReport = now;
                progress.Report((downloaded, total));
            }
        }

        return destPath;
    }

    /// <summary>
    /// Runs the downloaded Inno Setup installer silently (/VERYSILENT), closes the running app cleanly,
    /// installs the new version automatically with zero user clicks, and relaunches LiteVLC immediately!
    /// </summary>
    public static void LaunchSilentInstallAndRelaunch(string installerPath)
    {
        string currentExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LiteVLC.exe");
        string currentDir = Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory;

        // If running from an installed folder (not a dev bin\Release folder), overwrite in place automatically
        bool isDevBuild = currentDir.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase)
                       || currentDir.Contains(@"\bin\Debug", StringComparison.OrdinalIgnoreCase);

        string targetExe = isDevBuild
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LiteVLC", "LiteVLC.exe")
            : currentExe;
        string dirArg = isDevBuild ? "" : $" /DIR=\"{currentDir}\"";

        string scriptPath = Path.Combine(Path.GetTempPath(), "LiteVLC_Update", "apply_update.cmd");
        string script = $@"@echo off
timeout /t 1 /nobreak >nul
""{installerPath}"" /VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS{dirArg}
if exist ""{targetExe}"" start """" ""{targetExe}""
del /f /q ""{installerPath}"" >nul 2>&1
del /f /q ""%~f0"" >nul 2>&1
";
        File.WriteAllText(scriptPath, script, System.Text.Encoding.Default);

        Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c \"\"{scriptPath}\"\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static Version? ParseTagVersion(string tag)
    {
        if (string.IsNullOrWhiteSpace(tag)) return null;
        string clean = tag.Trim().TrimStart('v', 'V');
        int dash = clean.IndexOf('-');
        if (dash > 0) clean = clean[..dash];
        return Version.TryParse(clean, out var v) ? new Version(v.Major, v.Minor, Math.Max(0, v.Build)) : null;
    }
}
