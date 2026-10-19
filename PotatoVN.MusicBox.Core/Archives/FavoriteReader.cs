using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Favorite View Point resource archives.
///
/// Two unrelated layouts share the "bin" extension:
///  - FVP v2: int32 count, uint32 name-pool size, then a 12-byte index
///    (name offset, payload offset, size) followed by a NUL-separated name pool.
///  - ACPXPK: "ACPXPK01" / "ACPX_PK.1" magic, count at 8, then 0x28-byte
///    records (0x20-byte name, uint32 offset, uint32 size). Payloads may be
///    preceded by an "acp\0" + big-endian unpacked size header and LZW-coded.
///
/// The engine stores music as plain Ogg Vorbis inside these archives.
/// </summary>
public sealed class FavoriteReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];
    private readonly bool _lzw;

    public string FormatName { get; private init; } = "FVP";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private FavoriteReader(string path, bool lzw)
    {
        ArchivePath = path;
        _lzw = lzw;
    }

    public static FavoriteReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x20) return null;

            Span<byte> head = stackalloc byte[12];
            if (fs.Read(head) != 12) return null;

            if (head[..4].SequenceEqual("ACPX"u8)
                && (head[4..7].SequenceEqual("XPK"u8) || head[4..7].SequenceEqual("_PK"u8)))
            {
                var acp = new FavoriteReader(path, lzw: true) { FormatName = "FVP" };
                return acp.ReadAcpIndex(fs) ? acp : null;
            }

            // FVP v2 has no magic: validate the name pool placement instead.
            fs.Position = 0;
            var v2 = new FavoriteReader(path, lzw: false) { FormatName = "FVP" };
            return v2.ReadV2Index(fs) ? v2 : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadAcpIndex(FileStream fs)
    {
        fs.Position = 8;
        Span<byte> word = stackalloc byte[4];
        if (fs.Read(word) != 4) return false;

        int count = BinaryPrimitives.ReadInt32LittleEndian(word);
        if (count is <= 0 or > 0x100000) return false;

        long indexOffset = 0x0C;
        if (indexOffset + (long)count * 0x28 > fs.Length) return false;

        var record = new byte[0x28];
        for (int i = 0; i < count; i++)
        {
            fs.Position = indexOffset + i * 0x28;
            fs.ReadExactly(record);

            string name = ReadCp932(record.AsSpan(0, 0x20));
            if (string.IsNullOrEmpty(name)) return false;

            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x20));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(0x24));
            if (offset + (long)size > fs.Length) return false;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = offset,
                StoredLength = size,
                UnpackedLength = size,
                IsCompressed = true, // "acp" payloads are LZW; plain ones pass through
                Tag = "ACP",
            });
        }

        return _entries.Count > 0;
    }

    private bool ReadV2Index(FileStream fs)
    {
        fs.Position = 0;
        Span<byte> header = stackalloc byte[8];
        if (fs.Read(header) != 8) return false;

        int count = BinaryPrimitives.ReadInt32LittleEndian(header);
        if (count is <= 0 or > 0x100000) return false;

        uint namePoolSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
        long indexSize = (long)count * 12;
        long namesBase = 8 + indexSize;

        if (namePoolSize == 0 || namesBase + namePoolSize > fs.Length) return false;
        if (namesBase + namePoolSize >= fs.Length) return false;

        var index = new byte[indexSize];
        fs.Position = 8;
        fs.ReadExactly(index);

        var names = new byte[namePoolSize];
        fs.Position = namesBase;
        fs.ReadExactly(names);

        // Every record must point inside the pool and inside the file; one
        // mismatch means this is not an FVP v2 archive at all.
        var pending = new List<(string Name, uint Offset, uint Size)>(count);
        for (int i = 0; i < count; i++)
        {
            uint nameOffset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 12));
            if (nameOffset >= namePoolSize) return false;

            int end = Array.IndexOf(names, (byte)0, (int)nameOffset);
            if (end < 0) end = names.Length;
            string name = ReadCp932(names.AsSpan((int)nameOffset, end - (int)nameOffset));
            if (string.IsNullOrEmpty(name)) return false;

            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 12 + 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 12 + 8));
            if (offset + (long)size > fs.Length || size == 0) return false;

            pending.Add((name, offset, size));
        }

        foreach (var (name, offset, size) in pending)
        {
            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = offset,
                StoredLength = size,
                UnpackedLength = size,
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
        var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);
        if (!_lzw) return slice;

        using (slice)
        {
            Span<byte> head = stackalloc byte[8];
            if (slice.Length < 9 || slice.Read(head) != 8)
            {
                slice.Position = 0;
                return new MemoryStream(ReadAll(slice), writable: false);
            }

            if (!head[..4].SequenceEqual("acp\0"u8))
            {
                slice.Position = 0;
                return new MemoryStream(ReadAll(slice), writable: false);
            }

            int unpacked = BinaryPrimitives.ReadInt32BigEndian(head[4..]);
            if (unpacked <= 0 || unpacked > 256 * 1024 * 1024)
            {
                slice.Position = 0;
                return new MemoryStream(ReadAll(slice), writable: false);
            }

            var packed = ReadAll(slice);
            var decoded = Codecs.DecodeFvpLzw(packed.AsSpan(8), unpacked);
            return new MemoryStream(decoded, writable: false);
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        long length = stream.Length;
        if (length <= 0 || length > int.MaxValue) return [];
        var buffer = new byte[(int)length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    public void Dispose() { }
}
