using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Headerless UTF-16 index archive seen in "新建压缩文件/3564" style drops:
///
/// <code>
/// uint32 entryCount
/// uint32 indexSize
/// (entryCount) x { uint32 storedSize; uint32 dataOffset; UTF-16 name + NUL }
/// payloads at 8 + indexSize + dataOffset
/// </code>
///
/// There is no magic, so the index must end exactly where it claims and every
/// payload must land inside the file before the file is accepted.
/// </summary>
public sealed class SimpleArcReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "ARC";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private SimpleArcReader(string path) => ArchivePath = path;

    public static SimpleArcReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x20) return null;

            Span<byte> header = stackalloc byte[8];
            fs.ReadExactly(header);

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(header);
            uint indexSize = BinaryPrimitives.ReadUInt32LittleEndian(header[4..]);
            if (count is 0 or > 0x10000) return null;
            if (indexSize == 0 || 8L + indexSize >= fs.Length) return null;

            var index = new byte[indexSize];
            fs.Position = 8;
            fs.ReadExactly(index);

            long dataStart = 8 + indexSize;
            int pos = 0;
            var pending = new List<(string Name, long Offset, long Size)>((int)count);

            for (uint i = 0; i < count; i++)
            {
                if (pos + 8 > index.Length) return null;

                uint storedSize = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(pos));
                uint dataOffset = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(pos + 4));
                pos += 8;

                int nameStart = pos;
                while (pos + 1 < index.Length && (index[pos] != 0 || index[pos + 1] != 0)) pos += 2;
                if (pos + 1 >= index.Length) return null;

                string name = Encoding.Unicode.GetString(index, nameStart, pos - nameStart);
                pos += 2;

                if (string.IsNullOrWhiteSpace(name) || name.Length > 260) return null;
                if (dataStart + dataOffset + storedSize > fs.Length) return null;

                pending.Add((name, dataStart + dataOffset, storedSize));
            }

            // The declared index size must describe exactly the records read.
            if (pos != index.Length) return null;

            var reader = new SimpleArcReader(path);
            foreach (var (name, offset, size) in pending)
            {
                reader._entries.Add(new ArchiveEntry
                {
                    ArchivePath = path,
                    EntryPath = name.Replace('\\', '/'),
                    Offset = offset,
                    StoredLength = size,
                    UnpackedLength = size,
                });
            }

            return reader._entries.Count > 0 ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry) =>
        new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);

    public void Dispose() { }
}
