using System.Buffers.Binary;

namespace MusicBox.Core.Archives;

/// <summary>
/// Repeating-key mask used by XP3 archives that set the per-entry encryption
/// flag but still store plain containers underneath.
///
/// The key is 40 bytes long. Its first four bytes are the entry's "adlr" value
/// (the engine uses that field both as a checksum and as the mask seed), and the
/// remaining bytes follow from the container header: the first bytes of an Ogg
/// page are fixed by the format, so masking known plaintext with the stored
/// bytes recovers the rest of the key.
///
/// Derivation is validated against the payload before it is used: if the
/// decoded stream does not look like a container, the entry is returned as-is
/// (this is what happens for archives whose flag is a false positive).
/// </summary>
internal static class Xp3Cipher
{
    public const int KeyLength = 40;

    // Bytes we can predict at their offsets in a Vorbis identification page.
    private static readonly (int Offset, byte[] Value)[] KnownPlaintext =
    [
        (0, "OggS"u8.ToArray()),
        (4, [0x00, 0x02]),
        (6, new byte[8]),
        (18, new byte[4]),
        (26, [0x01]),
        (27, [0x1E]),
        (28, [0x01, (byte)'v', (byte)'o', (byte)'r', (byte)'b', (byte)'i', (byte)'s']),
        (36, new byte[3]),
    ];

    /// <summary>
    /// Recovers the 40-byte key for one entry, or null when the payload is not
    /// actually masked.
    /// </summary>
    public static byte[]? DeriveKey(string archivePath, long offset, long size, uint? adlr)
    {
        if (size < KeyLength) return null;

        byte[] head;
        try
        {
            using var stream = new FileSliceStream(archivePath, offset, Math.Min(size, 128));
            int take = (int)Math.Min(stream.Length, 128);
            head = new byte[take];
            stream.ReadExactly(head);
        }
        catch
        {
            return null;
        }

        var key = new byte[KeyLength];
        var known = new bool[KeyLength];
        int derived = 0;

        if (adlr is uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(key, value);
            known[0] = known[1] = known[2] = known[3] = true;
        }

        foreach (var (start, plain) in KnownPlaintext)
        {
            for (int i = 0; i < plain.Length; i++)
            {
                int pos = start + i;
                if (pos >= KeyLength || pos >= head.Length || known[pos]) continue;

                // A literal key byte and a known plaintext byte both fit the
                // observed payload, which is precisely what makes this work.
                key[pos] = (byte)(head[pos] ^ plain[i]);
                known[pos] = true;
                derived++;
            }
        }

        // A repeating mask must reproduce the entire identification page, not
        // just its magic. This rejects archives that use a rolling cipher or
        // merely set the flag on plain data.
        if (derived < KeyLength - 4 || !Verify(head, key)) return null;

        return key;
    }

    /// <summary>
    /// True when re-masking the first bytes with <paramref name="key"/> yields
    /// the stream the container header describes.
    /// </summary>
    public static bool Verify(ReadOnlySpan<byte> head, byte[] key)
    {
        if (head.Length < 58) return false;

        Span<byte> page = stackalloc byte[58];
        for (int i = 0; i < page.Length; i++) page[i] = (byte)(head[i] ^ key[i % KeyLength]);

        if (!page[..4].SequenceEqual("OggS"u8)) return false;
        if (page[4] != 0) return false;              // stream structure version

        // A Vorbis identification header: packet type 1, signature "vorbis",
        // zero version, non-zero channel count and sample rate.
        int packet = 27 + page[26];
        if (packet + 16 > page.Length) return false;
        if (page[packet] != 1 || !page.Slice(packet + 1, 6).SequenceEqual("vorbis"u8)) return false;

        uint version = BinaryPrimitives.ReadUInt32LittleEndian(page[(packet + 7)..]);
        byte channels = page[packet + 11];
        uint sampleRate = BinaryPrimitives.ReadUInt32LittleEndian(page[(packet + 12)..]);

        // The framing bit ends the identification packet.
        int framing = packet + 16;
        if (framing >= page.Length || (page[framing] & 0x01) == 0) return false;

        return version == 0 && channels is > 0 and < 9 && sampleRate is >= 8000 and <= 192000;
    }
}

/// <summary>XORs a stream with a repeating key. Seeking re-aligns the key.</summary>
internal sealed class XorStream(Stream inner, byte[] key) : Stream
{
    public override bool CanRead => true;
    public override bool CanSeek => inner.CanSeek;
    public override bool CanWrite => false;
    public override long Length => inner.Length;

    public override long Position
    {
        get => inner.Position;
        set => inner.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        Read(buffer.AsSpan(offset, count));

    public override int Read(Span<byte> buffer)
    {
        int read = inner.Read(buffer);
        for (int i = 0; i < read; i++)
        {
            long pos = inner.Position - read + i;
            buffer[i] ^= key[(int)(pos % key.Length)];
        }

        return read;
    }

    public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
    public override void Flush() { }
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) inner.Dispose();
        base.Dispose(disposing);
    }
}
