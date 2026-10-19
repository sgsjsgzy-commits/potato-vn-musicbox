using CommunityToolkit.Mvvm.ComponentModel;

namespace PotatoVN.MusicBox.Models;

/// <summary>
/// Persisted plugin state. Every field must be JSON-friendly and survive
/// version upgrades; <see cref="Version"/> lets us migrate old payloads.
/// </summary>
public partial class PluginData : ObservableRecipient
{
    public const int CurrentVersion = 1;

    [ObservableProperty] private int _version = CurrentVersion;

    /// <summary>Include voice clips in the library (off by default: thousands of files).</summary>
    [ObservableProperty] private bool _includeVoice;

    /// <summary>Include sound effects in the library.</summary>
    [ObservableProperty] private bool _includeSoundEffects;

    /// <summary>Probe track duration while scanning (can be turned off on slow disks).</summary>
    [ObservableProperty] private bool _probeDuration = true;

    /// <summary>Restore the previous queue and position when the player opens.</summary>
    [ObservableProperty] private bool _rememberPlayback = true;

    /// <summary>Playback volume, 0..1.</summary>
    [ObservableProperty] private double _volume = 0.8;

    /// <summary>Playlist repeat mode: "none", "all", "one".</summary>
    [ObservableProperty] private string _repeatMode = "all";

    /// <summary>Shuffle the queue instead of playing in order.</summary>
    [ObservableProperty] private bool _shuffle;

    /// <summary>Last selected game (Galgame.Uuid).</summary>
    [ObservableProperty] private string? _lastGameId;

    /// <summary>Last played track id, used to restore the queue.</summary>
    [ObservableProperty] private string? _lastTrackId;

    /// <summary>Playback position in seconds for <see cref="LastTrackId"/>.</summary>
    [ObservableProperty] private double _lastPositionSeconds;

    /// <summary>
    /// Where scan caches live. Empty means "next to the game library", which
    /// keeps a large cache off the system drive.
    /// </summary>
    [ObservableProperty] private string? _cacheFolder;

    /// <summary>
    /// Legacy scan cache from version 1. Migrated to <see cref="CacheFolder"/>
    /// on load and cleared afterwards; kept so old payloads deserialise.
    /// </summary>
    public Dictionary<string, CachedLibrary> Libraries { get; set; } = new();

    /// <summary>Records the resume point without waking up the save-on-change handler for each field.</summary>
    public void ApplyLastTrack(string? trackId, double positionSeconds)
    {
        LastTrackId = trackId;
        LastPositionSeconds = positionSeconds;
    }
}

/// <summary>
/// Cached scan result. Tracks store enough to rebuild the playlist without
/// rescanning, but playback always re-opens the archive so stale offsets after
/// a game update are detected and the cache is refreshed.
/// </summary>
public sealed class CachedLibrary
{
    public string GameName { get; set; } = string.Empty;
    public string GamePath { get; set; } = string.Empty;
    public DateTime ScannedAt { get; set; }
    public List<CachedTrack> Tracks { get; set; } = [];
    public List<string> Warnings { get; set; } = [];
}

public sealed class CachedTrack
{
    public string Id { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public string RelativeFolder { get; set; } = string.Empty;
    public string Container { get; set; } = string.Empty;
    public string ArchiveName { get; set; } = string.Empty;
    public int Kind { get; set; }
    public double DurationSeconds { get; set; }
    public string ArchivePath { get; set; } = string.Empty;
    public string EntryPath { get; set; } = string.Empty;
    public string FilePath { get; set; } = string.Empty;
    public long Offset { get; set; }
    public long StoredLength { get; set; }
    public long UnpackedLength { get; set; }
    public bool IsCompressed { get; set; }
    public bool IsEncrypted { get; set; }
}
