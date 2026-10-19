using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// BGI/Ethornell "PackFile" and "BURIKO ARC20" archives.
///
/// PackFile: "PackFile    " + uint32 count, then 0x20-byte records
/// (16-byte name, uint32 offset, uint32 size) and a data area.
/// ARC20:    "BURIKO ARC20" + int32 count, then 0x80-byte records
/// (0x60-byte name, uint32 offset, uint32 size).
///
/// Audio entries may be raw Ogg or "BW" containers (uint32 ogg offset + "bw  ").
/// </summary>
public sealed class BgiReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];
    private readonly bool _isAudioContainer;

    public string FormatName { get; private init; } = "BGI";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private BgiReader(string path, bool isAudioContainer)
    {
        ArchivePath = path;
        _isAudioContainer = isAudioContainer;
    }

    public static BgiReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x20) return null;

            Span<byte> head = stackalloc byte[16];
            if (fs.Read(head) != 16) return null;

            bool packFile = head[..4].SequenceEqual("Pack"u8) && head[4..12].SequenceEqual("File    "u8);
            bool buriko = head[..4].SequenceEqual("BURI"u8) && head[4..12].SequenceEqual("KO ARC20"u8);
            if (!packFile && !buriko) return null;

            var reader = new BgiReader(path, isAudioContainer: false)
            {
                FormatName = packFile ? "BGI" : "BGI2",
            };

            return reader.ReadIndex(fs, packFile) ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(FileStream fs, bool packFile)
    {
        int recordSize = packFile ? 0x20 : 0x80;
        int nameSize = packFile ? 0x10 : 0x60;

        Span<byte> countBytes = stackalloc byte[4];
        fs.Position = 12;
        fs.ReadExactly(countBytes);
        int count = BinaryPrimitives.ReadInt32LittleEndian(countBytes);
        if (count is <= 0 or > 0x100000) return false;

        long indexSize = (long)recordSize * count;
        long baseOffset = 0x10 + indexSize;
        if (baseOffset > fs.Length) return false;

        var index = new byte[indexSize];
        fs.Position = 0x10;
        fs.ReadExactly(index);

        Span<byte> probe = stackalloc byte[8];
        for (int i = 0; i < count; i++)
        {
            int record = i * recordSize;
            string name = ReadName(index.AsSpan(record, nameSize));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(record + nameSize));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(record + nameSize + 4));

            long absolute = baseOffset + offset;
            if (absolute < 0 || absolute + size > fs.Length || string.IsNullOrWhiteSpace(name)) continue;

            // Audio containers hide an Ogg stream behind a 8-byte "bw" header.
            long payloadOffset = absolute;
            long payloadLength = size;
            bool audioContainer = false;
            if (size >= 8)
            {
                fs.Position = absolute;
                fs.ReadExactly(probe);
                if (probe[4..8].SequenceEqual("bw  "u8))
                {
                    uint inner = BinaryPrimitives.ReadUInt32LittleEndian(probe);
                    if (inner > 0 && inner < size)
                    {
                        payloadOffset = absolute + inner;
                        payloadLength = size - inner;
                        audioContainer = true;
                    }
                }
            }

            string entryName = audioContainer && !name.Contains('.') ? name + ".ogg" : name;
            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = entryName,
                Offset = payloadOffset,
                StoredLength = payloadLength,
                UnpackedLength = payloadLength,
                Tag = audioContainer,
            });
        }

        return _entries.Count > 0;
    }

    private static string ReadName(ReadOnlySpan<byte> raw)
    {
        int length = raw.IndexOf((byte)0);
        if (length < 0) length = raw.Length;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(raw[..length]);
        }
        catch
        {
            return Encoding.ASCII.GetString(raw[..length]);
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry) =>
        new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);

    public void Dispose() { }
}
