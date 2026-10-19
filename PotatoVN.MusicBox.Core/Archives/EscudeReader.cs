using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Escu:de "ESC-ARC2" archives.
///
/// Layout: 8-byte magic, uint32 seed, then a key-derived obfuscated entry
/// count, a 12-byte-per-entry index XOR-masked with a rotating key, and a name
/// pool. Payloads are XOR-masked with the same generator and may be LZW-coded
/// behind an "acp\0" + big-endian size header.
/// </summary>
public sealed class EscudeReader : IArchiveReader
{
    private const uint KeyXor = 1705808741u;

    private readonly List<ArchiveEntry> _entries = [];
    private uint _seed;

    public string FormatName => "ESC-ARC";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private EscudeReader(string path) => ArchivePath = path;

    private static uint NextKey(ref uint seed)
    {
        uint value = seed ^ KeyXor;
        uint a = (((value >> 1) ^ value) >> 3);
        uint b = (((value << 1) ^ value) << 3);
        seed = a ^ b ^ value;
        return seed;
    }

    public static EscudeReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x20) return null;

            Span<byte> head = stackalloc byte[20];
            if (fs.Read(head) != head.Length) return null;
            if (!head[..8].SequenceEqual("ESC-ARC2"u8)) return null;

            // Header: seed, index count and name-pool length are each masked
            // with the next word of the generator.
            uint state = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]);
            uint count = BinaryPrimitives.ReadUInt32LittleEndian(head[12..]) ^ NextKey(ref state);
            if (count is 0 or > 0x40000) return null;

            uint poolSize = BinaryPrimitives.ReadUInt32LittleEndian(head[16..]) ^ NextKey(ref state);
            if (poolSize == 0 || poolSize > 64 * 1024 * 1024) return null;

            long indexSize = (long)count * 12;
            long namesPos = 20 + indexSize;
            if (namesPos + poolSize > fs.Length) return null;

            var index = new byte[indexSize];
            fs.Position = 20;
            fs.ReadExactly(index);

            var names = new byte[poolSize];
            fs.ReadExactly(names);

            var reader = new EscudeReader(path) { _seed = BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) };
            return reader.ReadIndex(index, names, count, state, fs.Length) ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(byte[] index, byte[] names, uint count, uint state, long fileLength)
    {
        // The index is XOR-masked word by word, continuing the key stream that
        // decoded the header.
        for (int i = 0; i + 4 <= index.Length; i += 4)
        {
            uint value = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i)) ^ NextKey(ref state);
            BinaryPrimitives.WriteUInt32LittleEndian(index.AsSpan(i), value);
        }

        for (int i = 0; i < count; i++)
        {
            int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(i * 12));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 12 + 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 12 + 8));

            if (nameOffset < 0 || nameOffset >= names.Length) return false;
            if (offset + (long)size > fileLength) return false;

            int end = Array.IndexOf(names, (byte)0, nameOffset);
            if (end < 0) end = names.Length;
            string name = ReadCp932(names.AsSpan(nameOffset, end - nameOffset));
            if (string.IsNullOrWhiteSpace(name)) return false;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = offset,
                StoredLength = size,
                UnpackedLength = size,
                IsCompressed = true, // decided per payload when it opens
                Tag = 0u,
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

        // Only the index is masked; payloads are stored as-is (either a plain
        // container or "acp\0"+big-endian size+ LZW data). Applying the index
        // key stream here would corrupt every entry.
        using (slice)
        {
            var data = new byte[entry.StoredLength];
            slice.ReadExactly(data);

            if (data.Length > 8 && data.AsSpan(0, 4).SequenceEqual("acp\0"u8))
            {
                int unpacked = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(4));
                var decoded = Codecs.DecodeFvpLzw(data.AsSpan(8), unpacked);
                return new MemoryStream(decoded, writable: false);
            }

            return new MemoryStream(data, writable: false);
        }
    }

    public void Dispose() { }
}
