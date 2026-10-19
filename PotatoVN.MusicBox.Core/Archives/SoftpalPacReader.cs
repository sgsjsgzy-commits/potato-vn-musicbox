using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Softpal / Amuse Craft "PAC " archives (e.g. 牵绊闪耀的恋之伊吕波).
///
/// Layout: int32 count at 8, index at 0x804, then records of
/// <c>[0x20-byte name][uint32 size][uint32 offset]</c>. The first payload
/// offset must equal the end of the index, which is what tells this format
/// apart from unrelated ".pac" files. Audio is stored as plain Ogg Vorbis.
/// </summary>
public sealed class SoftpalPacReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "PAC";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private SoftpalPacReader(string path) => ArchivePath = path;

    public static SoftpalPacReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x820) return null;

            Span<byte> head = stackalloc byte[12];
            if (fs.Read(head) != head.Length) return null;
            if (!head[..4].SequenceEqual("PAC "u8)) return null;

            int count = BinaryPrimitives.ReadInt32LittleEndian(head[8..]);
            if (count is <= 0 or > 0x100000) return null;

            const int indexOffset = 0x804;
            Span<byte> word = stackalloc byte[4];
            foreach (int nameLength in (int[])[0x20, 0x10])
            {
                long expectedData = indexOffset + (long)count * (nameLength + 8);
                if (expectedData + 8 > fs.Length) continue;

                fs.Position = indexOffset + nameLength + 4;
                fs.ReadExactly(word);
                if (BinaryPrimitives.ReadUInt32LittleEndian(word) != expectedData) continue;

                var reader = new SoftpalPacReader(path);
                return reader.ReadIndex(fs, count, indexOffset, nameLength) ? reader : null;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private bool ReadIndex(FileStream fs, int count, int indexOffset, int nameLength)
    {
        var record = new byte[nameLength + 8];
        fs.Position = indexOffset;

        for (int i = 0; i < count; i++)
        {
            if (fs.Read(record, 0, record.Length) != record.Length) return false;

            string name = ReadCp932(record.AsSpan(0, nameLength));
            if (string.IsNullOrWhiteSpace(name)) return false;

            uint size = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(nameLength));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(nameLength + 4));
            if (offset + (long)size > fs.Length) return false;

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

    public Stream OpenEntryStream(ArchiveEntry entry) =>
        new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);

    public void Dispose() { }
}
