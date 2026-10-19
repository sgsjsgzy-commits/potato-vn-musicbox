using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Entis GLS "ERISA-Archive" (.noa) used by Entis titles.
///
/// Layout: "Entis\x1A", file-kind 0x02000400 at 8, a "DirEntry" section at 0x40
/// whose length sits at +8 and whose body starts at +0x10. The body holds a
/// uint32 record count followed by 0x24-byte-or-longer records:
///
/// <code>
/// uint64 size; uint32 attribute; uint32 encoding; int64 relativeOffset;
/// uint64 time; uint32 extraLength; [extra]; uint32 nameLength; name (CP932)
/// </code>
///
/// Attribute 0x10 recurses into a sub-directory at relativeOffset+0x10, 0x20
/// and 0x40 terminate the section, anything else is a file whose payload starts
/// at relativeOffset+0x10 and whose real length is the uint64 at that header's
/// +8.
///
/// Audio is stored as "MIO" (the engine's own codec) and is not decoded here;
/// such entries are still listed so the UI can show what the archive holds.
/// </summary>
public sealed class EntisNoaReader : IArchiveReader
{
    private const uint DirectoryAttribute = 0x10;
    private const uint TerminatorA = 0x20;
    private const uint TerminatorB = 0x40;
    private const int RecordFixedSize = 0x24;

    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "NOA";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private EntisNoaReader(string path) => ArchivePath = path;

    public static EntisNoaReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x60) return null;

            Span<byte> head = stackalloc byte[0x50];
            fs.ReadExactly(head);
            if (!head[..5].SequenceEqual("Entis"u8) && !head[..4].SequenceEqual("VIST"u8)) return null;
            if (BinaryPrimitives.ReadUInt32LittleEndian(head[8..]) != 0x02000400) return null;
            if (!head[0x40..0x48].SequenceEqual("DirEntry"u8)) return null;

            var reader = new EntisNoaReader(path);
            return reader.WalkDirectory(fs, 0x40, string.Empty, 0) ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private bool WalkDirectory(FileStream fs, long record, string prefix, int depth)
    {
        if (depth > 16) return false;
        if (record < 0 || record + 0x10 > fs.Length) return false;

        Span<byte> header = stackalloc byte[0x10];
        fs.Position = record;
        fs.ReadExactly(header);
        if (!header[..8].SequenceEqual("DirEntry"u8)) return false;

        long length = BinaryPrimitives.ReadInt64LittleEndian(header[8..]);
        if (length <= 0 || length > 64 * 1024 * 1024) return false;
        if (record + 0x10 + length > fs.Length) return false;

        var body = new byte[length];
        fs.Position = record + 0x10;
        fs.ReadExactly(body);

        if (body.Length < 4) return false;
        int count = BinaryPrimitives.ReadInt32LittleEndian(body);
        if (count < 0 || count > 0x40000) return false;

        int pos = 4;
        for (int i = 0; i < count; i++)
        {
            if (pos + RecordFixedSize + 4 > body.Length) return false;

            ulong recordedSize = BinaryPrimitives.ReadUInt64LittleEndian(body.AsSpan(pos));
            uint attribute = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos + 8));
            uint encoding = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos + 12));
            long relativeOffset = BinaryPrimitives.ReadInt64LittleEndian(body.AsSpan(pos + 16));
            uint extraLength = BinaryPrimitives.ReadUInt32LittleEndian(body.AsSpan(pos + 32));

            pos += RecordFixedSize;
            if (extraLength > body.Length - pos - 4) return false;
            pos += (int)extraLength;

            int nameLength = BinaryPrimitives.ReadInt32LittleEndian(body.AsSpan(pos));
            pos += 4;
            if (nameLength < 0 || nameLength > 1024 || nameLength > body.Length - pos) return false;

            string name = ReadCp932(body.AsSpan(pos, nameLength));
            pos += nameLength;
            if (string.IsNullOrWhiteSpace(name)) return false;

            if (attribute is TerminatorA or TerminatorB) break;

            string path = prefix.Length == 0 ? name : $"{prefix}/{name}";

            if (attribute == DirectoryAttribute)
            {
                if (!WalkDirectory(fs, record + relativeOffset + 0x10, path, depth + 1)) return false;
                continue;
            }

            long entryHeader = record + relativeOffset;
            if (entryHeader < 0 || entryHeader + 0x10 > fs.Length) return false;

            var fileHeader = new byte[0x10];
            fs.Position = entryHeader;
            fs.ReadExactly(fileHeader);
            ulong fileSize = BinaryPrimitives.ReadUInt64LittleEndian(fileHeader[8..]);
            if (fileSize > 512L * 1024 * 1024) return false;

            long payload = entryHeader + 0x10;
            if (payload + (long)fileSize > fs.Length) return false;

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = path.Replace('\\', '/'),
                Offset = payload,
                StoredLength = (long)fileSize,
                UnpackedLength = (long)fileSize,
                IsEncrypted = encoding != 0,
                Tag = encoding,
            });
        }

        return true;
    }

    private static string ReadCp932(ReadOnlySpan<byte> raw)
    {
        int length = raw.IndexOf((byte)0);
        if (length >= 0) raw = raw[..length];
        if (raw.Length == 0) return string.Empty;

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

    public Stream OpenEntryStream(ArchiveEntry entry) =>
        new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);

    public void Dispose() { }
}
