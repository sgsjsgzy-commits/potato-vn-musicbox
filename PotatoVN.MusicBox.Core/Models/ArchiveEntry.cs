namespace MusicBox.Core.Models;

/// <summary>
/// One file located inside a game archive (or a loose file on disk).
/// Offsets/lengths are in bytes. A virtual entry (loose file) leaves
/// <see cref="ArchivePath"/> empty and uses <see cref="FilePath"/> directly.
/// </summary>
public sealed class ArchiveEntry
{
    /// <summary>Path of the containing archive, or the file itself for loose files.</summary>
    public string ArchivePath { get; init; } = string.Empty;

    /// <summary>Entry path inside the archive. Empty for loose files.</summary>
    public string EntryPath { get; init; } = string.Empty;

    /// <summary>Absolute path for loose files; empty for archive entries.</summary>
    public string FilePath { get; init; } = string.Empty;

    /// <summary>Offset of the payload inside <see cref="ArchivePath"/>.</summary>
    public long Offset { get; init; }

    /// <summary>Stored (possibly compressed/encrypted) payload length.</summary>
    public long StoredLength { get; init; }

    /// <summary>Expected length after decompression/decryption, when known.</summary>
    public long UnpackedLength { get; init; }

    /// <summary>True when the payload must be decompressed by the reader.</summary>
    public bool IsCompressed { get; init; }

    /// <summary>True when the payload must be decrypted by the reader.</summary>
    public bool IsEncrypted { get; init; }

    /// <summary>Engine-specific hint used by the matching reader (segment list, prefix, etc.).</summary>
    public object? Tag { get; init; }

    /// <summary>File name without directories, as stored in the archive.</summary>
    public string Name => Path.GetFileName(EntryPath.Replace('\\', '/'));

    public bool IsLooseFile => string.IsNullOrEmpty(ArchivePath);

    /// <summary>
    /// Copy carrying a different display/entry name. Payload location and
    /// decoding hints are unchanged.
    /// </summary>
    public ArchiveEntry WithName(string name) => new()
    {
        ArchivePath = ArchivePath,
        EntryPath = name,
        FilePath = FilePath,
        Offset = Offset,
        StoredLength = StoredLength,
        UnpackedLength = UnpackedLength,
        IsCompressed = IsCompressed,
        IsEncrypted = IsEncrypted,
        Tag = Tag,
    };

    public override string ToString() => IsLooseFile ? FilePath : $"{ArchivePath}::{EntryPath}";
}
