using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// YuRis YPF archive. Directory records are 0x17 + name bytes:
/// uint32 name hash, byte obfuscated name length, name bytes (XOR key),
/// byte file type, byte packed flag, uint32 unpacked size, uint32 packed size,
/// uint32 offset, uint32 checksum.
///
/// The name XOR key is not stored; it is recovered by assuming the file name
/// ends in ".xxx" (the third-from-last name byte XOR '.' yields the key), which
/// holds for every known game using this format.
/// </summary>
public sealed class YpfReader : IArchiveReader
{
    private static readonly byte[] SwapTable00 =
    [
        0x03, 0x48, 0x06, 0x35,
        0x0C, 0x10, 0x11, 0x19, 0x1C, 0x1E,
        0x09, 0x0B, 0x0D, 0x13, 0x15, 0x1B, 0x20, 0x23, 0x26, 0x29, 0x2C, 0x2F, 0x2E, 0x32,
    ];

    private static readonly byte[] SwapTable04 =
    [
        0x0C, 0x10, 0x11, 0x19, 0x1C, 0x1E,
        0x09, 0x0B, 0x0D, 0x13, 0x15, 0x1B, 0x20, 0x23, 0x26, 0x29, 0x2C, 0x2F, 0x2E, 0x32,
    ];

    private static readonly byte[] SwapTable10 =
    [
        0x09, 0x0B, 0x0D, 0x13, 0x15, 0x1B, 0x20, 0x23, 0x26, 0x29, 0x2C, 0x2F, 0x2E, 0x32,
    ];

    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "YPF";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private YpfReader(string path) => ArchivePath = path;

    public static YpfReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x20) return null;

            Span<byte> head = stackalloc byte[16];
            if (fs.Read(head) != 16) return null;
            if (head[0] != 'Y' || head[1] != 'P' || head[2] != 'F' || head[3] != 0) return null;

            uint version = BinaryPrimitives.ReadUInt32LittleEndian(head[4..]);
            int count = BinaryPrimitives.ReadInt32LittleEndian(head[8..]);
            uint dirSize = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]);
            if (count is <= 0 or > 0x100000) return null;
            if (dirSize < count * 0x17) return null;

            var reader = new YpfReader(path);
            return reader.ReadIndex(fs, version, count, dirSize) ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(FileStream fs, uint version, int count, uint dirSize)
    {
        byte[] swap = version < 0x100 ? SwapTable04
            : version is >= 0x12C and < 0x196 ? SwapTable10
            : SwapTable00;

        // Newer engines append extra bytes after the standard 0x12-byte tail
        // (a checksum), so the record stride grows accordingly.
        int extraHeader = version >= 0x1D9 ? 4 : version == 0xDE ? 8 : 0;
        int recordTail = 0x12 + extraHeader;

        long dirOffset = 0x20;
        long dirEnd = dirOffset + dirSize;
        if (dirEnd > fs.Length) return false;

        var dir = new byte[dirSize];
        fs.Position = dirOffset;
        fs.ReadExactly(dir);

        int pos = 0;
        byte? key = null;

        // The name-length byte is obfuscated through a swap table whose index
        // range is small (max 0x31). Lengths outside it are already plain.
        for (int i = 0; i < count; i++)
        {
            if (pos + 5 + recordTail > dir.Length) break;

            uint nameHash = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos));
            int rawLength = dir[pos + 4] ^ 0xFF;
            int nameLength = DecryptLength(swap, rawLength);
            pos += 5;

            if (nameLength <= 0 || pos + nameLength + recordTail > dir.Length) break;

            var nameBytes = dir.AsSpan(pos, nameLength).ToArray();
            pos += nameLength;

            if (key is null)
            {
                if (nameLength < 4) return false;
                key = (byte)(nameBytes[^4] ^ (byte)'.');
            }

            for (int j = 0; j < nameBytes.Length; j++) nameBytes[j] ^= key.Value;
            string name = DecodeCp932(nameBytes);

            byte fileType = dir[pos];
            bool packed = dir[pos + 1] != 0;
            uint unpackedSize = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 2));
            uint packedSize = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 6));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(dir.AsSpan(pos + 10));
            pos += recordTail;

            if (offset + packedSize > fs.Length || string.IsNullOrWhiteSpace(name)) continue;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = offset,
                StoredLength = packedSize,
                UnpackedLength = unpackedSize,
                IsCompressed = packed,
                Tag = fileType,
            });
        }

        return _entries.Count > 0;
    }

    /// <summary>
    /// The obfuscated length byte maps to the *neighbouring* table entry:
    /// odd index → previous entry, even index → next entry. Bytes outside the
    /// table are already plain.
    /// </summary>
    private static int DecryptLength(byte[] swap, int value)
    {
        int index = Array.IndexOf(swap, (byte)value);
        if (index < 0) return value;

        int neighbour = (index & 1) != 0 ? index - 1 : index + 1;
        return neighbour < swap.Length ? swap[neighbour] : value;
    }

    private static string DecodeCp932(byte[] bytes)
    {
        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(bytes);
        }
        catch
        {
            return Encoding.UTF8.GetString(bytes);
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);
        if (!entry.IsCompressed) return slice;

        using (slice)
        {
            using var zlib = new ZLibStream(slice, CompressionMode.Decompress);
            var output = new MemoryStream((int)Math.Min(entry.UnpackedLength, 128L * 1024 * 1024));
            zlib.CopyTo(output);
            output.Position = 0;
            return output;
        }
    }

    public void Dispose() { }
}
