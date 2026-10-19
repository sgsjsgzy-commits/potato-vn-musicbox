using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Parsed archive index. Implementations only read the directory; payloads are
/// opened lazily through <see cref="OpenEntryStream"/>.
/// </summary>
public interface IArchiveReader : IDisposable
{
    /// <summary>Human readable format label, e.g. "XP3", "LINK6".</summary>
    string FormatName { get; }

    string ArchivePath { get; }

    IReadOnlyList<ArchiveEntry> Entries { get; }

    /// <summary>
    /// True when the payloads of encrypted entries can still be read (the
    /// encryption only covered filenames). Readers that cannot decrypt report
    /// false so the scanner can skip those entries instead of emitting silence.
    /// </summary>
    bool CanDecryptPayload => false;

    /// <summary>
    /// Human-readable reason when the archive is present but its index is
    /// protected (e.g. KrkrZ "Cxdec"/Hxv4 needs runtime state). Null otherwise.
    /// </summary>
    string? IndexProtectionNote => null;

    /// <summary>Opens a readable stream over the entry's decoded payload.</summary>
    Stream OpenEntryStream(ArchiveEntry entry);
}

/// <summary>
/// Detects and opens supported archives. Every reader must be safe against
/// malformed input: probing never throws, it returns null instead.
/// </summary>
public static class ArchiveRegistry
{
    public static readonly string[] SupportedExtensions =
    [
        ".xp3", ".arc", ".pak", ".pac", ".noa", ".ypf", ".rpa", ".nsa", ".dat", ".bin", ".pck",
        ".int", ".afa", ".owp",
    ];

    /// <summary>
    /// Tries every reader in order. Returns null when the file is not a
    /// recognised archive or when its index cannot be parsed.
    /// </summary>
    public static IArchiveReader? TryOpen(string path)
    {
        foreach (var factory in Factories)
        {
            try
            {
                var reader = factory(path);
                if (reader is not null) return reader;
            }
            catch
            {
                // A malformed archive must never break a full-library scan.
            }
        }

        return null;
    }

    /// <summary>
    /// Probe order matters: unambiguous signatures first, then the formats that
    /// are only recognisable by a structurally valid index. Signatureless
    /// readers (FVP bin, KIF) validate every record before they accept a file,
    /// so they are safe to keep last.
    /// </summary>
    private static readonly List<Func<string, IArchiveReader?>> Factories =
    [
        Xp3Reader.TryOpen,
        LinkReader.TryOpen,
        YpfReader.TryOpen,
        RpaReader.TryOpen,
        BgiReader.TryOpen,
        NsaReader.TryOpen,
        NekoPackReader.TryOpen,
        LeafLacReader.TryOpen,
        AfaReader.TryOpen,
        SoftpalPacReader.TryOpen,
        TacticsReader.TryOpen,
        EscudeReader.TryOpen,
        CatSystem2Reader.TryOpen,
        EntisNoaReader.TryOpen,
        FavoriteReader.TryOpen,
        FavoriteBinReader.TryOpen,
        SimpleArcReader.TryOpen,
        XorOggReader.TryOpen,
    ];

    /// <summary>True when the file starts with one of the known archive signatures.</summary>
    public static bool HasKnownSignature(string path)
    {
        try
        {
            using var fs = File.OpenRead(path);
            Span<byte> head = stackalloc byte[16];
            var read = fs.Read(head);
            if (read < 4) return false;

            return head[..Math.Min(read, 16)] switch
            {
                [0x58, 0x50, 0x33, 0x0D, ..] => true, // XP3
                [0x4C, 0x49, 0x4E, 0x4B, ..] => true, // LINK
                [0x59, 0x50, 0x46, 0x00, ..] => true, // YPF
                [0x52, 0x50, 0x41, 0x2D, ..] => true, // RPA-
                [0x50, 0x61, 0x63, 0x6B, ..] => true, // Pack (BGI)
                [0x42, 0x55, 0x52, 0x49, ..] => true, // BURI (BGI v2)
                [0x4E, 0x45, 0x4B, 0x4F, ..] => true, // NEKOPACK4A
                [0x4B, 0x49, 0x46, 0x00, ..] => true, // KIF (CatSystem2)
                [0x4C, 0x41, 0x43, 0x00, ..] => true, // LAC (Leaf)
                [0x41, 0x46, 0x41, 0x48, ..] => true, // AFAH (AliceSoft)
                [0x45, 0x53, 0x43, 0x2D, ..] => true, // ESC-ARC (Escu:de)
                [0x54, 0x41, 0x43, 0x54, ..] => true, // TACTICS_ARC_FILE
                [0x45, 0x6E, 0x74, 0x69, ..] => true, // Entis NOA
                [0x50, 0x41, 0x43, 0x20, ..] => true, // "PAC " (Softpal)
                _ => false,
            };
        }
        catch
        {
            return false;
        }
    }
}
