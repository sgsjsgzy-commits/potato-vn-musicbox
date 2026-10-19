using System.Buffers.Binary;

namespace MusicBox.Core.Archives;

/// <summary>
/// Unmasks KiriKiri XP3 payloads whose Ogg streams are XORed by the packer.
///
/// Two schemes occur in practice and both are recoverable without any external
/// key:
/// <list type="bullet">
/// <item><b>Per-page mask.</b> Each page carries its own single-byte key: the
/// first stored byte is "O" masked with that byte, so the key falls out of the
/// page itself (Yuzusoft "cxdec" and relatives).</item>
/// <item><b>NekoWorks mask.</b> A 31-byte key derived from the entry's "adlr"
/// field is repeated over the whole payload. The same rule reproduces both the
/// identification page and every following page.</item>
/// </list>
/// Both variants leave the page-0 header partly protected in some titles; the
/// codec description that follows is enough to rebuild a valid page 0.
/// </summary>
internal static class Xp3OggUnmask
{
    private const int MaxScan = 4096;
    private const int NekoWorksKeyLength = 31;

    /// <summary>
    /// Wraps <paramref name="source"/> when its payload is a masked Ogg stream.
    /// Returns null (leaving the stream at position 0) otherwise.
    /// </summary>
    public static Stream? Wrap(Stream source, uint? adlr)
    {
        if (!source.CanSeek || source.Length < 64) return null;

        try
        {
            source.Position = 0;
            int headLength = (int)Math.Min(MaxScan, source.Length);
            var head = new byte[headLength];
            source.ReadExactly(head);

            if (IsPlainContainer(head))
            {
                source.Position = 0;
                return null;
            }

            if (adlr is uint value)
            {
                var neko = Xp3RepeatingKeyStream.TryCreate(source, DeriveNekoWorksKey(value));
                if (neko is not null) return neko;
            }

            if (TryFindFirstPage(source, head, out int firstPage, out byte firstKey))
            {
                var unmasked = Xp3PageStream.TryCreate(source, firstPage, firstKey);
                if (unmasked is not null) return unmasked;
            }

            source.Position = 0;
            return null;
        }
        catch
        {
            try { source.Position = 0; } catch { /* the stream is unusable anyway */ }
            return null;
        }
    }

    /// <summary>
    /// NekoWorks key schedule: the seed is spread across 31 bytes by XORing its
    /// low byte and rotating it down eight bits at a time.
    /// </summary>
    private static byte[] DeriveNekoWorksKey(uint seed)
    {
        ulong value = (seed & 0x7FFFFFFF) | ((ulong)(seed & 0x7FFFFFFF) << 31);
        var key = new byte[NekoWorksKeyLength];
        for (int i = 0; i < key.Length; i++)
        {
            key[i] = (byte)value;
            value = ((value & 0xFFFFFFFEUL) << 23) | (value >> 8);
        }

        return key;
    }

    private static bool IsPlainContainer(ReadOnlySpan<byte> head) =>
        head.Length >= 4 &&
        (head[..4].SequenceEqual("OggS"u8)
         || head[..4].SequenceEqual("RIFF"u8)
         || head[..4].SequenceEqual("fLaC"u8)
         || head[..4].SequenceEqual("FSB5"u8)
         || head[..3].SequenceEqual("ID3"u8));

    /// <summary>
    /// Finds the first page by structure rather than by checksum: the length
    /// implied by the lacing table must land exactly on another page whose own
    /// key reproduces the Ogg signature. The signature alone matches about once
    /// per 16 MB of random data, but two pages in a row do not.
    ///
    /// Checksums are not used here because the packer leaves some pages with a
    /// stale CRC; decoders ignore it, so requiring it would reject valid audio.
    /// </summary>
    private static bool TryFindFirstPage(Stream source, byte[] head, out int pageOffset, out byte pageKey)
    {
        for (int pos = 0; pos + 27 <= head.Length; pos++)
        {
            byte key = (byte)(head[pos] ^ 0x4F);
            if ((head[pos + 1] ^ key) != 0x67) continue;
            if ((head[pos + 2] ^ key) != 0x67) continue;
            if ((head[pos + 3] ^ key) != 0x53) continue;

            int segments = head[pos + 26] ^ key;
            if (pos + 27 + segments > head.Length) continue;

            long length = OggPageLength(head.AsSpan(pos), key, segments);
            if (length <= 0 || pos + length > source.Length) continue;

            if (!FollowsAnotherPage(source, pos + length)) continue;

            pageOffset = pos;
            pageKey = key;
            return true;
        }

        pageOffset = 0;
        pageKey = 0;
        return false;
    }

    private static long OggPageLength(ReadOnlySpan<byte> header, byte key, int segments)
    {
        if (header.Length < 27 + segments) return -1;

        long body = 0;
        for (int i = 0; i < segments; i++) body += header[27 + i] ^ key;
        return 27L + segments + body;
    }

    /// <summary>
    /// True when <paramref name="offset"/> is either the end of the payload or
    /// the start of another page that carries the Ogg signature under its own
    /// mask byte.
    /// </summary>
    private static bool FollowsAnotherPage(Stream source, long offset)
    {
        if (offset == source.Length) return true;
        if (offset + 27 > source.Length) return false;

        var header = new byte[27];
        source.Position = offset;
        if (source.Read(header, 0, 27) != 27) return false;

        byte key = (byte)(header[0] ^ 0x4F);
        return (header[1] ^ key) == 0x67
               && (header[2] ^ key) == 0x67
               && (header[3] ^ key) == 0x53;
    }
}

/// <summary>
/// Stream over a payload masked with one repeating key. Validates the key
/// against the identification page before it is used, so an entry whose "adlr"
/// is an ordinary checksum is left untouched.
/// </summary>
internal sealed class Xp3RepeatingKeyStream : Stream
{
    private readonly Stream _source;
    private readonly byte[] _key;
    private long _position;

    private Xp3RepeatingKeyStream(Stream source, byte[] key)
    {
        _source = source;
        _key = key;
    }

    public static Xp3RepeatingKeyStream? TryCreate(Stream source, byte[] key)
    {
        if (!Validate(source, key)) return null;
        return new Xp3RepeatingKeyStream(source, key);
    }

    /// <summary>
    /// A repeating-key mask must reproduce the identification page's fixed
    /// bytes: magic, version, granule zero, sequence zero and the Vorbis (or
    /// Opus) signature right after the lacing table.
    /// </summary>
    private static bool Validate(Stream source, byte[] key)
    {
        const int sample = 128;
        var head = new byte[sample];
        source.Position = 0;
        int read = source.Read(head, 0, sample);
        source.Position = 0;
        if (read < 64) return false;

        for (int i = 0; i < read; i++) head[i] ^= key[i % key.Length];

        if (!head.AsSpan(0, 4).SequenceEqual("OggS"u8)) return false;
        if (head[4] != 0 || head[5] != 2) return false;

        // Granule position and sequence number of the first page are zero.
        for (int i = 6; i < 14; i++) if (head[i] != 0) return false;
        for (int i = 18; i < 22; i++) if (head[i] != 0) return false;

        int segments = head[26];
        if (segments is <= 0 or > 255) return false;

        int body = 0;
        for (int i = 0; i < segments; i++) body += head[27 + i];
        int packet = 27 + segments;
        if (packet + 7 > read) return false;

        return head.AsSpan(packet, 7).SequenceEqual("\x01vorbis"u8)
               || head.AsSpan(packet, 8).SequenceEqual("OpusHead"u8);
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        _source.Position = _position;
        int read = _source.Read(buffer);
        for (int i = 0; i < read; i++)
            buffer[i] ^= _key[(int)((_position + i) % _key.Length)];

        _position += read;
        return read;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;
    public override long Length => _source.Length;

    public override long Position
    {
        get => _position;
        set
        {
            _position = Math.Clamp(value, 0, Length);
            _source.Position = _position;
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => _position,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>
/// Seekable view over a page-masked Ogg stream. Page boundaries are discovered
/// lazily as playback advances, so a short probe only pays for the pages it
/// actually touches; payload bytes are decrypted on demand rather than copied.
/// </summary>
internal sealed class Xp3PageStream : Stream
{
    private readonly Stream _source;
    private readonly byte[]? _syntheticFirstPage;
    private readonly long _firstPageOffset;
    private readonly List<Page> _pages = [];

    private long _indexedThrough;
    private bool _indexComplete;
    private long _position;

    private readonly record struct Page(long Start, int Length, long RawStart, byte Key, bool Synthetic);

    private Xp3PageStream(Stream source, long firstPageOffset, byte[]? synthetic)
    {
        _source = source;
        _firstPageOffset = firstPageOffset;
        _syntheticFirstPage = synthetic;
        _indexedThrough = firstPageOffset;

        if (synthetic is not null)
            _pages.Add(new Page(0, synthetic.Length, -1, 0, true));
    }

    /// <summary>
    /// Builds the stream, or returns null when the payload does not parse as a
    /// continuous Ogg page sequence (wrong key, truncated file, not Ogg at all).
    /// </summary>
    public static Xp3PageStream? TryCreate(Stream source, long firstPage, byte key)
    {
        byte[]? synthetic = null;

        if (firstPage > 0)
        {
            synthetic = RebuildFirstPage(source, (int)firstPage, key);
            if (synthetic is null) return null;
        }

        var stream = new Xp3PageStream(source, firstPage, synthetic);
        if (!stream.IndexNextPage()) return null;
        if (stream._pages.Count == 0) return null;

        stream._position = 0;
        return stream;
    }

    /// <summary>
    /// Rebuilds page 0 from the bytes the per-entry key did not touch. The
    /// surviving part is the identification packet, which is what the decoder
    /// actually needs; serial number and sequence come from the next page.
    /// </summary>
    private static byte[]? RebuildFirstPage(Stream source, int firstPage, byte key)
    {
        int prefix = firstPage;
        if (prefix < 28) return null;

        var raw = new byte[prefix];
        source.Position = 0;
        if (source.Read(raw, 0, prefix) != prefix) return null;

        int segments = raw[26] ^ key;
        if (segments is <= 0 or >= 255) return null;
        if (27 + segments >= prefix) return null;

        int bodyLength = prefix - 27 - segments;
        var lacing = new byte[segments];
        var body = new byte[bodyLength];
        for (int i = 0; i < segments; i++) lacing[i] = (byte)(raw[27 + i] ^ key);
        for (int i = 0; i < bodyLength; i++) body[i] = (byte)(raw[27 + segments + i] ^ key);

        // The identification packet must be there; otherwise this is not a
        // header page and the reconstruction would be a guess.
        bool identified = body.AsSpan().StartsWith("\x01vorbis"u8)
                          || body.AsSpan().StartsWith("OpusHead"u8);
        if (!identified) return null;

        // Serial belongs to the stream, and the following page carries it.
        var serial = new byte[4];
        source.Position = firstPage + 14;
        if (source.Read(serial, 0, 4) != 4) return null;
        for (int i = 0; i < 4; i++) serial[i] ^= key;

        var page = new byte[27 + segments + bodyLength];
        page[0] = (byte)'O';
        page[1] = (byte)'g';
        page[2] = (byte)'g';
        page[3] = (byte)'S';
        page[4] = 0;      // stream structure version
        page[5] = 2;      // beginning of stream
        // The granule position of an identification header is zero.
        serial.CopyTo(page, 14);
        // The first page always has sequence number zero.
        page[26] = (byte)segments;
        lacing.CopyTo(page, 27);
        body.CopyTo(page, 27 + segments);

        uint crc = OggCrc.Compute(page);
        BinaryPrimitives.WriteUInt32LittleEndian(page.AsSpan(22), crc);
        return page;
    }

    /// <summary>
    /// Reads one page header at <see cref="_indexedThrough"/> and appends it.
    /// Returns false when the stream ends or the page sequence stops parsing.
    /// </summary>
    private bool IndexNextPage()
    {
        if (_indexComplete) return false;
        if (_indexedThrough + 27 > _source.Length)
        {
            _indexComplete = true;
            return false;
        }

        var header = new byte[27 + 255];
        _source.Position = _indexedThrough;
        if (_source.Read(header, 0, 27) != 27)
        {
            _indexComplete = true;
            return false;
        }

        byte key = (byte)(header[0] ^ 0x4F);
        if ((header[1] ^ key) != 0x67 || (header[2] ^ key) != 0x67 || (header[3] ^ key) != 0x53)
        {
            _indexComplete = true;
            return false;
        }

        int segments = header[26] ^ key;
        if (_source.Read(header, 27, segments) != segments)
        {
            _indexComplete = true;
            return false;
        }

        long length = 27L + segments;
        for (int i = 0; i < segments; i++) length += header[27 + i] ^ key;

        if (_indexedThrough + length > _source.Length)
        {
            _indexComplete = true;
            return false;
        }

        _pages.Add(new Page(_indexedThrough, (int)length, _indexedThrough, key, false));
        _indexedThrough += length;
        return true;
    }

    private void EnsureIndexedThrough(long position)
    {
        while (!_indexComplete && _indexedThrough <= position)
        {
            if (!IndexNextPage()) break;
        }
    }

    private int FindPage(long position)
    {
        int low = 0, high = _pages.Count - 1, found = -1;
        while (low <= high)
        {
            int mid = (low + high) / 2;
            if (_pages[mid].Start <= position)
            {
                found = mid;
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return found;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        if (buffer.Length == 0) return 0;

        EnsureIndexedThrough(_position);

        int total = 0;
        while (total < buffer.Length)
        {
            int index = FindPage(_position);
            if (index < 0) break;

            var page = _pages[index];
            int within = (int)(_position - page.Start);
            if (within >= page.Length)
            {
                // At a page boundary; make sure the next page is known before
                // deciding the stream has ended.
                EnsureIndexedThrough(_position);
                index = FindPage(_position);
                if (index < 0 || (int)(_position - _pages[index].Start) >= _pages[index].Length) break;
                page = _pages[index];
                within = (int)(_position - page.Start);
            }

            int take = Math.Min(page.Length - within, buffer.Length - total);
            if (take <= 0) break;

            if (page.Synthetic)
            {
                _syntheticFirstPage.AsSpan(within, take).CopyTo(buffer[total..]);
            }
            else
            {
                _source.Position = page.RawStart + within;
                int read = _source.Read(buffer.Slice(total, take));
                if (read <= 0) break;
                for (int i = 0; i < read; i++) buffer[total + i] ^= page.Key;
                take = read;
            }

            total += take;
            _position += take;
        }

        return total;
    }

    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => false;

    /// <summary>
    /// Walking the whole page table is what a duration probe needs anyway; the
    /// result is cached, so playback does not repeat it.
    /// </summary>
    public override long Length
    {
        get
        {
            EnsureIndexedThrough(long.MaxValue);
            return _indexedThrough;
        }
    }

    public override long Position
    {
        get => _position;
        set
        {
            long clamped = Math.Max(0, value);
            EnsureIndexedThrough(clamped);
            _position = Math.Min(clamped, _indexedThrough);
        }
    }

    public override long Seek(long offset, SeekOrigin origin)
    {
        Position = origin switch
        {
            SeekOrigin.Begin => offset,
            SeekOrigin.Current => _position + offset,
            SeekOrigin.End => Length + offset,
            _ => _position,
        };
        return _position;
    }

    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _source.Dispose();
        base.Dispose(disposing);
    }
}

/// <summary>Ogg page checksum (CRC-32, polynomial 0x04C11DB7, not reflected).</summary>
internal static class OggCrc
{
    private static readonly uint[] Table = BuildTable();

    private static uint[] BuildTable()
    {
        var table = new uint[256];
        for (uint i = 0; i < 256; i++)
        {
            uint value = i << 24;
            for (int bit = 0; bit < 8; bit++)
                value = (value & 0x80000000) != 0 ? (value << 1) ^ 0x04C11DB7 : value << 1;
            table[i] = value;
        }

        return table;
    }

    public static uint Compute(ReadOnlySpan<byte> data)
    {
        uint crc = 0;
        foreach (byte value in data)
            crc = (crc << 8) ^ Table[((crc >> 24) ^ value) & 0xFF];
        return crc;
    }
}
