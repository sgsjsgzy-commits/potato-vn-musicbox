using System.Buffers.Binary;
using System.IO.Compression;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// KiriKiri XP3 archive reader.
///
/// Layout: 11-byte magic, int64 index offset at 0x0B (optionally indirected via
/// an 0x80 block). The index is either raw or zlib compressed and consists of
/// tagged records; "File" records carry "info" (flags/sizes/UTF-16 name) and
/// "segm" (28-byte segment descriptors) sections.
///
/// Filename encryption: some engines (Yuzusoft et al.) store transformed UTF-16
/// names and encrypted payloads. We detect that case and mark entries encrypted;
/// names that cannot be decoded are dropped rather than shown as garbage.
/// </summary>
public sealed class Xp3Reader : IArchiveReader
{
    private static readonly byte[] Magic = [0x58, 0x50, 0x33, 0x0D, 0x0A, 0x20, 0x0A, 0x1A, 0x8B, 0x67, 0x01];

    private readonly FileStream _stream;
    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "XP3";
    public string ArchivePath { get; }

    public string? IndexProtectionNote => IsProtectedIndex
        ? "索引被 KrkrZ 保护，文件名无法还原，曲目按序号显示"
        : null;

    /// <summary>
    /// The XP3 encryption flag is advisory: many archives set it while storing
    /// plain containers, and <see cref="OpenEntryStream"/> returns the payload
    /// untouched when no key can be derived. Entries are therefore always
    /// attempted, and the scanner drops the ones whose magic stays unreadable.
    /// </summary>
    public bool CanDecryptPayload => true;
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private Xp3Reader(string path, FileStream stream)
    {
        ArchivePath = path;
        _stream = stream;
    }

    public static Xp3Reader? TryOpen(string path)
    {
        FileStream? fs = null;
        try
        {
            fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x20) return null;

            Span<byte> magic = stackalloc byte[11];
            if (fs.Read(magic) != magic.Length || !magic.SequenceEqual(Magic)) return null;

            var reader = new Xp3Reader(path, fs);
            if (!reader.ReadIndex())
            {
                // A protected index parses structurally but yields no entries;
                // keep the reader so the scanner can explain why.
                if (reader.IsProtectedIndex) return reader;
                return null;
            }

            return reader;
        }
        catch
        {
            fs?.Dispose();
            return null;
        }
    }

    private bool ReadIndex()
    {
        const long baseOffset = 0;
        Span<byte> buf = stackalloc byte[8];

        // Some games embed the archive inside their exe; we only support plain files.
        ReadExactly(buf);
        long dirOffset = BinaryPrimitives.ReadInt64LittleEndian(buf);
        if (dirOffset < 0x13 || dirOffset >= _stream.Length) return false;

        // The 0x80 indirection block is one byte marker + 8 reserved bytes +
        // an 8-byte pointer at +9. The marker is a single byte, not a uint32:
        // the following reserved bytes are usually zero.
        _stream.Position = dirOffset;
        if (_stream.ReadByte() == 0x80)
        {
            _stream.Position = dirOffset + 9;
            ReadExactly(buf);
            dirOffset = baseOffset + BinaryPrimitives.ReadInt64LittleEndian(buf);
            if (dirOffset < 0x13 || dirOffset >= _stream.Length) return false;
        }

        _stream.Position = dirOffset;
        int headerType = _stream.ReadByte();
        byte[] index;

        if (headerType == 0)
        {
            ReadExactly(buf);
            long size = BinaryPrimitives.ReadInt64LittleEndian(buf);
            if (size <= 0 || size > 256 * 1024 * 1024 || dirOffset + 9 + size > _stream.Length) return false;
            index = ReadBytes((int)size);
        }
        else if (headerType == 1)
        {
            ReadExactly(buf);
            long packedSize = BinaryPrimitives.ReadInt64LittleEndian(buf);
            ReadExactly(buf);
            long unpackedSize = BinaryPrimitives.ReadInt64LittleEndian(buf);
            if (packedSize <= 0 || packedSize > 256 * 1024 * 1024 || unpackedSize > 512 * 1024 * 1024) return false;

            byte[] packed = ReadBytes((int)packedSize);
            using var input = new MemoryStream(packed);
            using var zlib = new ZLibStream(input, CompressionMode.Decompress);
            using var output = new MemoryStream((int)Math.Min(unpackedSize, 512 * 1024 * 1024));
            zlib.CopyTo(output);
            index = output.ToArray();
        }
        else
        {
            return false;
        }

        return ParseIndex(index, baseOffset);
    }

    /// <summary>
    /// True when the index opens with an "Hxv4" chunk, KrkrZ's index
    /// protection. The chunk is a 12-byte header followed by a fixed-size blob;
    /// the "File" records that follow are ordinary XP3 records whose names have
    /// been replaced by sequential placeholders. The payloads themselves are
    /// page-masked Ogg streams, which <see cref="Xp3OggUnmask"/> recovers.
    /// </summary>
    public bool IsProtectedIndex { get; private set; }

    private bool ParseIndex(byte[] index, long baseOffset)
    {
        int pos = 0;
        if (index.Length >= 12 && index.AsSpan(0, 4).SequenceEqual("Hxv4"u8))
        {
            IsProtectedIndex = true;
            long headerSize = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(4));
            if (headerSize < 0 || 12 + headerSize > index.Length) return false;
            pos = 12 + (int)headerSize;
        }

        while (pos + 12 <= index.Length)
        {
            uint signature = BinaryPrimitives.ReadUInt32LittleEndian(index.AsSpan(pos));
            long entrySize = BinaryPrimitives.ReadInt64LittleEndian(index.AsSpan(pos + 4));
            pos += 12;
            if (entrySize < 0 || pos + entrySize > index.Length) break;

            var payload = index.AsSpan(pos, (int)entrySize);
            pos += (int)entrySize;

            if (signature != 0x656C6946) continue; // "File"

            var entry = ParseFileRecord(payload, baseOffset, ArchivePath, IsProtectedIndex);
            if (entry is null) continue;

            // Protected indexes replace every name with a single placeholder
            // character (U+5000, U+5001, ...). Those carry no information, so
            // the ordinal is a more useful label; the scanner appends the real
            // extension once it sniffs the payload.
            if (IsProtectedIndex && IsPlaceholderName(entry.EntryPath))
                entry = entry.WithName($"track{_entries.Count + 1:0000}");

            _entries.Add(entry);
        }

        return _entries.Count > 0;
    }

    /// <summary>
    /// True for the one-character placeholders a protected index uses in place
    /// of file names. The packer numbers them from U+5000 upwards, so with a
    /// few hundred entries the sequence runs well past U+50FF; anything in the
    /// CJK unified block counts, since a real single-character file name does
    /// not occur in these archives.
    /// </summary>
    private static bool IsPlaceholderName(string name) =>
        name.Length == 1 && name[0] is >= '一' and <= '鿿';

    private static ArchiveEntry? ParseFileRecord(
        ReadOnlySpan<byte> payload, long baseOffset, string archivePath, bool protectedIndex)
    {
        uint flags = 0;
        long unpackedSize = 0;
        string? name = null;
        uint? adlr = null;
        var segments = new List<Xp3Segment>();

        int pos = 0;
        while (pos + 12 <= payload.Length)
        {
            uint section = BinaryPrimitives.ReadUInt32LittleEndian(payload[pos..]);
            long sectionSize = BinaryPrimitives.ReadInt64LittleEndian(payload[(pos + 4)..]);
            pos += 12;
            if (sectionSize < 0 || pos + sectionSize > payload.Length) break;

            var data = payload.Slice(pos, (int)sectionSize);
            pos += (int)sectionSize;

            if (section == 0x6F666E69 && data.Length >= 22) // "info"
            {
                // Layout: uint32 flags, int64 unpacked size, int64 packed size,
                // uint16 name length (in UTF-16 code units), then the name.
                flags = BinaryPrimitives.ReadUInt32LittleEndian(data);
                unpackedSize = BinaryPrimitives.ReadInt64LittleEndian(data[4..]);
                int nameLength = BinaryPrimitives.ReadUInt16LittleEndian(data[20..]);
                if (nameLength > 0 && 22 + nameLength * 2 <= data.Length)
                    name = Encoding.Unicode.GetString(data.Slice(22, nameLength * 2));
            }
            else if (section == 0x726C6461 && data.Length >= 4) // "adlr"
            {
                // The engine's "adler" field doubles as the first four bytes of
                // the per-entry payload key in protected archives.
                adlr = BinaryPrimitives.ReadUInt32LittleEndian(data);
            }
            else if (section == 0x6D676573 && data.Length >= 28) // "segm"
            {
                for (int i = 0; i + 28 <= data.Length; i += 28)
                {
                    var seg = data.Slice(i, 28);
                    segments.Add(new Xp3Segment(
                        BinaryPrimitives.ReadUInt32LittleEndian(seg) != 0,
                        baseOffset + BinaryPrimitives.ReadInt64LittleEndian(seg[4..]),
                        BinaryPrimitives.ReadInt64LittleEndian(seg[12..]),
                        BinaryPrimitives.ReadInt64LittleEndian(seg[20..])));
                }
            }
        }

        if (name is null || segments.Count == 0) return null;

        // Protected indexes name their entries with a single placeholder
        // character, which the general name check would reject.
        bool placeholder = protectedIndex && IsPlaceholderName(name);
        if (!placeholder && !IsUsableName(name)) return null;

        // Size accounting: prefer the info section, fall back to the segments.
        long stored = segments.Sum(s => s.IsCompressed ? s.PackedSize : s.Size);
        long unpacked = unpackedSize > 0 ? unpackedSize : segments.Sum(s => s.Size);

        return new ArchiveEntry
        {
            ArchivePath = archivePath,
            EntryPath = name,
            Offset = segments[0].Offset,
            StoredLength = stored,
            UnpackedLength = unpacked,
            IsCompressed = segments.Any(s => s.IsCompressed),
            IsEncrypted = (flags & 0x80000000u) != 0,
            Tag = new Xp3Entry(segments, adlr),
        };
    }

    /// <summary>
    /// Encrypted XP3 indexes yield control characters / lone NULs. Rather than
    /// exposing unreadable names we skip those entries entirely.
    /// </summary>
    private static bool IsUsableName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        if (name.Contains('\0')) return false;

        foreach (char c in name)
        {
            if (char.IsControl(c)) return false;
        }

        return name.Length > 1;
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        if (entry.Tag is not Xp3Entry record || record.Segments.Count == 0)
            throw new InvalidDataException("XP3 entry has no segments.");

        var segments = record.Segments;

        // Single uncompressed segment: stream directly to avoid buffering large BGM.
        if (segments.Count == 1 && !segments[0].IsCompressed)
        {
            var seg = segments[0];
            var slice = new FileSliceStream(ArchivePath, seg.Offset, seg.Size);

            // Most protected KiriKiri archives (Yuzusoft "cxdec" and its
            // relatives) mask the payload one Ogg page at a time. That variant
            // is fully recoverable, including entries whose index does not even
            // set the encryption flag, so it is tried first.
            var unmasked = Xp3OggUnmask.Wrap(slice, record.Adlr);
            if (unmasked is not null) return unmasked;

            // Some archives set the encryption flag but still store plain
            // containers, so the flag alone is not trustworthy. A repeating
            // mask is only applied when it reproduces the whole identification
            // page; position-dependent ciphers are left untouched and reported
            // by the scanner instead of yielding noise.
            if (!entry.IsEncrypted)
                return slice;

            var key = Xp3Cipher.DeriveKey(ArchivePath, seg.Offset, seg.Size, record.Adlr);
            if (key is null) return slice;

            return new XorStream(slice, key);
        }

        var output = new MemoryStream((int)Math.Min(entry.UnpackedLength, 128L * 1024 * 1024));
        foreach (var seg in segments)
        {
            using var slice = new FileSliceStream(ArchivePath, seg.Offset, seg.IsCompressed ? seg.PackedSize : seg.Size);
            if (seg.IsCompressed)
            {
                using var zlib = new ZLibStream(slice, CompressionMode.Decompress, leaveOpen: true);
                zlib.CopyTo(output);
            }
            else
            {
                slice.CopyTo(output);
            }
        }

        output.Position = 0;
        return output;
    }

    private void ReadExactly(Span<byte> buffer)
    {
        if (_stream.Read(buffer) != buffer.Length) throw new EndOfStreamException();
    }

    private byte[] ReadBytes(int count)
    {
        var buffer = new byte[count];
        _stream.ReadExactly(buffer);
        return buffer;
    }

    public void Dispose() => _stream.Dispose();

    internal readonly record struct Xp3Segment(bool IsCompressed, long Offset, long Size, long PackedSize);

    /// <summary>Everything the reader needs to open one entry.</summary>
    internal sealed record Xp3Entry(List<Xp3Segment> Segments, uint? Adlr);
}

/// <summary>
/// Read-only window over a region of a file. Each instance opens its own handle
/// so several tracks can be streamed concurrently by the player.
/// </summary>
public sealed class FileSliceStream : Stream
{
    private readonly FileStream _stream;
    private readonly long _start;
    private readonly long _length;
    private long _position;

    public FileSliceStream(string path, long offset, long length)
    {
        _stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        _start = offset;
        _length = Math.Min(length, _stream.Length - offset);
        _stream.Position = offset;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _length;

    public override long Position
    {
        get => _position;
        set => _position = Math.Clamp(value, 0, _length);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        long remaining = _length - _position;
        if (remaining <= 0) return 0;

        int toRead = (int)Math.Min(buffer.Length, remaining);
        _stream.Position = _start + _position;
        int read = _stream.Read(buffer[..toRead]);
        _position += read;
        return read;
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => _length + offset,
            _ => _position,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _stream.Dispose();
        base.Dispose(disposing);
    }
}
