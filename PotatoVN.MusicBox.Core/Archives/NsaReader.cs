using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// NScripter NSA archive. Big-endian directory: int16 count, uint32 index size,
/// then per entry a NUL-terminated CP932 name, byte compression type,
/// uint32 offset, uint32 stored size, uint32 unpacked size.
/// Compression: 0 = none, 1 = SPB, 2 = LZSS, 4 = NBZ (bzip2 after 4-byte header).
/// Audio is usually stored uncompressed, so SPB (images) is not decoded.
/// </summary>
public sealed class NsaReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "NSA";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private NsaReader(string path) => ArchivePath = path;

    public static NsaReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 16) return null;

            var reader = new NsaReader(path);
            return reader.ReadIndex(fs) ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(FileStream fs)
    {
        long start = 0;
        Span<byte> head = stackalloc byte[2];
        fs.ReadExactly(head);

        // Some archives begin with two NUL bytes.
        if (head[0] == 0 && head[1] == 0)
        {
            start = 2;
            if (fs.Length < 8) return false;
        }
        else
        {
            fs.Position = 0;
        }

        fs.Position = start;
        Span<byte> countBytes = stackalloc byte[2];
        fs.ReadExactly(countBytes);
        int count = BinaryPrimitives.ReadUInt16BigEndian(countBytes);
        if (count is <= 0 or > 0x10000) return false;

        Span<byte> sizeBytes = stackalloc byte[4];
        fs.ReadExactly(sizeBytes);
        long baseOffset = start + BinaryPrimitives.ReadUInt32BigEndian(sizeBytes);
        if (baseOffset <= 0 || baseOffset >= fs.Length) return false;

        for (int i = 0; i < count; i++)
        {
            if (fs.Position >= baseOffset) break;

            string name = ReadCString(fs);
            if (string.IsNullOrEmpty(name)) return false;

            Span<byte> record = stackalloc byte[13];
            if (fs.Read(record) != record.Length) break;

            byte compression = record[0];
            uint offset = BinaryPrimitives.ReadUInt32BigEndian(record[1..]);
            uint storedSize = BinaryPrimitives.ReadUInt32BigEndian(record[5..]);
            uint unpackedSize = BinaryPrimitives.ReadUInt32BigEndian(record[9..]);

            long absolute = baseOffset + offset;
            if (absolute + storedSize > fs.Length) continue;

            bool supported = compression is 0 or 4;
            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = absolute,
                StoredLength = storedSize,
                UnpackedLength = unpackedSize,
                IsCompressed = compression == 4,
                Tag = supported ? compression : -1,
            });
        }

        return _entries.Count > 0;
    }

    private static string ReadCString(FileStream fs)
    {
        var bytes = new List<byte>(32);
        int b;
        while ((b = fs.ReadByte()) > 0 && bytes.Count < 256) bytes.Add((byte)b);
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
        // NBZ (bzip2) entries are excluded by the scanner; only raw audio is playable.
        if (entry.IsCompressed)
            throw new NotSupportedException("Compressed NSA entries are not supported.");

        return new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);
    }

    public void Dispose() { }
}
