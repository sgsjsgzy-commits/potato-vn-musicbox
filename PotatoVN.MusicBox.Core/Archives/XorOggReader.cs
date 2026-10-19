using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Ogg Vorbis files whose whole payload is XORed with a single byte (".owp",
/// used by the あねもい engine). Decoding happens while streaming, so a 15 MB
/// track is never copied to disk.
/// </summary>
public sealed class XorOggReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];

    public string FormatName => "OWP";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private XorOggReader(string path) => ArchivePath = path;

    /// <summary>
    /// Probes the header for "OggS" after XOR with a candidate key constant.
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

    public Stream OpenEntryStream(ArchiveEntry entry) =>
        new XorStream(new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength),
            entry.Tag is byte key ? key : (byte)0);

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
