using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Leaf "LAC" resource archives used by Aquaplus titles.
///
/// Two layouts share the "LAC\0" magic:
///  - hierarchical LAC: int32 count at 4, then 0x78-byte records with a
///    0x3E-byte CP932 name, a pack flag at 0x3E, stored size at 0x54,
///    unpacked size at 0x58 and an int64 offset at 0x60. Packed entries are
///    LZSS with a space-filled ring buffer.
///  - PAK/LAC: 0x28-byte records holding a name masked with 0xFF, a pack flag
///    at 0x1F, uint32 size at 0x20 and uint32 offset at 0x24. Packed payloads
///    start with their unpacked size.
///
/// Audio is stored as plain Ogg Vorbis.
/// </summary>
public sealed class LeafLacReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];
    private readonly bool _hierarchical;

    public string FormatName { get; private init; } = "LAC";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private LeafLacReader(string path, bool hierarchical)
    {
        ArchivePath = path;
        _hierarchical = hierarchical;
    }

    public static LeafLacReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x10) return null;

            Span<byte> head = stackalloc byte[8];
            if (fs.Read(head) != 8) return null;
            if (!head[..3].SequenceEqual("LAC"u8) || head[3] != 0) return null;

            int count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
            if (count is <= 0 or > 0x100000) return null;

            // The record stride tells the layouts apart: a hierarchical archive
            // must fit its 0x78-byte records, a PAK its 0x28-byte ones.
            long hierEnd = 8L + (long)count * 0x78;
            if (hierEnd <= fs.Length)
            {
                var lac = new LeafLacReader(path, hierarchical: true);
                if (lac.ReadHierarchical(fs, count)) return lac;
            }

            var pak = new LeafLacReader(path, hierarchical: false) { FormatName = "LAC" };
            return pak.ReadPak(fs, count) ? pak : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadHierarchical(FileStream fs, int count)
    {
        var record = new byte[0x78];
        fs.Position = 8;

        for (int i = 0; i < count; i++)
        {
            if (fs.Read(record, 0, record.Length) != record.Length) return false;

            string name = ReadCp932(record.AsSpan(0, 0x3E));
            if (string.IsNullOrEmpty(name)) return false;

            bool packed = record[0x3E] != 0;
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x54));
            uint unpacked = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x58));
            long offset = BinaryPrimitives.ReadInt64LittleEndian(record.AsSpan(0x60));

            if (offset < 0 || offset + stored > fs.Length) return false;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = offset,
                StoredLength = stored,
                UnpackedLength = unpacked > 0 ? unpacked : stored,
                IsCompressed = packed,
                Tag = packed ? "LZSS" : null,
            });
        }

        return _entries.Count > 0;
    }

    private bool ReadPak(FileStream fs, int count)
    {
        var record = new byte[0x28];
        fs.Position = 8;

        for (int i = 0; i < count; i++)
        {
            if (fs.Read(record, 0, record.Length) != record.Length) return false;

            // The name is stored with every byte XORed with 0xFF; the pack flag
            // sits outside the masked area.
            int length = 0;
            while (length < 0x1F && record[length] != 0) length++;
            if (length == 0) return false;

            for (int b = 0; b < length; b++) record[b] ^= 0xFF;
            string name = ReadCp932(record.AsSpan(0, length));
            if (string.IsNullOrWhiteSpace(name)) return false;

            bool packed = record[0x1F] != 0;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x20));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x24));

            if (offset + (long)size > fs.Length) return false;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = offset,
                StoredLength = size,
                UnpackedLength = size,
                IsCompressed = packed,
                Tag = packed ? "LZSS" : null,
            });
        }

        return _entries.Count > 0;
    }

    private static string ReadCp932(ReadOnlySpan<byte> raw)
    {
        int length = raw.IndexOf((byte)0);
        if (length < 0) length = raw.Length;
        if (length == 0) return string.Empty;

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

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        if (!entry.IsCompressed) return new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);

        long offset = entry.Offset;
        long size = entry.StoredLength;
        int unpacked = (int)Math.Min(entry.UnpackedLength, int.MaxValue);

        using var slice = new FileSliceStream(ArchivePath, offset, size);
        var payload = new byte[size];
        slice.ReadExactly(payload);

        if (!_hierarchical && payload.Length > 4)
        {
            // PAK entries start with a size word. When it merely repeats the
            // stored size, the next word holds the unpacked size instead.
            uint first = BinaryPrimitives.ReadUInt32LittleEndian(payload);
            if (first == size)
            {
                payload = payload[4..];
                if (payload.Length >= 4)
                {
                    unpacked = (int)Math.Min(
                        BinaryPrimitives.ReadUInt32LittleEndian(payload), int.MaxValue);
                    payload = payload[4..];
                }
            }
            else
            {
                unpacked = (int)Math.Min(first, int.MaxValue);
                payload = payload[4..];
            }

            if (unpacked <= 0) unpacked = 1;
        }

        if (payload.Length == 0) return new MemoryStream([], writable: false);

        var decoded = Codecs.DecodeLzss(payload, unpacked, frameFill: _hierarchical ? (byte)0x20 : (byte)0);
        return new MemoryStream(decoded, writable: false);
    }

    public void Dispose() { }
}
