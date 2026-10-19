using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// AliceSoft System 4 "AFAH / AlicArch" archives.
///
/// Classic layout (version 1/2): "AFAH", reserved word, "AlicArch", int32
/// version at 0x10, uint32 base offset at 0x18, "INFO" at 0x1C, then the
/// compressed index size, unpacked size and entry count, followed by a zlib
/// index. Each record is
/// <c>[int32 nameLength][int32 step][step bytes of name]</c>, two skipped
/// int32, (version 1 only: one more), then <c>uint32 offset</c> (relative to
/// the base) and <c>uint32 size</c>.
///
/// Payloads are stored as-is; audio is plain Ogg Vorbis.
/// </summary>
public sealed class AfaReader : IArchiveReader
{
    private static readonly byte[] AffKey =
    [
        0xC8, 0xBB, 0x8F, 0xB7, 0xED, 0x43, 0x99, 0x4A,
        0xA2, 0x7E, 0x5B, 0xB0, 0x68, 0x18, 0xF8, 0x88,
    ];

    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "AFA";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private AfaReader(string path) => ArchivePath = path;

    public static AfaReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x40) return null;

            Span<byte> head = stackalloc byte[0x2C];
            if (fs.Read(head) != head.Length) return null;
            if (!head[..4].SequenceEqual("AFAH"u8)) return null;
            if (!head[8..16].SequenceEqual("AlicArch"u8)) return null;
            if (!head[0x1C..0x20].SequenceEqual("INFO"u8)) return null;

            int version = BinaryPrimitives.ReadInt32LittleEndian(head[0x10..]);
            if (version is < 1 or > 2) return null;

            uint baseOffset = BinaryPrimitives.ReadUInt32LittleEndian(head[0x18..]);
            uint packedSize = BinaryPrimitives.ReadUInt32LittleEndian(head[0x20..]);
            int unpackedSize = BinaryPrimitives.ReadInt32LittleEndian(head[0x24..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(head[0x28..]);

            if (count is <= 0 or > 0x100000) return null;
            if (packedSize == 0 || 0x2C + (long)packedSize > fs.Length) return null;
            if (unpackedSize <= 0 || unpackedSize > 256 * 1024 * 1024) return null;

            var packed = new byte[packedSize];
            fs.Position = 0x2C;
            fs.ReadExactly(packed);

            var index = Codecs.Inflate(packed);
            var reader = new AfaReader(path);
            return reader.ReadIndex(index, version, baseOffset, fs.Length) ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(byte[] index, int version, uint baseOffset, long fileLength)
    {
        int pos = 0;
        while (pos + 8 <= index.Length)
        {
            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(pos));
            int step = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(pos + 4));
            pos += 8;

            if (nameLength <= 0 || nameLength > step || pos + step > index.Length) return false;

            string name = ReadCp932(index.AsSpan(pos, nameLength));
            pos += step;

            pos += 8;                        // two skipped words
            if (version < 2) pos += 4;       // v1 carries an extra word

            if (pos + 8 > index.Length) return false;

            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(pos));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(pos + 4));
            pos += 8;

            long absolute = baseOffset + offset;
            if (absolute + size > fileLength || string.IsNullOrWhiteSpace(name)) return false;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = absolute,
                StoredLength = size,
                UnpackedLength = size,
            });
        }

        return _entries.Count > 0;
    }

    private static string ReadCp932(ReadOnlySpan<byte> raw)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(raw);
        }
        catch
        {
            return Encoding.ASCII.GetString(raw);
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);

        // "AFF\0" payloads have their first 0x40 data bytes XORed with a fixed
        // key; everything else (including audio) is stored verbatim.
        if (entry.StoredLength <= 0x10) return slice;

        Span<byte> marker = stackalloc byte[4];
        slice.Read(marker);
        slice.Position = 0;
        if (!marker.SequenceEqual("AFF\0"u8)) return slice;

        using (slice)
        {
            var data = new byte[entry.StoredLength];
            slice.ReadExactly(data);

            int masked = (int)Math.Min(0x40, data.Length - 0x10);
            for (int i = 0; i < masked; i++) data[0x10 + i] ^= AffKey[i & 0xF];

            return new MemoryStream(data, writable: false);
        }
    }

    public void Dispose() { }
}
