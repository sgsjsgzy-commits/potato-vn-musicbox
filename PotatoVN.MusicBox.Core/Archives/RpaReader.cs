using System.IO.Compression;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// Ren'Py RPA archive reader (RPA-2.0 / 3.0 / 3.2 / 4.0).
///
/// Header line: "RPA-x.y &lt;hex index offset&gt; [hex key] [hex key32]".
/// The index at that offset is a zlib stream containing a pickled dict:
///   { b"path": [(offset, length, prefix), ...], ... }
/// Offsets and lengths are XORed with the key; the prefix (first 16 bytes of
/// the payload) is stored separately and must be prepended on read.
/// </summary>
public sealed class RpaReader : IArchiveReader
{
    private readonly List<ArchiveEntry> _entries = [];
    private readonly int _key;

    public string FormatName { get; private init; } = "RPA";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;

    private RpaReader(string path, int key)
    {
        ArchivePath = path;
        _key = key;
    }

    public static RpaReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < 51) return null;

            string header = ReadHeaderLine(fs);
            if (!header.StartsWith("RPA-", StringComparison.Ordinal)) return null;

            var parts = header.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2) return null;

            string version = parts[0];
            long offset = Convert.ToInt64(parts[1], 16);
            int key = version switch
            {
                "RPA-3.2" => parts.Length > 3 ? Convert.ToInt32(parts[3], 16) : 0,
                "RPA-3.0" or "RPA-4.0" => parts.Length > 2 ? Convert.ToInt32(parts[2], 16) : 0,
                _ => 0,
            };

            if (offset <= 0 || offset >= fs.Length) return null;

            fs.Position = offset;
            using var zlib = new ZLibStream(fs, CompressionMode.Decompress, leaveOpen: true);
            using var index = new MemoryStream();
            zlib.CopyTo(index);

            var pickle = new PickleReader(index.ToArray());
            if (pickle.Read() is not Dictionary<object, object?> dict) return null;

            var reader = new RpaReader(path, key) { FormatName = version };
            reader.ParseIndex(dict);
            return reader.Entries.Count > 0 ? reader : null;
        }
        catch
        {
            return null;
        }
    }

    private void ParseIndex(Dictionary<object, object?> dict)
    {
        foreach (var (rawKey, rawValue) in dict)
        {
            string name = rawKey switch
            {
                string s => s,
                byte[] b => Encoding.UTF8.GetString(b),
                _ => rawKey.ToString() ?? string.Empty,
            };
            if (string.IsNullOrEmpty(name)) continue;

            if (rawValue is not List<object?> list || list.Count == 0) continue;
            if (list[0] is not object?[] tuple || tuple.Length < 2) continue;

            long offset = Convert.ToInt64(tuple[0]) ^ _key;
            long length = Convert.ToInt64(tuple[1]) ^ _key;
            byte[] prefix = tuple.Length > 2 ? ToBytes(tuple[2]) : [];

            _entries.Add(new ArchiveEntry
            {
                ArchivePath = ArchivePath,
                EntryPath = name.Replace('\\', '/'),
                Offset = offset,
                StoredLength = Math.Max(0, length - prefix.Length),
                UnpackedLength = length,
                Tag = prefix,
            });
        }
    }

    private static byte[] ToBytes(object? value) => value switch
    {
        byte[] b => b,
        string s => Encoding.UTF8.GetBytes(s),
        null => [],
        _ => [],
    };

    private static string ReadHeaderLine(Stream stream)
    {
        var bytes = new List<byte>(64);
        int b;
        while ((b = stream.ReadByte()) >= 0 && b != '\n' && bytes.Count < 256) bytes.Add((byte)b);
        return Encoding.UTF8.GetString(bytes.ToArray()).TrimEnd('\r');
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        byte[] prefix = entry.Tag as byte[] ?? [];
        var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);

        if (prefix.Length == 0) return slice;

        var output = new MemoryStream((int)Math.Min(entry.UnpackedLength, 64L * 1024 * 1024));
        output.Write(prefix);
        slice.CopyTo(output);
        slice.Dispose();
        output.Position = 0;
        return output;
    }

    public void Dispose() { }
}
