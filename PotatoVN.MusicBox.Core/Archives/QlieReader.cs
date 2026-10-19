using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// QLIE engine "FilePackVer3.0" archives (GameData/*.pack).
///
/// Layout: payloads, an index, a 0x400-byte key area, then a 28-byte footer
/// holding the marker, entry count and index offset.
///
/// Each index record is
/// <c>[uint16 nameLength][name XOR nameKey][uint64 offset][uint32 storedSize]
/// [uint32 unpackedSize][uint32 packed][uint32 encryption][uint32 checksum]</c>.
/// The name key is derived from a 256-byte checksum block that sits just before
/// the footer; the member key is derived from the archive key and the name.
///
/// Only the legacy scheme (no external key file) is implemented, which covers
/// archives whose entries use encryption type 0 or 1.
/// </summary>
public sealed class QlieReader : IArchiveReader
{
    private const int FooterSize = 28;
    private const int KeyAreaSize = 0x400;
    private const int RecordTail = 28;

    private readonly List<ArchiveEntry> _entries = [];
    private readonly uint _archiveKey;
    private byte[]? _keyFile;

    public string FormatName => "QLIE";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private QlieReader(string path, uint archiveKey)
    {
        ArchivePath = path;
        _archiveKey = archiveKey;
    }

    public static QlieReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < KeyAreaSize + FooterSize) return null;

            var footer = new byte[FooterSize];
            fs.Position = fs.Length - FooterSize;
            fs.ReadExactly(footer);

            if (!footer.AsSpan(0, 16).SequenceEqual("FilePackVer3.0\0\0"u8)) return null;

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16));
            uint indexOffset = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(20));
            long indexEnd = fs.Length - FooterSize - KeyAreaSize;

            if (count is 0 or > 100_000) return null;
            if (indexOffset >= indexEnd) return null;

            // The footer (28 bytes) follows a 256-byte checksum block, and the
            // index ends before that block: the block starts 0x100 bytes
            // earlier than the footer-relative end.
            var keyBlock = new byte[256];
            fs.Position = fs.Length - FooterSize - KeyAreaSize;
            fs.ReadExactly(keyBlock);
            indexEnd = fs.Length - FooterSize - KeyAreaSize;

            uint archiveKey = HashV3(keyBlock) & 0x0FFFFFFF;

            var reader = new QlieReader(path, archiveKey);
            if (!reader.ReadIndex(fs, count, indexOffset, indexEnd)) return null;

            // Entries encrypted with the newer scheme need the title's key
            // file, which ships next to the executable.
            if (reader._entries.Any(e => e.IsEncrypted))
                reader._keyFile = LoadKeyFile(path);
            return reader;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(FileStream fs, uint count, long indexOffset, long indexEnd)
    {
        fs.Position = indexOffset;
        var header = new byte[2];

        for (uint i = 0; i < count; i++)
        {
            if (fs.Position + header.Length > indexEnd) return false;
            fs.ReadExactly(header);

            int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(header);
            if (nameLength is <= 0 or > 256) return false;
            if (fs.Position + nameLength + RecordTail > indexEnd) return false;

            var rawName = new byte[nameLength];
            fs.ReadExactly(rawName);
            CryptName(rawName, _archiveKey);

            string name = DecodeCp932(rawName);
            if (string.IsNullOrWhiteSpace(name)) return false;

            var record = new byte[RecordTail];
            fs.ReadExactly(record);

            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(record);
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8));
            uint unpacked = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12));
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16));
            uint encryption = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20));

            if (offset + stored > (ulong)indexEnd) return false;
            if (packed > 1 || encryption > 4) return false;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = (long)offset,
                StoredLength = stored,
                UnpackedLength = unpacked > 0 ? unpacked : stored,
                IsCompressed = packed == 1,
                IsEncrypted = encryption != 0,
                Tag = new QlieEntry(rawName, encryption),
            });
        }

        return _entries.Count > 0;
    }

    private static string DecodeCp932(byte[] raw)
    {
        int length = raw.AsSpan().IndexOf((byte)0);
        if (length < 0) length = raw.Length;
        if (length == 0) return string.Empty;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(raw, 0, length);
        }
        catch
        {
            return Encoding.ASCII.GetString(raw, 0, length);
        }
    }

    /// <summary>Name mask: the key mixes the name length and the archive key.</summary>
    private static void CryptName(Span<byte> raw, uint archiveKey)
    {
        uint key = (uint)raw.Length + (archiveKey ^ 0x3E);
        for (int i = 0; i < raw.Length; i++)
        {
            raw[i] ^= (byte)((((uint)(i + 1) ^ key) + (uint)i + 1) & 0xFF);
        }
    }

    /// <summary>QLIE 3.0 checksum over the 256-byte key block.</summary>
    private static uint HashV3(ReadOnlySpan<byte> data)
    {
        ulong h = 0;
        ulong key = 0;

        for (int pos = 0; pos + 8 <= data.Length; pos += 8)
        {
            Add16(ref h, 0x0307030703070307UL);
            Add16(ref key, BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]) ^ h);
        }

        return (uint)(key ^ (key >> 32));
    }

    /// <summary>16-bit lane-wise addition as the engine performs it.</summary>
    private static void Add16(ref ulong accumulator, ulong value)
    {
        // Each 16-bit lane is added modulo 2^16; carry must not leak into the
        // next lane, so every lane is masked at its own width.
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 16)
        {
            ulong lane = ((accumulator >> shift) & 0xFFFF) + ((value >> shift) & 0xFFFF);
            result |= (lane & 0xFFFF) << shift;
        }

        accumulator = result;
    }

    /// <summary>
    /// Finds the title's key file ("DLL/key.fkey" or a copy beside the
    /// archive). Its first entry is itself the key material.
    /// </summary>
    private static byte[]? LoadKeyFile(string archivePath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(archivePath);
            if (directory is null) return null;

            string[] candidates =
            [
                Path.Combine(directory, "DLL", "key.fkey"),
                Path.Combine(directory, "key.fkey"),
                Path.Combine(Path.GetDirectoryName(directory) ?? directory, "DLL", "key.fkey"),
            ];

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);
        if (!entry.IsEncrypted) return Unpack(slice, entry);

        using (slice)
        {
            if (entry.Tag is not QlieEntry meta)
                throw new InvalidDataException("QLIE entry is missing its index record.");

            var data = new byte[entry.StoredLength];
            slice.ReadExactly(data);

            if (meta.Encryption == 1)
            {
                DecryptLegacy(data, _archiveKey);
            }
            else if (_keyFile is { Length: >= 256 } keyFile)
            {
                DecryptKeyed(data, meta.RawName, _archiveKey, keyFile);
            }
            else
            {
                return new MemoryStream(data, writable: false);
            }

            return Unpack(new MemoryStream(data, writable: false), entry);
        }
    }

    private static Stream Unpack(Stream payload, ArchiveEntry entry)
    {
        if (!entry.IsCompressed) return payload;

        using (payload)
        {
            var decoded = Codecs.Inflate(ReadAll(payload));
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

    /// <summary>
    /// Legacy member cipher: a 64-bit rolling hash XORed over complete qwords.
    /// </summary>
    private static void DecryptLegacy(Span<byte> data, uint archiveKey)
    {
        ulong h = 0xA73C5F9DA73C5F9DUL;
        ulong x = (uint)((data.Length + archiveKey) ^ 0xFEC9753E);
        x |= x << 32;

        for (int pos = 0; pos + 8 <= data.Length; pos += 8)
        {
            // The rolling constant is added lane-wise in 32-bit halves.
            h = ((h + 0xCE24F523CE24F523UL) & 0xFFFFFFFFFFFFFFFFUL) ^ x;
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]);
            BinaryPrimitives.WriteUInt64LittleEndian(data[pos..], word ^ h);
        }
    }

    /// <summary>
    /// Key-file cipher: a 64-bit MT stream drives a rotating 16-entry table.
    /// The seed mixes the name bytes, the archive key and the payload length.
    /// </summary>
    private static void DecryptKeyed(Span<byte> data, byte[] name, uint archiveKey, byte[] keyFile)
    {
        uint h = 0x85F532;
        uint seed = 0x33F641;
        for (int i = 0; i < name.Length; i++)
        {
            h = h + (uint)(i & 0xFF) * name[i];
            seed ^= h;
        }

        int length = data.Length;
        uint mixed = (uint)((7 * (length & 0xFFFFFF) + length + h + (h ^ (uint)length ^ 0x8F32DC)) & 0xFFFFFFFF);
        seed = seed + (archiveKey ^ mixed);
        seed = 9 * (seed & 0x00FFFFFF);

        var random = new QlieTwister(seed, keyFile);
        var table = new ulong[16];
        for (int i = 0; i < table.Length; i++) table[i] = random.Next64();
        for (int i = 0; i < 9; i++) random.Next();

        ulong running = random.Next64();
        int slot = (int)(random.Next() & 15);

        for (int pos = 0; pos + 8 <= data.Length; pos += 8)
        {
            running = AddLanes(running ^ table[slot], table[slot], 32);
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]);
            ulong plain = word ^ running;
            BinaryPrimitives.WriteUInt64LittleEndian(data[pos..], plain);
            running = AddLanes(running, plain, 8) ^ plain;
            running = AddLanes((running << 1) & 0xFFFFFFFEFFFFFFFEUL, plain, 16);
            slot = (slot + 1) & 15;
        }
    }

    /// <summary>Lane-wise addition: the engine adds independent bit-fields.</summary>
    private static ulong AddLanes(ulong a, ulong b, int bits)
    {
        ulong mask = (1UL << bits) - 1;
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += bits)
        {
            result |= ((((a >> shift) + (b >> shift)) & mask) << shift);
        }

        return result;
    }

    /// <summary>QLIE's 64-word MT variant, distinct from standard MT19937.</summary>
    private sealed class QlieTwister
    {
        private readonly uint[] _state = new uint[64];
        private int _pos = 64;

        public QlieTwister(uint seed, byte[] keyFile)
        {
            _state[0] = seed;
            for (int i = 1; i < 64; i++)
            {
                uint prev = _state[i - 1];
                _state[i] = unchecked(0x6611BC19u * (prev ^ (prev >> 30)) + (uint)i);
            }

            int words = Math.Min(keyFile.Length / 4, 64);
            for (int i = 0; i < words; i++)
            {
                _state[i] ^= BinaryPrimitives.ReadUInt32LittleEndian(keyFile.AsSpan(i * 4));
            }
        }

        public uint Next()
        {
            if (_pos >= 64)
            {
                for (int i = 0; i < 63; i++)
                {
                    uint y = (_state[i] & 0x80000000u) | ((_state[i + 1] & 0x7FFFFFFFu) >> 1);
                    _state[i] = _state[(i + 39) % 64] ^ y
                                ^ ((_state[i + 1] & 1) != 0 ? 0x9908B0DFu : 0u);
                }

                uint z = (_state[63] & 0x80000000u) | ((_state[0] & 0x7FFFFFFFu) >> 1);
                _state[63] = _state[38] ^ z ^ ((_state[62] & 1) != 0 ? 0x9908B0DFu : 0u);
                _pos = 0;
            }

            uint value = _state[_pos++];
            value ^= value >> 11;
            value ^= (value << 7) & 0x9C4F88E3u;
            value ^= (value << 15) & 0xE7F70000u;
            return value ^ (value >> 18);
        }

        public ulong Next64() => Next() | ((ulong)Next() << 32);
    }

    public void Dispose() { }

    /// <summary>Name bytes and cipher selector, kept for per-entry decryption.</summary>
    internal sealed record QlieEntry(byte[] RawName, uint Encryption);
}
