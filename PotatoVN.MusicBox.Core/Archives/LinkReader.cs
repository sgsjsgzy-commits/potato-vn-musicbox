using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// KaGuYa "LINK" resource archive (versions 3–6), used by several Japanese
/// visual novel engines. v6 stores UTF-16 names and 4-byte size-prefixed
/// records; payloads may be BMR-compressed or encrypted (encryption requires
/// params.dat, which we do not implement — such entries are skipped).
/// </summary>
public sealed class LinkReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName { get; private init; } = "LINK";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private LinkReader(string path)
    {
        ArchivePath = path;
    }

    public static LinkReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x10) return null;

            Span<byte> head = stackalloc byte[8];
            if (fs.Read(head) != 8) return null;
            if (head[0] != 'L' || head[1] != 'I' || head[2] != 'N' || head[3] != 'K') return null;

            int version = head[4] - '0';
            if (version is < 3 or > 6) return null;

            var reader = new LinkReader(path) { FormatName = $"LINK{version}" };
            return reader.ReadIndex(fs, version) ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(FileStream fs, int version)
    {
        if (version < 6)
        {
            // v3/v4 legacy index: 32-bit count, name list, then offset/size table.
            return ReadLegacyIndex(fs, version);
        }

        fs.Position = 7;
        int keyLength = fs.ReadByte();
        if (keyLength < 0 || 8 + keyLength >= fs.Length) return false;
        fs.Position = 8 + keyLength;

        long maxOffset = fs.Length;
        Span<byte> header = stackalloc byte[13];
        Span<byte> nameLength = stackalloc byte[2];

        while (fs.Position + 13 < maxOffset)
        {
            long baseOffset = fs.Position;
            if (fs.Read(header) != header.Length) break;

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header);
            if (size == 0) break;
            if (size < 0x10 || baseOffset + size > maxOffset) return _entries.Count > 0;

            ushort flags = BinaryPrimitives.ReadUInt16LittleEndian(header[4..]);
            bool compressed = (flags & 3) != 0;
            bool encrypted = (flags & 4) != 0;

            if (fs.Read(nameLength) != 2) break;
            int nameBytes = BinaryPrimitives.ReadUInt16LittleEndian(nameLength);
            if (nameBytes <= 0 || nameBytes > 0x800 || fs.Position + nameBytes > maxOffset) return _entries.Count > 0;

            var nameBuffer = new byte[nameBytes];
            fs.ReadExactly(nameBuffer);
            string name = Encoding.Unicode.GetString(nameBuffer);

            long dataOffset = fs.Position;
            long dataSize = size - (dataOffset - baseOffset);
            if (dataSize <= 0) return _entries.Count > 0;

            long unpacked = dataSize;
            if (compressed)
            {
                Span<byte> magic = stackalloc byte[4];
                fs.ReadExactly(magic);
                if (magic.SequenceEqual("BMR"u8))
                {
                    Span<byte> sizeBytes = stackalloc byte[4];
                    fs.ReadExactly(sizeBytes);
                    unpacked = BinaryPrimitives.ReadUInt32LittleEndian(sizeBytes);
                }

                fs.Position = dataOffset;
            }

            if (!encrypted && !string.IsNullOrWhiteSpace(name))
            {
                _entries.Add(new ArchiveEntry
                {
                    ArchivePath = ArchivePath,
                    EntryPath = name.Replace('\\', '/'),
                    Offset = dataOffset,
                    StoredLength = dataSize,
                    UnpackedLength = unpacked,
                    IsCompressed = compressed,
                    IsEncrypted = false,
                    Tag = compressed ? "BMR" : null,
                });
            }

            fs.Position = baseOffset + size;
        }

        return _entries.Count > 0;
    }

    private bool ReadLegacyIndex(FileStream fs, int version)
    {
        fs.Position = 4;
        Span<byte> buf = stackalloc byte[4];
        fs.ReadExactly(buf);
        int count = BinaryPrimitives.ReadInt32LittleEndian(buf);
        if (count is <= 0 or > 0x100000) return false;

        fs.ReadExactly(buf);
        uint namesSize = BinaryPrimitives.ReadUInt32LittleEndian(buf);
        if (namesSize > fs.Length) return false;

        var names = new List<string>(count);
        for (int i = 0; i < count; i++)
        {
            var name = ReadCString(fs);
            if (string.IsNullOrEmpty(name)) return false;
            names.Add(name);
        }

        fs.Position = 12 + namesSize;
        var offsets = new (uint Offset, uint Size)[count];
        Span<byte> pair = stackalloc byte[8];
        for (int i = 0; i < count; i++)
        {
            fs.ReadExactly(pair);
            offsets[i] = (BinaryPrimitives.ReadUInt32LittleEndian(pair),
                          BinaryPrimitives.ReadUInt32LittleEndian(pair[4..]));
        }

        for (int i = 0; i < count; i++)
        {
            var (offset, size) = offsets[i];
            if (offset + size > fs.Length) continue;
            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = names[i].Replace('\\', '/'),
                Offset = offset,
                StoredLength = size,
                UnpackedLength = size,
            });
        }

        return _entries.Count > 0;
    }

    private static string ReadCString(FileStream fs)
    {
        var bytes = new List<byte>(32);
        int b;
        while ((b = fs.ReadByte()) > 0) bytes.Add((byte)b);
        if (b < 0) return string.Empty;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(bytes.ToArray());
        }
        catch
        {
            return Encoding.ASCII.GetString(bytes.ToArray());
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);
        if (!entry.IsCompressed) return slice;

        // BMR is an LZSS variant; decode into memory on demand.
        using (slice)
        {
            var output = BmrDecoder.Decode(slice, (int)entry.UnpackedLength);
            return new MemoryStream(output, writable: false);
        }
    }

    public void Dispose() { }
}

/// <summary>
/// BMR (KaGuYa) LZSS decoder. Adapted from the documented bitstream layout:
/// MSB-first bit reader, literal flag bit, 8-bit literal or 12-bit distance +
/// 4-bit length pairs.
/// </summary>
internal static class BmrDecoder
{
    public static byte[] Decode(Stream input, int expectedSize)
    {
        var reader = new MsbBitReader(input);
        var output = new byte[Math.Max(expectedSize, 0x100)];
        int written = 0;
        int windowPos = 0;
        var window = new byte[0x1000];

        while (written < expectedSize)
        {
            int bit = reader.ReadBit();
            if (bit < 0) break;

            if (bit == 1)
            {
                int literal = reader.ReadBits(8);
                if (literal < 0) break;
                output[written++] = (byte)literal;
                window[windowPos] = (byte)literal;
                windowPos = (windowPos + 1) & 0xFFF;
            }
            else
            {
                int distance = reader.ReadBits(12);
                int length = reader.ReadBits(4);
                if (distance < 0 || length < 0) break;

                for (int i = 0; i <= length + 2; i++)
                {
                    byte value = window[(distance + i) & 0xFFF];
                    if (written < output.Length) output[written++] = value;
                    window[windowPos] = value;
                    windowPos = (windowPos + 1) & 0xFFF;
                    if (written >= expectedSize) break;
                }
            }
        }

        if (written != output.Length)
        {
            var trimmed = new byte[written];
            Array.Copy(output, trimmed, written);
            return trimmed;
        }

        return output;
    }

    private sealed class MsbBitReader(Stream stream)
    {
        private int _current;
        private int _remaining;

        public int ReadBit()
        {
            if (_remaining == 0)
            {
                _current = stream.ReadByte();
                if (_current < 0) return -1;
                _remaining = 8;
            }

            _remaining--;
            return (_current >> _remaining) & 1;
        }

        public int ReadBits(int count)
        {
            int value = 0;
            for (int i = 0; i < count; i++)
            {
                int bit = ReadBit();
                if (bit < 0) return -1;
                value = (value << 1) | bit;
            }

            return value;
        }
    }
}
