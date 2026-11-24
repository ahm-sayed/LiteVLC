using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Xml.Linq;
using LiteVLC.Models;

namespace LiteVLC.Services;

/// <summary>
/// Reads and writes standard VLC-compatible XSPF (.xspf) and M3U8/M3U playlists with full UTF-8/Arabic path support.
/// </summary>
public static class PlaylistFileService
{
    private static readonly XNamespace XspfNs = "http://xspf.org/ns/0/";

    public static bool IsPlaylistFile(string path)
    {
        string ext = Path.GetExtension(path);
        return ext.Equals(".xspf", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase);
    }

    public static void Save(string filePath, IEnumerable<PlaylistItem> items)
    {
        string ext = Path.GetExtension(filePath);
        if (ext.Equals(".m3u", StringComparison.OrdinalIgnoreCase) || ext.Equals(".m3u8", StringComparison.OrdinalIgnoreCase))
            SaveM3u8(filePath, items);
        else
            SaveXspf(filePath, items);
    }

    public static List<(string Path, string Title)> Load(string filePath)
    {
        if (!File.Exists(filePath)) return new List<(string, string)>();
        string ext = Path.GetExtension(filePath);
        if (ext.Equals(".xspf", StringComparison.OrdinalIgnoreCase))
            return LoadXspf(filePath);
        return LoadM3u(filePath);
    }

    private static void SaveXspf(string filePath, IEnumerable<PlaylistItem> items)
    {
        var trackList = new XElement(XspfNs + "trackList");
        foreach (var item in items)
        {
            string location = item.IsStream
                ? item.Path
                : new Uri(Path.GetFullPath(item.Path)).AbsoluteUri;

            var track = new XElement(XspfNs + "track",
                new XElement(XspfNs + "location", location),
                new XElement(XspfNs + "title", item.Title));
            trackList.Add(track);
        }

        var doc = new XDocument(
            new XDeclaration("1.0", "UTF-8", null),
            new XElement(XspfNs + "playlist",
                new XAttribute("version", "1"),
                new XElement(XspfNs + "title", Path.GetFileNameWithoutExtension(filePath)),
                new XElement(XspfNs + "creator", "LiteVLC"),
                trackList));

        using var writer = new StreamWriter(filePath, false, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        doc.Save(writer);
    }

    private static List<(string Path, string Title)> LoadXspf(string filePath)
    {
        var results = new List<(string, string)>();
        string baseDir = Path.GetDirectoryName(filePath) ?? "";

        try
        {
            var doc = XDocument.Load(filePath);
            var tracks = doc.Descendants().Where(e => e.Name.LocalName.Equals("track", StringComparison.OrdinalIgnoreCase));
            foreach (var tr in tracks)
            {
                string? loc = tr.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("location", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                if (string.IsNullOrWhiteSpace(loc)) continue;

                string resolved = ResolveEntryPath(loc, baseDir);
                string? title = tr.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("title", StringComparison.OrdinalIgnoreCase))?.Value.Trim();
                if (string.IsNullOrWhiteSpace(title))
                    title = IsUrl(resolved) ? resolved : Path.GetFileName(resolved);

                results.Add((resolved, title));
            }
        }
        catch { }

        return results;
    }

    private static void SaveM3u8(string filePath, IEnumerable<PlaylistItem> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("#EXTM3U");
        foreach (var item in items)
        {
            sb.AppendLine($"#EXTINF:-1,{item.Title}");
            sb.AppendLine(item.Path);
        }
        File.WriteAllText(filePath, sb.ToString(), new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));
    }

    private static List<(string Path, string Title)> LoadM3u(string filePath)
    {
        var results = new List<(string, string)>();
        string baseDir = Path.GetDirectoryName(filePath) ?? "";
        string? pendingTitle = null;

        try
        {
            foreach (string rawLine in File.ReadLines(filePath, Encoding.UTF8))
            {
                string line = rawLine.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = line.IndexOf(',');
                    pendingTitle = comma >= 0 ? line[(comma + 1)..].Trim() : null;
                    continue;
                }
                if (line.StartsWith('#')) continue;

                string resolved = ResolveEntryPath(line, baseDir);
                string title = !string.IsNullOrWhiteSpace(pendingTitle)
                    ? pendingTitle
                    : (IsUrl(resolved) ? resolved : Path.GetFileName(resolved));
                pendingTitle = null;

                results.Add((resolved, title));
            }
        }
        catch { }

        return results;
    }

    private static string ResolveEntryPath(string location, string baseDir)
    {
        if (IsUrl(location)) return location;

        if (location.StartsWith("file://", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(location, UriKind.Absolute, out var uri))
        {
            return uri.LocalPath;
        }

        string unescaped = Uri.UnescapeDataString(location);
        if (!Path.IsPathRooted(unescaped) && !string.IsNullOrWhiteSpace(baseDir))
            return Path.GetFullPath(Path.Combine(baseDir, unescaped));

        return unescaped;
    }

    private static bool IsUrl(string s)
        => s.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
        || s.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);
}
