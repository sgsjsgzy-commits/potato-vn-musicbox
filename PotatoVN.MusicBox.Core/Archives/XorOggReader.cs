using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// ".owp" containers, used by the あねもい engine. Despite the extension these
/// are Ogg streams masked the same way protected XP3 payloads are: either a
/// single byte over the whole file, or one byte per page. Both are recovered
/// here, and decoding happens while streaming so a 15 MB track is never copied
/// to disk.
/// </summary>
public sealed class XorOggReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "OWP";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private XorOggReader(string path) => ArchivePath = path;

    /// <summary>
    /// Probes the header for "OggS" under a single-byte mask. A file whose
    /// pages each carry a different mask is reported by
    /// <see cref="Xp3OggUnmask"/> instead and is not handled here.
    /// </summary>
    public static XorOggReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x100) return null;

            Span<byte> head = stackalloc byte[16];
            if (fs.Read(head) != head.Length) return null;

            byte key = (byte)(head[0] ^ (byte)'O');
            if ((head[1] ^ key) != 'g' || (head[2] ^ key) != 'g' || (head[3] ^ key) != 'S') return null;

            var reader = new XorOggReader(path);
            reader._entries.Add(new ArchiveEntry
            {
                ArchivePath = path,
                EntryPath = Path.GetFileName(path),
                Offset = 0,
                StoredLength = fs.Length,
                UnpackedLength = fs.Length,
                Tag = key,
            });
            return reader;
        }
        catch
        {
            return null;
        }
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);

        // Files with a per-page mask are not single-byte XORed; hand them to
        // the page unmasker so they still stream correctly.
        var perPage = Xp3OggUnmask.Wrap(slice, null);
        if (perPage is not null) return perPage;

        return new XorStream(slice, entry.Tag is byte key ? key : (byte)0);
    }

    public void Dispose() { }

    private sealed class XorStream(Stream inner, byte key) : Stream
    {
        public override bool CanRead => true;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }

        public override int Read(byte[] buffer, int offset, int count)
        {
            int read = inner.Read(buffer, offset, count);
            for (int i = 0; i < read; i++) buffer[offset + i] ^= key;
            return read;
        }

        public override int Read(Span<byte> buffer)
        {
            int read = inner.Read(buffer);
            for (int i = 0; i < read; i++) buffer[i] ^= key;
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
}
