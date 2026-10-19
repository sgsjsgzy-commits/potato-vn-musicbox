using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Favorite View Point v2 archives without any magic (bgm.bin, se.bin,
/// voice.bin, graph_*.bin). Layout:
///
/// <code>
/// int32  count
/// int32  size of the name pool
/// (count) x { uint32 nameOffset; uint32 payloadOffset; uint32 size }
/// char[] name pool, NUL separated, UTF-16LE
/// payloads, back to back
/// </code>
///
/// Detection relies on the index and pool fitting the file exactly and on every
/// payload falling inside it, which random ".bin" files fail.
/// </summary>
public sealed class FavoriteBinReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "FVP-BIN";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private FavoriteBinReader(string path) => ArchivePath = path;

    public static FavoriteBinReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x40) return null;

            Span<byte> header = stackalloc byte[8];
            fs.ReadExactly(header);

            int count = BinaryPrimitives.ReadInt32LittleEndian(header);
            if (count is <= 0 or > 0x10000) return null;

            uint namePoolSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            long indexSize = (long)count * 12;
            long namesBase = 8 + indexSize;
            if (namePoolSize == 0 || namePoolSize > 16 * 1024 * 1024) return null;

            long dataStart = namesBase + namePoolSize;
            if (dataStart >= fs.Length) return null;

            var index = new byte[indexSize];
            fs.ReadExactly(index);

            var names = new byte[namePoolSize];
            fs.ReadExactly(names);

            var reader = new FavoriteBinReader(path);
            if (!reader.ReadEntries(index, names, namesBase, dataStart, fs.Length)) return null;
            return reader;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadEntries(byte[] index, byte[] names, long namesBase, long dataStart, long fileLength)
    {
        int count = index.Length / 12;
        uint previousEnd = 0;

        for (int i = 0; i < count; i++)
        {
            int nameOffset = BinaryPrimitives.ReadInt32LittleEndian(index.AsSpan(i * 12));
            uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 12 + 4));
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(i * 12 + 8));

            if (nameOffset < 0 || nameOffset >= names.Length) return false;
            if (dataOffset < dataStart || dataOffset + (long)size > fileLength) return false;

            // Records are ordered and non-overlapping; the first payload starts
            // right after the name pool.
            if (i == 0 && dataOffset > dataStart + 0x1000) return false;
            if (i > 0 && dataOffset < previousEnd) return false;
            previousEnd = dataOffset + size;

            string name = ReadUtf16Name(names, nameOffset);
            if (string.IsNullOrEmpty(name)) return false;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = dataOffset,
                StoredLength = size,
                UnpackedLength = size,
            });
        }

        return _entries.Count > 0;
    }

    private static string ReadUtf16Name(byte[] names, int offset)
    {
        int end = offset;
        while (end + 1 < names.Length && (names[end] != 0 || names[end + 1] != 0)) end += 2;

        int length = end - offset;
        if (length <= 0) return string.Empty;
        return Encoding.Unicode.GetString(names, offset, length);
    }

    public Stream OpenEntryStream(ArchiveEntry entry) =>
        new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);

    public void Dispose() { }
}
