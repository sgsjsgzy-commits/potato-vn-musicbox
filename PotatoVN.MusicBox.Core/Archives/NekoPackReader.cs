using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// NekoSDK "NEKOPACK4A" resource archive.
///
/// Layout: 9-byte magic "NEKOPACK4", one version byte ('A' or 'S'), a uint32
/// index size, then records of
/// <c>[uint32 nameLength][name][uint32 offset ^ key][uint32 size ^ key]</c>
/// where <c>key</c> is the signed-byte sum of the name. Payloads are zlib
/// streams whose first four bytes are XOR-masked with a key derived from the
/// stored size; the last four bytes are the unpacked size trailer.
/// </summary>
public sealed class NekoPackReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "NEKOPACK";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private NekoPackReader(string path) => ArchivePath = path;

    public static NekoPackReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x20) return null;

            Span<byte> head = stackalloc byte[0x10];
            if (fs.Read(head) != head.Length) return null;
            if (!head[..9].SequenceEqual("NEKOPACK4"u8)) return null;

            char version = (char)head[9];
            uint indexSize = BinaryPrimitives.ReadUInt32LittleEndian(head[10..]);
            if (indexSize == 0 || indexSize > 64 * 1024 * 1024) return null;

            long indexStart = version == 'S' ? 0x0A : 0x0E;
            long indexEnd = indexStart + indexSize + (version == 'S' ? 12 : 0);
            if (indexEnd > fs.Length) return null;

            var reader = new NekoPackReader(path);
            return reader.ReadIndex(fs, indexStart, indexEnd) ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(FileStream fs, long indexStart, long indexEnd)
    {
        long pos = indexStart;
        var header = new byte[12];
        var nameBuffer = new byte[0x100];

        while (pos + 4 <= indexEnd)
        {
            fs.Position = pos;
            if (fs.Read(header, 0, 4) != 4) break;

            uint nameLength = BinaryPrimitives.ReadUInt32LittleEndian(header);
            pos += 4;
            if (nameLength == 0) break;
            if (nameLength > nameBuffer.Length || pos + nameLength + 8 > fs.Length) return false;

            fs.ReadExactly(nameBuffer, 0, (int)nameLength);
            pos += nameLength;

            // The obfuscation key is the signed-byte sum of the name.
            int key = 0;
            for (int i = 0; i < nameLength; i++) key += (sbyte)nameBuffer[i];

            fs.ReadExactly(header, 0, 8);
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(header) ^ (uint)key;
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(header.AsSpan(4)) ^ (uint)key;
            pos += 8;

            if (offset < 0x0E || offset + (long)size > fs.Length || size < 4) return false;

            string name = ReadCString(nameBuffer.AsSpan(0, (int)nameLength));
            if (string.IsNullOrWhiteSpace(name)) continue;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = offset,
                StoredLength = size,
                UnpackedLength = size, // trailer is read when the entry opens
                IsCompressed = true,
                Tag = "NEKOPACK",
            });
        }

        return _entries.Count > 0;
    }

    private static string ReadCString(ReadOnlySpan<byte> raw)
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
            return Encoding.UTF8.GetString(raw[..length]);
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        long length = entry.StoredLength;
        if (length <= 0 || length > 512L * 1024 * 1024)
            throw new InvalidDataException("NEKOPACK payload is too large.");

        using var slice = new FileSliceStream(ArchivePath, entry.Offset, length);
        var stored = new byte[(int)length];
        slice.ReadExactly(stored);

        UnmaskHeader(stored.AsSpan(0, Math.Min(4, stored.Length)), length);

        if (stored.Length <= 8)
            return new MemoryStream(stored.AsSpan(0, Math.Min(4, stored.Length)).ToArray(), writable: false);

        // Trailer: unpacked size. Everything between the 4-byte header and the
        // trailer is one zlib stream.
        int unpacked = BinaryPrimitives.ReadInt32LittleEndian(stored.AsSpan(stored.Length - 4));
        int bodyLength = stored.Length - 8;
        var body = new byte[bodyLength];
        Array.Copy(stored, 0, body, 0, 4);
        Array.Copy(stored, 4, body, 4, bodyLength - 4);

        try
        {
            var decoded = Codecs.Inflate(body);
            return new MemoryStream(decoded, writable: false);
        }
        catch
        {
            return new MemoryStream(stored, writable: false);
        }
    }

    private static void UnmaskHeader(Span<byte> data, long storedSize)
    {
        uint key = (uint)(storedSize / 8 + 0x22);
        for (int i = 0; i < data.Length; i++)
        {
            data[i] ^= (byte)key;
            key <<= 3;
        }
    }

    public void Dispose() { }
}
