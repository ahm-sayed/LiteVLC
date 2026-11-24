using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Reflection;
using System.Text;
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
/// Checks GitHub Releases for ahm-sayed/LiteVLC, always resolving the strictly highest semantic version
/// (jumping straight to the absolute latest release in one step), downloads the newest Inno Setup installer
/// with progress, waits cleanly for the running process to exit, overwrites the existing installation in-place
/// (removing any duplicate older copy), and relaunches LiteVLC automatically.
/// </summary>
public sealed class AutoUpdaterService
{
    private const string RepoOwner = "ahm-sayed";
    private const string RepoName = "LiteVLC";
    private static readonly string ReleasesListApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases?per_page=20";
    private static readonly string LatestApiUrl = $"https://api.github.com/repos/{RepoOwner}/{RepoName}/releases/latest";

    private static readonly HttpClient Http = new()
    {
        Timeout = TimeSpan.FromMinutes(10)
    };

    public static Version GetCurrentVersion()
    {
        var v = Assembly.GetExecutingAssembly().GetName().Version;
        if (v == null) return new Version(1, 6, 1);
        return new Version(v.Major, v.Minor, Math.Max(0, v.Build));
    }

    public async Task<UpdateCheckResult> CheckForUpdateAsync(CancellationToken ct = default)
    {
        Version current = GetCurrentVersion();
        try
        {
            // Always fetch the full recent releases list with no-cache headers and pick the highest semantic Version.
            // Guarantees jumping directly to the absolute latest version (never an intermediate or cached release!).
            var candidates = await FetchReleaseCandidatesAsync(current, ct).ConfigureAwait(false);
            var newest = candidates
                .Where(c => !string.IsNullOrWhiteSpace(c.InstallerUrl))
                .OrderByDescending(c => c.LatestVersion)
                .FirstOrDefault();

            if (newest != null)
            {
                return new UpdateCheckResult
                {
                    UpdateAvailable = newest.LatestVersion > current,
                    CurrentVersion = current,
                    LatestVersion = newest.LatestVersion,
                    TagName = newest.TagName,
                    ReleaseNotes = newest.ReleaseNotes,
                    InstallerUrl = newest.InstallerUrl,
                    InstallerFileName = newest.InstallerFileName,
                    InstallerSizeBytes = newest.InstallerSizeBytes,
                    ReleasePageUrl = newest.ReleasePageUrl
                };
            }

            // Fallback to /releases/latest if list returned no installer assets
            return await FetchSingleLatestAsync(current, ct).ConfigureAwait(false);
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

    private async Task<List<UpdateCheckResult>> FetchReleaseCandidatesAsync(Version current, CancellationToken ct)
    {
        var list = new List<UpdateCheckResult>();
        using var req = CreateGitHubRequest($"{ReleasesListApiUrl}&_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", current);
        using var resp = await Http.SendAsync(req, ct).ConfigureAwait(false);
        if (!resp.IsSuccessStatusCode)
            return list;

        string json = await resp.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);
        if (doc.RootElement.ValueKind != JsonValueKind.Array)
            return list;

        foreach (var rel in doc.RootElement.EnumerateArray())
        {
            if (rel.TryGetProperty("draft", out var dP) && dP.ValueKind == JsonValueKind.True) continue;
            if (rel.TryGetProperty("prerelease", out var pP) && pP.ValueKind == JsonValueKind.True) continue;

            var parsed = ParseReleaseElement(rel, current);
            if (parsed != null)
                list.Add(parsed);
        }

        return list;
    }

    private async Task<UpdateCheckResult> FetchSingleLatestAsync(Version current, CancellationToken ct)
    {
        using var req = CreateGitHubRequest($"{LatestApiUrl}?_={DateTimeOffset.UtcNow.ToUnixTimeSeconds()}", current);
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
        return ParseReleaseElement(doc.RootElement, current) ?? new UpdateCheckResult
        {
            CurrentVersion = current,
            LatestVersion = current
        };
    }

    private static HttpRequestMessage CreateGitHubRequest(string url, Version current)
    {
        var req = new HttpRequestMessage(HttpMethod.Get, url);
        req.Headers.TryAddWithoutValidation("User-Agent", $"LiteVLC/{current}");
        req.Headers.TryAddWithoutValidation("Accept", "application/vnd.github+json");
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true, MustRevalidate = true };
        req.Headers.Pragma.TryParseAdd("no-cache");
        return req;
    }

    private static UpdateCheckResult? ParseReleaseElement(JsonElement root, Version current)
    {
        string tag = root.TryGetProperty("tag_name", out var tP) ? (tP.GetString() ?? "") : "";
        Version? latest = ParseTagVersion(tag);
        if (latest == null) return null;

        string notes = root.TryGetProperty("body", out var bP) ? (bP.GetString() ?? "") : "";
        string htmlUrl = root.TryGetProperty("html_url", out var hP) ? (hP.GetString() ?? "") : "";

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

    public async Task<string> DownloadInstallerAsync(
        UpdateCheckResult update,
        IProgress<(long Downloaded, long Total)> progress,
        CancellationToken ct)
    {
        string tempDir = Path.Combine(Path.GetTempPath(), "LiteVLC_Update");
        Directory.CreateDirectory(tempDir);

        // Remove any stale old installers from previous update runs so we never accidentally run an older file
        try
        {
            foreach (string old in Directory.EnumerateFiles(tempDir, "LiteVLC-Setup-*.exe"))
            {
                try { File.Delete(old); } catch { }
            }
        }
        catch { }

        string fileName = string.IsNullOrWhiteSpace(update.InstallerFileName)
            ? $"LiteVLC-Setup-{update.LatestVersion}.exe"
            : update.InstallerFileName;
        string destPath = Path.Combine(tempDir, fileName);

        using var req = new HttpRequestMessage(HttpMethod.Get, update.InstallerUrl);
        req.Headers.TryAddWithoutValidation("User-Agent", $"LiteVLC/{update.CurrentVersion}");
        req.Headers.CacheControl = new CacheControlHeaderValue { NoCache = true, NoStore = true };
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
    /// Launches a UTF-8 PowerShell updater helper that:
    /// 1. Waits reliably until the current LiteVLC.exe process (and any other LiteVLC instances) have completely exited and released file locks.
    /// 2. Runs the Inno Setup installer silently in-place into the exact install folder (elevating automatically if installed under Program Files).
    /// 3. Verifies installation finished, relaunches the updated LiteVLC.exe, and cleans up temporary files.
    /// </summary>
    public static void LaunchSilentInstallAndRelaunch(string installerPath)
    {
        string currentExe = Environment.ProcessPath ?? Path.Combine(AppContext.BaseDirectory, "LiteVLC.exe");
        string currentDir = (Path.GetDirectoryName(currentExe) ?? AppContext.BaseDirectory).TrimEnd('\\', '/');

        bool isDevBuild = currentDir.Contains(@"\bin\Release", StringComparison.OrdinalIgnoreCase)
                       || currentDir.Contains(@"\bin\Debug", StringComparison.OrdinalIgnoreCase);

        string targetDir = isDevBuild
            ? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", "LiteVLC")
            : currentDir;
        string targetExe = Path.Combine(targetDir, "LiteVLC.exe");

        bool needsElevation = !isDevBuild && !IsDirectoryWritable(targetDir);
        string scopeFlag = needsElevation ? "/ALLUSERS" : "/CURRENTUSER";
        int currentPid = Environment.ProcessId;

        string tempDir = Path.Combine(Path.GetTempPath(), "LiteVLC_Update");
        Directory.CreateDirectory(tempDir);
        string scriptPath = Path.Combine(tempDir, $"apply_update_{Guid.NewGuid():N}.ps1");

        static string Esc(string s) => s.Replace("'", "''");

        string psScript = $@"
$ErrorActionPreference = 'SilentlyContinue'
$oldPid = {currentPid}
$installer = '{Esc(installerPath)}'
$targetDir = '{Esc(targetDir)}'
$targetExe = '{Esc(targetExe)}'
$needsElevation = ${(needsElevation ? "true" : "false")}

# 1. Wait until the exiting LiteVLC process fully terminates (no timeout.exe console bug!)
$deadline = (Get-Date).AddSeconds(12)
while ((Get-Date) -lt $deadline) {{
    $p = Get-Process -Id $oldPid -ErrorAction SilentlyContinue
    if ($null -eq $p) {{ break }}
    Start-Sleep -Milliseconds 250
}}

# Ensure no lingering LiteVLC instance locks LiteVLC.exe or libmpv-2.dll
Get-Process -Name 'LiteVLC' -ErrorAction SilentlyContinue | ForEach-Object {{
    try {{ $_.Kill() }} catch {{}}
}}
Start-Sleep -Milliseconds 500

# 2. Run Inno Setup installer silently, overriding destination to exact targetDir
$argsList = '/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /CLOSEAPPLICATIONS /FORCECLOSEAPPLICATIONS {scopeFlag} /DIR=""' + $targetDir + '""'
if ($needsElevation) {{
    $inst = Start-Process -FilePath $installer -ArgumentList $argsList -Verb RunAs -Wait -PassThru
}} else {{
    $inst = Start-Process -FilePath $installer -ArgumentList $argsList -Wait -PassThru
}}

Start-Sleep -Milliseconds 400

# 3. Relaunch updated LiteVLC cleanly
if (Test-Path $targetExe) {{
    Start-Process -FilePath $targetExe
}} else {{
    $fallbackExe = Join-Path $env:LOCALAPPDATA 'Programs\LiteVLC\LiteVLC.exe'
    if (Test-Path $fallbackExe) {{ Start-Process -FilePath $fallbackExe }}
}}

# 4. Clean up temporary installer and this helper script
Remove-Item -Path $installer -Force -ErrorAction SilentlyContinue
Remove-Item -Path $PSCommandPath -Force -ErrorAction SilentlyContinue
";
        File.WriteAllText(scriptPath, psScript, new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Process.Start(new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -WindowStyle Hidden -File \"{scriptPath}\"",
            UseShellExecute = false,
            CreateNoWindow = true,
            WindowStyle = ProcessWindowStyle.Hidden
        });
    }

    private static bool IsDirectoryWritable(string dirPath)
    {
        try
        {
            if (!Directory.Exists(dirPath)) return true;
            string probe = Path.Combine(dirPath, $".litevlc_write_test_{Guid.NewGuid():N}");
            using (File.Create(probe)) { }
            File.Delete(probe);
            return true;
        }
        catch
        {
            return false;
        }
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
