namespace MusicBox.Core.Models;

public enum TrackKind
{
    Bgm,
    Voice,
    SoundEffect,
    Unknown,
}

/// <summary>
/// A playable track discovered in a game folder.
/// </summary>
public sealed class GameTrack
{
    /// <summary>Stable identity: hash of source + entry path.</summary>
    public string Id { get; init; } = string.Empty;

    /// <summary>Display title derived from the file name (extension stripped).</summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>File name including extension.</summary>
    public string FileName { get; init; } = string.Empty;

    /// <summary>Relative folder inside the game (e.g. "bgm", "sound/se").</summary>
    public string RelativeFolder { get; init; } = string.Empty;

    /// <summary>Container format label shown in the UI ("OGG", "XP3", "YPF", ...).</summary>
    public string Container { get; init; } = string.Empty;

    /// <summary>Archive file name if the track lives in an archive, else empty.</summary>
    public string ArchiveName { get; init; } = string.Empty;

    public TrackKind Kind { get; init; }

    /// <summary>Playable payload. Loose files point directly at disk.</summary>
    public ArchiveEntry Source { get; init; } = null!;

    /// <summary>Duration in seconds when known (0 if not probed yet).</summary>
    public double DurationSeconds { get; set; }

    /// <summary>Disc/album hint for grouping (folder name).</summary>
    public string Album => string.IsNullOrEmpty(RelativeFolder) ? "游戏根目录" : RelativeFolder;

    /// <summary>Searchable text (lowercase, no extension).</summary>
    public string SearchText { get; init; } = string.Empty;

    public override string ToString() => Title;
}

/// <summary>
/// Result of scanning one game folder.
/// </summary>
public sealed class GameMusicLibrary
{
    public string GameName { get; init; } = string.Empty;
    public string GamePath { get; init; } = string.Empty;
    public Guid GameId { get; init; }
    public List<GameTrack> Tracks { get; } = [];
    public List<string> Warnings { get; } = [];
    public DateTime ScannedAt { get; init; } = DateTime.Now;
    public TimeSpan ScanDuration { get; set; }

    public IEnumerable<GameTrack> Bgm => Tracks.Where(t => t.Kind == TrackKind.Bgm);
    public IEnumerable<GameTrack> Voice => Tracks.Where(t => t.Kind == TrackKind.Voice);
    public IEnumerable<GameTrack> SoundEffects => Tracks.Where(t => t.Kind == TrackKind.SoundEffect);
}
