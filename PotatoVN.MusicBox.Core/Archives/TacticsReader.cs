using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Nexton/Tactics "TACTICS_ARC_FILE" archive.
///
/// Layout: 16-byte magic, then records of
/// <c>[uint32 storedSize][uint32 unpackedSize][uint32 nameLength][name]
/// [payload]</c> with an extra 8 reserved bytes between the name and the
/// payload. Payloads are XOR-masked with a per-title key and then either stored
/// raw or compressed with the engine's LZSS variant.
///
/// The XOR key comes from a short table of known titles; an unknown title still
/// opens, but its entries cannot be decoded.
/// </summary>
public sealed class TacticsReader : IArchiveReader
{
    private static readonly byte[][] KnownKeys =
    [
        Encoding.ASCII.GetBytes("Puni0r4p"),
        Encoding.ASCII.GetBytes("lzV90z46"),
        Encoding.ASCII.GetBytes("qkh873z1"),
        Encoding.ASCII.GetBytes("RuPMqG64"),
    ];

    private readonly List<ArchiveEntry> _entries = [];
    private readonly byte[] _key;

    public string FormatName => "TACTICS";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private TacticsReader(string path, byte[] key)
    {
        ArchivePath = path;
        _key = key;
    }

    public static TacticsReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x20) return null;

            Span<byte> head = stackalloc byte[16];
            if (fs.Read(head) != 16) return null;
            if (!head.SequenceEqual("TACTICS_ARC_FILE"u8)) return null;

            // Pick the key that yields a self-consistent index: a wrong key
            // scrambles the first record's sizes.
            foreach (var key in KnownKeys)
            {
                var reader = new TacticsReader(path, key);
                if (reader.ReadIndex(fs)) return reader;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(FileStream fs)
    {
        var record = new byte[20];
        long pos = 16;

        while (pos + record.Length <= fs.Length)
        {
            fs.Position = pos;
            if (fs.Read(record, 0, record.Length) != record.Length) break;

            uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(record);
            uint unpackedSize = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(4));
            uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8));

            if (nameLength > 0x400) return false;

            long namePos = pos + 20;
            long dataPos = namePos + nameLength + 8;
            if (nameLength > 0 && dataPos + storedSize > fs.Length) return false;

            if (nameLength > 0)
            {
                var nameBuffer = new byte[nameLength];
                fs.Position = namePos;
                fs.ReadExactly(nameBuffer);
                string name = DecodeName(nameBuffer);
                if (string.IsNullOrWhiteSpace(name)) return false;

                _entries.Add(new ArchiveEntry
                {
                    ArchivePath = ArchivePath,
                    EntryPath = name.Replace('\\', '/'),
                    Offset = dataPos,
                    StoredLength = storedSize,
                    UnpackedLength = unpackedSize > 0 ? unpackedSize : storedSize,
                    IsCompressed = unpackedSize > 0 && unpackedSize != storedSize,
                });
            }

            pos = dataPos + storedSize;
        }

        return _entries.Count > 0;
    }

    private string DecodeName(byte[] raw)
    {
        // Names are XOR-masked with the same key as the payload.
        for (int i = 0; i < raw.Length; i++) raw[i] ^= _key[i % _key.Length];

        int length = Array.IndexOf(raw, (byte)0);
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

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        using var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);
        var payload = new byte[entry.StoredLength];
        slice.ReadExactly(payload);

        for (int i = 0; i < payload.Length; i++) payload[i] ^= _key[i % _key.Length];

        if (!entry.IsCompressed) return new MemoryStream(payload, writable: false);

        var decoded = Codecs.DecodeTacticsLzss(payload);
        return new MemoryStream(decoded, writable: false);
    }

    public void Dispose() { }
}
