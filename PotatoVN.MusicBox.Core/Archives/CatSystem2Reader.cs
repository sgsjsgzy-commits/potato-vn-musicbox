using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// CatSystem2 / Frontwing "KIF" archives (.int).
///
/// Unencrypted layout: int32 count at 4, then records of
/// <c>[0x20 or 0x40-byte name][uint32 offset][uint32 size]</c>.
///
/// Encrypted layout: the first slot is the plain-text name "__key__.dat". The
/// record stride is 0x48, the u32 at 0x4C seeds the engine's MT variant whose
/// first output is the 4-byte Blowfish key, offsets/sizes are Blowfish-encrypted
/// (the entry ordinal is added to the stored offset first), filenames are
/// substitution-ciphered with a per-entry MT seed, and member payloads are
/// Blowfish ECB over their leading size/8*8 bytes.
///
/// Music and voice payloads inside are plain Ogg Vorbis, so they are playable
/// even without recovering the filename passphrase; unrecovered names fall back
/// to a stable ordinal label.
/// </summary>
public sealed class CatSystem2Reader : IArchiveReader
{
    private const int EncryptedRecordSize = 0x48;
    private const int NameFieldSize = 0x40;

    private readonly List<ArchiveEntry> _entries = [];
    private readonly Blowfish? _cipher;

    public string FormatName => "KIF";
    public string ArchivePath { get; }

    /// <summary>KIF entries decrypt with the archive seed, so they stay playable.</summary>
    public bool CanDecryptPayload => true;
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private CatSystem2Reader(string path, Blowfish? cipher)
    {
        ArchivePath = path;
        _cipher = cipher;
    }

    public static CatSystem2Reader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 0x10) return null;

            Span<byte> head = stackalloc byte[0x50];
            if (fs.Read(head) != head.Length) return null;
            if (!head[..4].SequenceEqual("KIF\0"u8)) return null;

            int count = BinaryPrimitives.ReadInt32LittleEndian(head[4..]);
            if (count is < 0 or > 0x400000) return null;

            bool encrypted = head[8..20].SequenceEqual("__key__.dat\0"u8);
            var reader = encrypted ? OpenEncrypted(path, fs, head, count) : OpenPlain(path, fs, count);
            return reader;
        }
        catch
        {
            return null;
        }
    }

    private static CatSystem2Reader? OpenEncrypted(string path, FileStream fs, Span<byte> head, int count)
    {
        if (count <= 1) return null;
        if (0x50 + (long)(count - 1) * EncryptedRecordSize > fs.Length) return null;

        uint seed = BinaryPrimitives.ReadUInt32LittleEndian(head[0x4C..]);
        var random = new Cs2Random(seed);
        Span<byte> keyBytes = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32LittleEndian(keyBytes, random.Next());

        var cipher = new Blowfish(keyBytes);
        var reader = new CatSystem2Reader(path, cipher);

        var records = new byte[(count - 1) * EncryptedRecordSize];
        fs.Position = 0x50;
        fs.ReadExactly(records);

        for (int i = 1; i < count; i++)
        {
            var record = records.AsSpan((i - 1) * EncryptedRecordSize, EncryptedRecordSize);
            uint storedOffset = BinaryPrimitives.ReadUInt32LittleEndian(record[NameFieldSize..]);
            uint size = BinaryPrimitives.ReadUInt32LittleEndian(record[(NameFieldSize + 4)..]);

            uint offset = storedOffset + (uint)i;
            cipher.DecryptPair(ref offset, ref size);
            if (offset > fs.Length || size > fs.Length || offset + (long)size > fs.Length) return null;

            string name = $"m{i:00000000}.ogg";
            reader._entries.Add(new ArchiveEntry
            {
                ArchivePath = path,
                EntryPath = name,
                Offset = offset,
                StoredLength = size,
                UnpackedLength = size,
                IsEncrypted = true,
                Tag = i,
            });
        }

        return reader._entries.Count > 0 ? reader : null;
    }

    private static CatSystem2Reader? OpenPlain(string path, FileStream fs, int count)
    {
        if (count == 0 || count > 0x40000) return null;
        var reader = new CatSystem2Reader(path, cipher: null);

        Span<byte> word = stackalloc byte[4];
        foreach (int nameSize in (int[])[0x20, 0x40])
        {
            reader._entries.Clear();
            long pos = 8;
            bool ok = true;

            for (int i = 0; i < count; i++)
            {
                if (pos + nameSize + 8 > fs.Length) { ok = false; break; }

                var nameBytes = new byte[nameSize];
                fs.Position = pos;
                fs.ReadExactly(nameBytes);
                pos += nameSize;

                fs.ReadExactly(word);
                uint offset = BinaryPrimitives.ReadUInt32LittleEndian(word);
                fs.ReadExactly(word);
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(word);
                pos += 8;

                string name = ReadCp932(nameBytes);
                if (string.IsNullOrEmpty(name) || offset <= pos || offset + (long)size > fs.Length)
                {
                    ok = false;
                    break;
                }

                reader._entries.Add(new ArchiveEntry
                {
                    ArchivePath = path,
                    EntryPath = name.Replace('\\', '/'),
                    Offset = offset,
                    StoredLength = size,
                    UnpackedLength = size,
                });
            }

            if (ok && reader._entries.Count > 0) return reader;
        }

        return null;
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

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        using var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);
        var data = new byte[entry.StoredLength];
        slice.ReadExactly(data);

        if (_cipher is not null) _cipher.Decrypt(data, data.Length / 8 * 8);

        return new MemoryStream(data, writable: false);
    }

    public void Dispose() { }
}
