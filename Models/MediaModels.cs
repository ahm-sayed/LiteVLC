using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace LiteVLC.Models;

public sealed class PlaylistItem : INotifyPropertyChanged
{
    private bool _isPlaying;
    private string _durationText = "--:--";

    public string Path { get; init; } = "";
    public string Title { get; init; } = "";
    public bool IsStream => Path.StartsWith("http://", StringComparison.OrdinalIgnoreCase)
                         || Path.StartsWith("https://", StringComparison.OrdinalIgnoreCase)
                         || Path.StartsWith("rtsp://", StringComparison.OrdinalIgnoreCase);

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnPropertyChanged();
        }
    }

    public string DurationText
    {
        get => _durationText;
        set
        {
            if (_durationText == value) return;
            _durationText = value;
            OnPropertyChanged();
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class MediaTrack
{
    public int Id { get; init; }
    public string Type { get; init; } = ""; // "video", "audio", "sub"
    public string Title { get; init; } = "";
    public string Lang { get; init; } = "";
    public string Codec { get; init; } = "";
    public bool Selected { get; init; }
    public bool External { get; init; }

    public string DisplayName
    {
        get
        {
            var parts = new List<string>();
            if (!string.IsNullOrWhiteSpace(Title)) parts.Add(Title);
            if (!string.IsNullOrWhiteSpace(Lang)) parts.Add(Lang.ToUpperInvariant());
            if (!string.IsNullOrWhiteSpace(Codec)) parts.Add(Codec.ToUpperInvariant());
            if (External) parts.Add("EXT");
            return parts.Count > 0 ? $"#{Id}  {string.Join(" • ", parts)}" : $"Track #{Id}";
        }
    }
}

public sealed class PlaybackStats
{
    public string VideoCodec { get; set; } = "—";
    public string AudioCodec { get; set; } = "—";
    public string HwDec { get; set; } = "auto";
    public string Resolution { get; set; } = "—";
    public double Fps { get; set; }
    public double CacheSeconds { get; set; }
    public double AvSyncMs { get; set; }
    public long DroppedFrames { get; set; }
    public double LastSeekMs { get; set; }
    public bool FastSeekEnabled { get; set; } = true;
}

public sealed class RecentFileEntry
{
    public string Path { get; init; } = "";
    public string FileName { get; init; } = "";
    public string ResumeBadge { get; init; } = "";
    public bool HasResume => !string.IsNullOrEmpty(ResumeBadge);
}

public enum LoopMode
{
    Off = 0,
    RepeatAll = 1,
    RepeatOne = 2
}

public sealed class AppPersistedState
{
    public double Volume { get; set; } = 100;
    public bool FastSeek { get; set; } = true;
    public bool RememberPosition { get; set; } = true;
    public bool AudioBoost { get; set; } = true;
    public string PreferredSubLang { get; set; } = "ara";
    public List<string> RecentFiles { get; set; } = new();
    public Dictionary<string, double> ResumePositions { get; set; } = new();
}

public static class StateStore
{
    private static readonly string StateDir =
        System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LiteVLC");
    private static readonly string StateFile = System.IO.Path.Combine(StateDir, "state.json");

    public static AppPersistedState Load()
    {
        try
        {
            if (File.Exists(StateFile))
            {
                var json = File.ReadAllText(StateFile);
                return JsonSerializer.Deserialize<AppPersistedState>(json) ?? new AppPersistedState();
            }
        }
        catch
        {
            // Fall back to defaults cleanly
        }
        return new AppPersistedState();
    }

    public static void Save(AppPersistedState state)
    {
        try
        {
            Directory.CreateDirectory(StateDir);
            if (state.RecentFiles.Count > 12)
                state.RecentFiles.RemoveRange(12, state.RecentFiles.Count - 12);
            if (state.ResumePositions.Count > 200)
            {
                var keys = new List<string>(state.ResumePositions.Keys);
                for (int i = 0; i < keys.Count - 150; i++)
                    state.ResumePositions.Remove(keys[i]);
            }
            var json = JsonSerializer.Serialize(state, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(StateFile, json);
        }
        catch
        {
            // Ignore persistence errors on exit
        }
    }
}
