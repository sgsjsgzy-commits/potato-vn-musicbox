using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Models;

namespace MusicBox.Core.Archives;

/// <summary>
/// QLIE engine "FilePackVer3.x" archives (GameData/*.pack).
///
/// Layout: payloads, an index, a 256-byte checksum block, then a 28-byte footer
/// holding the marker, entry count and index offset.
///
/// Each index record is
/// <c>[uint16 nameLength][name XOR nameKey][uint64 offset][uint32 storedSize]
/// [uint32 unpackedSize][uint32 packed][uint32 encryption][uint32 checksum]</c>.
///
/// Two generations ship with different cryptography:
/// <list type="bullet">
/// <item>3.0 — CP932 names masked with a byte key. Encrypted members use the
/// legacy stream cipher (type 1) or the keyed MT cipher (types 2+), whose key
/// material comes from "DLL/key.fkey" and, for most titles, a 256-byte icon
/// image embedded in the executable.</item>
/// <item>3.1 — UTF-16 names and a different checksum. The "pack_keyfile"
/// member decrypts with the external key file, then serves as the key for every
/// later member; no executable icon is involved.</item>
/// </list>
/// </summary>
public sealed class QlieReader : IArchiveReader
{
    private const int FooterSize = 28;
    private const int KeyAreaSize = 0x400;
    private const int RecordTail = 28;

    private readonly List<ArchiveEntry> _entries;
    private readonly uint _archiveKey;
    private readonly bool _unicode;
    private readonly byte[]? _keyFile;
    private readonly byte[]? _gameKey;
    private readonly bool _canDecrypt;

    public string FormatName => "QLIE";
    public string ArchivePath { get; }
    public IReadOnlyList<ArchiveEntry> Entries => _entries;
    public bool CanDecryptPayload => _canDecrypt;

    private QlieReader(
        string path, uint archiveKey, bool unicode,
        byte[]? keyFile, byte[]? gameKey, bool canDecrypt, List<ArchiveEntry> entries)
    {
        ArchivePath = path;
        _archiveKey = archiveKey;
        _unicode = unicode;
        _keyFile = keyFile;
        _gameKey = gameKey;
        _canDecrypt = canDecrypt;
        _entries = entries;
    }

    public static QlieReader? TryOpen(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            if (fs.Length < KeyAreaSize + FooterSize) return null;

            var footer = new byte[FooterSize];
            fs.Position = fs.Length - FooterSize;
            fs.ReadExactly(footer);

            bool unicode = footer.AsSpan(0, 16).SequenceEqual("FilePackVer3.1\0\0"u8);
            if (!unicode && !footer.AsSpan(0, 16).SequenceEqual("FilePackVer3.0\0\0"u8))
                return null;

            uint count = BinaryPrimitives.ReadUInt32LittleEndian(footer.AsSpan(16));
            ulong indexOffset = BinaryPrimitives.ReadUInt64LittleEndian(footer.AsSpan(20));
            long indexEnd = fs.Length - FooterSize - KeyAreaSize;

            if (count is 0 or > 100_000) return null;
            if (indexOffset >= (ulong)indexEnd) return null;

            // The checksum block sits immediately before the footer.
            var keyBlock = new byte[256];
            fs.Position = fs.Length - FooterSize - KeyAreaSize;
            fs.ReadExactly(keyBlock);

            uint archiveKey = unicode
                ? HashV31(keyBlock) & 0x0FFFFFFF
                : HashV3(keyBlock) & 0x0FFFFFFF;

            var entries = ReadIndex(fs, count, (long)indexOffset, indexEnd, archiveKey, unicode, path);
            if (entries is null || entries.Count == 0) return null;

            // Key material: the title's key file, plus the executable icon for
            // the 3.0 generation. The "pack_keyfile" member is decrypted first
            // and then serves as the key for every following member.
            byte[]? keyFile = LoadKeyFile(path);
            byte[]? gameKey = !unicode ? LoadIconKey(path) : null;
            byte[]? packKey = ResolvePackKey(path, entries, archiveKey, unicode, keyFile, gameKey);
            if (packKey is not null) keyFile = packKey;

            // Type 1 needs no key material; everything else needs the key file.
            bool canDecrypt = entries.All(e =>
                e.Tag is not QlieEntry meta || meta.Encryption is 0 or 1 || keyFile is not null);

            return new QlieReader(path, archiveKey, unicode, keyFile, gameKey, canDecrypt, entries);
        }
        catch
        {
            return null;
        }
    }

    private static List<ArchiveEntry>? ReadIndex(
        FileStream fs, uint count, long indexOffset, long indexEnd,
        uint archiveKey, bool unicode, string path)
    {
        var entries = new List<ArchiveEntry>((int)Math.Min(count, 100_000));
        fs.Position = indexOffset;
        var header = new byte[2];

        for (uint i = 0; i < count; i++)
        {
            if (fs.Position + header.Length > indexEnd) return null;
            fs.ReadExactly(header);

            int nameUnits = BinaryPrimitives.ReadUInt16LittleEndian(header);
            if (nameUnits is <= 0 or > 256) return null;

            int nameBytes = unicode ? nameUnits * 2 : nameUnits;
            if (fs.Position + nameBytes + RecordTail > indexEnd) return null;

            var rawName = new byte[nameBytes];
            fs.ReadExactly(rawName);

            byte[] decoded = unicode ? UnmaskNameV31(rawName, archiveKey) : UnmaskNameV3(rawName, archiveKey);
            string name = unicode ? DecodeUtf16(decoded) : DecodeCp932(decoded);
            if (string.IsNullOrWhiteSpace(name)) return null;

            var record = new byte[RecordTail];
            fs.ReadExactly(record);

            ulong offset = BinaryPrimitives.ReadUInt64LittleEndian(record);
            uint stored = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(8));
            uint unpacked = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(12));
            uint packed = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(16));
            uint encryption = BinaryPrimitives.ReadUInt32LittleEndian(record.AsSpan(20));

            if (offset + stored > (ulong)indexEnd) return null;
            if (packed > 1 || encryption > 4) return null;

            entries.Add(new ArchiveEntry
            {
                ArchivePath = path,
                EntryPath = name.Replace('\\', '/'),
                Offset = (long)offset,
                StoredLength = stored,
                UnpackedLength = unpacked > 0 ? unpacked : stored,
                IsCompressed = packed == 1,
                IsEncrypted = encryption != 0,
                Tag = new QlieEntry(decoded, name, (int)encryption),
            });
        }

        return entries;
    }

    /// <summary>3.0 names: byte-wise mask keyed by the name length and archive key.</summary>
    private static byte[] UnmaskNameV3(byte[] raw, uint archiveKey)
    {
        var data = (byte[])raw.Clone();
        uint key = (uint)data.Length + (archiveKey ^ 0x3E);
        for (int i = 0; i < data.Length; i++)
            data[i] ^= (byte)((((uint)(i + 1) ^ key) + (uint)i + 1) & 0xFF);

        return data;
    }

    /// <summary>3.1 names: UTF-16 masked with a 16-bit rolling key.</summary>
    private static byte[] UnmaskNameV31(byte[] raw, uint archiveKey)
    {
        var data = (byte[])raw.Clone();
        int chars = data.Length / 2;
        int hash = (chars * chars) ^ chars;
        hash ^= 0x3E13 ^ (int)(archiveKey >> 16) ^ (int)archiveKey;
        hash &= 0xFFFF;

        int state = hash;
        for (int i = 0; i < chars; i++)
        {
            state = hash + i + 8 * state;
            data[i * 2] ^= (byte)state;
            data[i * 2 + 1] ^= (byte)(state >> 8);
        }

        return data;
    }

    private static string DecodeUtf16(byte[] data)
    {
        int length = data.Length;
        while (length >= 2 && data[length - 1] == 0 && data[length - 2] == 0) length -= 2;
        return length == 0 ? string.Empty : Encoding.Unicode.GetString(data, 0, length);
    }

    private static string DecodeCp932(byte[] raw)
    {
        int length = raw.AsSpan().IndexOf((byte)0);
        if (length < 0) length = raw.Length;
        if (length == 0) return string.Empty;

        try
        {
            Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
            return Encoding.GetEncoding(932).GetString(raw, 0, length);
        }
        catch
        {
            return Encoding.ASCII.GetString(raw, 0, length);
        }
    }

    /// <summary>QLIE 3.0 checksum over the 256-byte key block.</summary>
    private static uint HashV3(ReadOnlySpan<byte> data)
    {
        ulong h = 0;
        ulong key = 0;

        for (int pos = 0; pos + 8 <= data.Length; pos += 8)
        {
            Add16(ref h, 0x0307030703070307UL);
            Add16(ref key, BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]) ^ h);
        }

        return (uint)(key ^ (key >> 32));
    }

    /// <summary>
    /// 3.1 checksum: the same lane-wise addition, but each 32-bit half is
    /// rotated left by three after every word and the halves are folded with a
    /// multiply-add at the end.
    /// </summary>
    private static uint HashV31(ReadOnlySpan<byte> data)
    {
        ulong h = 0;
        ulong key = 0;

        for (int pos = 0; pos + 8 <= data.Length; pos += 8)
        {
            Add16(ref h, 0xA35793A7A35793A7UL);
            Add16(ref key, BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]) ^ h);
            key = RotateLanes32(key, 3);
        }

        short a = (short)(key & 0xFFFF);
        short b = (short)((key >> 32) & 0xFFFF);
        short c = (short)((key >> 16) & 0xFFFF);
        short d = (short)((key >> 48) & 0xFFFF);
        return (uint)(a * b + c * d);
    }

    private static ulong RotateLanes32(ulong value, int bits)
    {
        uint low = (uint)value;
        uint high = (uint)(value >> 32);
        low = (low << bits) | (low >> (32 - bits));
        high = (high << bits) | (high >> (32 - bits));
        return low | ((ulong)high << 32);
    }

    /// <summary>16-bit lane-wise addition as the engine performs it.</summary>
    private static void Add16(ref ulong accumulator, ulong value)
    {
        // Each 16-bit lane is added modulo 2^16; carry must not leak into the
        // next lane, so every lane is masked at its own width.
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 16)
        {
            ulong lane = ((accumulator >> shift) & 0xFFFF) + ((value >> shift) & 0xFFFF);
            result |= (lane & 0xFFFF) << shift;
        }

        accumulator = result;
    }

    /// <summary>
    /// Finds the title's key file ("DLL/key.fkey" or a copy beside the
    /// archive). Its first entry is itself the key material.
    /// </summary>
    private static byte[]? LoadKeyFile(string archivePath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(archivePath);
            if (directory is null) return null;

            string? parent = Path.GetDirectoryName(directory);
            string[] candidates =
            [
                Path.Combine(directory, "DLL", "key.fkey"),
                Path.Combine(directory, "key.fkey"),
                Path.Combine(parent ?? directory, "DLL", "key.fkey"),
                Path.Combine(parent ?? directory, "key.fkey"),
            ];

            foreach (var candidate in candidates)
            {
                if (File.Exists(candidate)) return File.ReadAllBytes(candidate);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Extracts the 256-byte "IconKeyImage" that most 3.0 titles embed as a
    /// Delphi form resource. The image is a TIcon stream; the key follows its
    /// six-byte header.
    /// </summary>
    private static byte[]? LoadIconKey(string archivePath)
    {
        try
        {
            string? directory = Path.GetDirectoryName(archivePath);
            for (int depth = 0; directory is not null && depth < 3; depth++)
            {
                foreach (var exe in Directory.EnumerateFiles(directory, "*.exe"))
                {
                    var key = ReadIconKey(exe);
                    if (key is not null) return key;
                }

                directory = Path.GetDirectoryName(directory);
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    private static byte[]? ReadIconKey(string exePath)
    {
        try
        {
            var info = new FileInfo(exePath);
            if (info.Length is <= 0 or > 512L * 1024 * 1024) return null;

            byte[] data;
            using (var fs = new FileStream(exePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                data = new byte[fs.Length];
                fs.ReadExactly(data);
            }

            ReadOnlySpan<byte> marker = "IconKeyImage"u8;
            ReadOnlySpan<byte> icon = "\x05TIcon"u8;

            int search = 0;
            while (search < data.Length)
            {
                int name = data.AsSpan(search).IndexOf(marker);
                if (name < 0) break;
                name += search;

                int iconStart = data.AsSpan(name, Math.Min(200, data.Length - name)).IndexOf(icon);
                if (iconStart < 0)
                {
                    search = name + marker.Length;
                    continue;
                }

                iconStart += name + icon.Length;
                if (iconStart + 0x100 <= data.Length) return data.AsSpan(iconStart, 0x100).ToArray();

                search = name + marker.Length;
            }

            return null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Decrypts the archive's "pack_keyfile" member. Its plaintext is the key
    /// used by every later member, which is why it has to be resolved first.
    /// </summary>
    private static byte[]? ResolvePackKey(
        string path, List<ArchiveEntry> entries, uint archiveKey, bool unicode,
        byte[]? keyFile, byte[]? gameKey)
    {
        var pack = entries.FirstOrDefault(
            e => e.EntryPath.Contains("pack_keyfile", StringComparison.OrdinalIgnoreCase));
        if (pack?.Tag is not QlieEntry meta || meta.Encryption == 0) return null;

        try
        {
            var data = ReadPayload(path, pack);

            if (unicode)
            {
                // 3.1: type 1 is the table cipher and needs no key material,
                // which is what makes the key file self-bootstrapping.
                if (meta.Encryption == 1) CryptTable(data, meta.Name, archiveKey);
                else if (keyFile is not null) CryptV2(data, meta.Name, archiveKey, keyFile, null);
                else return null;
            }
            else
            {
                // 3.0: type 1 is the legacy stream cipher (no key material);
                // types 2+ the MT cipher seeded with key file and icon.
                if (meta.Encryption == 1) CryptLegacy(data, archiveKey);
                else if (keyFile is not null) CryptMt(data, meta.NameBytes, archiveKey, keyFile, gameKey);
                else return null;
            }

            // A decrypted key file always carries this marker. When it is
            // missing the key material was wrong (for 3.0 that usually means
            // the executable's icon was not found).
            return IsKeyFile(data) ? data : null;
        }
        catch
        {
            return null;
        }
    }

    private static bool IsKeyFile(ReadOnlySpan<byte> data) =>
        data.Length >= 12 && data[..8].SequenceEqual("KeyFile "u8);

    private static byte[] ReadPayload(string path, ArchiveEntry entry)
    {
        using var slice = new FileSliceStream(path, entry.Offset, entry.StoredLength);
        var data = new byte[entry.StoredLength];
        slice.ReadExactly(data);
        return data;
    }

    public Stream OpenEntryStream(ArchiveEntry entry)
    {
        var slice = new FileSliceStream(ArchivePath, entry.Offset, entry.StoredLength);
        if (!entry.IsEncrypted) return Unpack(slice, entry);

        using (slice)
        {
            if (entry.Tag is not QlieEntry meta)
                throw new InvalidDataException("QLIE entry is missing its index record.");

            var data = new byte[entry.StoredLength];
            slice.ReadExactly(data);

            if (_unicode)
            {
                if (meta.Encryption == 1) CryptTable(data, meta.Name, _archiveKey);
                else if (_keyFile is not null) CryptV2(data, meta.Name, _archiveKey, _keyFile, null);
                else return Unpack(new MemoryStream(data, writable: false), entry);
            }
            else
            {
                if (meta.Encryption == 1) CryptLegacy(data, _archiveKey);
                else if (_keyFile is not null) CryptMt(data, meta.NameBytes, _archiveKey, _keyFile, _gameKey);
                else return Unpack(new MemoryStream(data, writable: false), entry);
            }

            return Unpack(new MemoryStream(data, writable: false), entry);
        }
    }

    private static Stream Unpack(Stream payload, ArchiveEntry entry)
    {
        if (!entry.IsCompressed) return payload;

        using (payload)
        {
            var raw = ReadAll(payload);
            var decoded = QlieCodec.TryDecompress1Pc(raw, entry.UnpackedLength);
            return new MemoryStream(decoded ?? raw, writable: false);
        }
    }

    private static byte[] ReadAll(Stream stream)
    {
        long length = stream.Length;
        if (length <= 0 || length > int.MaxValue) return [];
        var buffer = new byte[(int)length];
        stream.ReadExactly(buffer);
        return buffer;
    }

    /// <summary>
    /// 3.0 type 1: a 64-bit rolling hash whose 32-bit lanes step by a fixed
    /// constant and are XORed with the previous decoded qword.
    /// </summary>
    private static void CryptLegacy(Span<byte> data, uint archiveKey)
    {
        int length = data.Length;
        if (length < 8) return;

        ulong hash = 0xA73C5F9DA73C5F9DUL;
        ulong xor = ((uint)length + archiveKey) ^ 0xFEC9753E;
        xor |= xor << 32;

        for (int pos = 0; pos + 8 <= length; pos += 8)
        {
            hash = AddLanes32(hash, 0xCE24F523CE24F523UL) ^ xor;
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data[pos..]) ^ hash;
            BinaryPrimitives.WriteUInt64LittleEndian(data[pos..], word);
            xor = word;
        }
    }

    /// <summary>
    /// 3.1 type 1: a 32-word table grown from a seed that mixes the member
    /// name, the archive key and the payload length. Two adjacent table words
    /// form the running mask, stepping through the table every qword.
    /// </summary>
    private static void CryptTable(byte[] data, string name, uint archiveKey)
    {
        int length = data.Length;
        if (length < 8) return;

        uint hash = 0x85F532;
        uint seed = 0x33F641;
        for (int i = 0; i < name.Length; i++)
        {
            hash += (uint)(name[i] << (i & 7));
            seed ^= hash;
        }

        seed += archiveKey ^ (7 * ((uint)length & 0xFFFFFF) + (uint)length
                              + hash + (hash ^ (uint)length ^ 0x8F32DCu));
        seed = 9 * (seed & 0xFFFFFF);

        var table = GenerateTable(0x20, seed);
        int k = 2 * (int)(table[0xD] & 0xF);
        ulong running = table[6] | ((ulong)table[7] << 32);

        for (int pos = 0; pos + 8 <= length; pos += 8)
        {
            ulong t = table[k] | ((ulong)table[k + 1] << 32);
            running = AddLanes32(running ^ t, t);
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)) ^ running;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(pos), word);

            running = AddLanes8(running, word) ^ word;
            running = AddLanes16(ShiftLanes32(running), word);
            k = (k + 2) & 0x1F;
        }
    }

    /// <summary>
    /// 3.0 type 2+: a 64-word MT variant seeded from the member name bytes and
    /// XORed with the key material.
    /// </summary>
    private static void CryptMt(byte[] data, byte[] nameBytes, uint archiveKey, byte[] keyFile, byte[]? gameKey)
    {
        int length = data.Length;
        if (length < 8) return;

        uint hash = 0x85F532;
        uint seed = 0x33F641;
        for (int i = 0; i < nameBytes.Length; i++)
        {
            hash += (uint)(i & 0xFF) * nameBytes[i];
            seed ^= hash;
        }

        seed += archiveKey ^ (7 * ((uint)length & 0xFFFFFF) + (uint)length
                              + hash + (hash ^ (uint)length ^ 0x8F32DCu));
        seed = 9 * (seed & 0xFFFFFF);
        if (gameKey is not null) seed ^= 0x453A;

        var random = new QlieTwister(seed);
        random.XorState(keyFile);
        if (gameKey is not null) random.XorState(gameKey);

        var table = new ulong[16];
        for (int i = 0; i < table.Length; i++) table[i] = random.Next64();
        for (int i = 0; i < 9; i++) random.Next();

        ulong running = random.Next64();
        int slot = (int)(random.Next() & 0xF);

        for (int pos = 0; pos + 8 <= length; pos += 8)
        {
            running = AddLanes32(running ^ table[slot], table[slot]);
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)) ^ running;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(pos), word);

            running = AddLanes8(running, word) ^ word;
            running = AddLanes16(ShiftLanes32(running), word);
            slot = (slot + 1) & 0xF;
        }
    }

    /// <summary>
    /// 3.1 type 2: a 64-word table grown from a seed plus a 0x400-byte schedule
    /// derived from the key file, which makes the mask key dependent.
    /// </summary>
    private static void CryptV2(byte[] data, string name, uint archiveKey, byte[] keyFile, byte[]? gameKey)
    {
        int length = data.Length;
        if (length < 8) return;

        uint hash = 0x86F7E2;
        uint seed = 0x4437F1;
        for (int i = 0; i < name.Length; i++)
        {
            hash += (uint)(name[i] << (i & 7));
            seed ^= hash;
        }

        seed += archiveKey ^ (13 * ((uint)length & 0xFFFFFF) + (uint)length
                              + hash + (hash ^ (uint)length ^ 0x56E213u));
        seed = 13 * (seed & 0xFFFFFF);
        if (gameKey is not null) seed ^= 0x453A;

        var table = GenerateTable(0x20, seed, 0x8A77F473);
        var keyData = GenerateKeyData(keyFile);

        int k = (8 * (int)(table[8] & 0xD)) & 0x7F;
        ulong running = table[6] | ((ulong)table[7] << 32);

        for (int pos = 0; pos + 8 <= length; pos += 8)
        {
            int tIndex = 2 * (k & 0xF);
            ulong t = table[tIndex] | ((ulong)table[tIndex + 1] << 32);
            t ^= BinaryPrimitives.ReadUInt64LittleEndian(keyData.AsSpan(8 * k));

            running = AddLanes32(running ^ t, t);
            ulong word = BinaryPrimitives.ReadUInt64LittleEndian(data.AsSpan(pos)) ^ running;
            BinaryPrimitives.WriteUInt64LittleEndian(data.AsSpan(pos), word);

            running = AddLanes8(running, word) ^ word;
            running = AddLanes16(ShiftLanes32(running), word);
            k = (k + 1) & 0x7F;
        }
    }

    /// <summary>Linear-congruential table used by the 3.1 ciphers.</summary>
    private static uint[] GenerateTable(int length, uint seed, uint key = 0x8DF21431)
    {
        var table = new uint[length];
        for (int i = 0; i < length; i++)
        {
            ulong product = key * (ulong)(seed ^ key);
            seed = (uint)((product >> 32) + product);
            table[i] = seed;
        }

        return table;
    }

    /// <summary>
    /// Key schedule derived from the key file: a fixed pattern XORed with a
    /// stride selected by two bytes of the key file itself.
    /// </summary>
    private static byte[] GenerateKeyData(byte[]? keyFile)
    {
        var data = new byte[0x400];
        for (int i = 0; i < 0x100; i++)
        {
            int value = i % 3 != 0 ? (i + 7) * -(i + 3) : (i + 7) * (i + 3);
            BinaryPrimitives.WriteInt32LittleEndian(data.AsSpan(i * 4), value);
        }

        if (keyFile is { Length: >= 128 })
        {
            int k = keyFile[49] % 73 + 128;
            int step = keyFile[79] % 7 + 7;
            for (int i = 0; i < data.Length; i++)
            {
                k = (k + step) % keyFile.Length;
                data[i] ^= keyFile[k];
            }
        }

        return data;
    }

    /// <summary>Per-32-bit-lane addition, as MMX PADDD performs it.</summary>
    private static ulong AddLanes32(ulong a, ulong b)
    {
        uint low = (uint)a + (uint)b;
        uint high = (uint)(a >> 32) + (uint)(b >> 32);
        return low | ((ulong)high << 32);
    }

    /// <summary>Per-byte addition, as MMX PADDB performs it.</summary>
    private static ulong AddLanes8(ulong a, ulong b)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 8)
            result |= ((((a >> shift) + (b >> shift)) & 0xFF) << shift);
        return result;
    }

    /// <summary>Per-16-bit-lane addition, as MMX PADDW performs it.</summary>
    private static ulong AddLanes16(ulong a, ulong b)
    {
        ulong result = 0;
        for (int shift = 0; shift < 64; shift += 16)
            result |= ((((a >> shift) + (b >> shift)) & 0xFFFF) << shift);
        return result;
    }

    /// <summary>Per-32-bit-lane left shift, as MMX PSLLD performs it.</summary>
    private static ulong ShiftLanes32(ulong value)
    {
        uint low = (uint)value << 1;
        uint high = (uint)(value >> 32) << 1;
        return low | ((ulong)high << 32);
    }

    /// <summary>QLIE's 64-word MT variant, distinct from standard MT19937.</summary>
    private sealed class QlieTwister
    {
        private readonly uint[] _state = new uint[64];
        private int _pos = 64;

        public QlieTwister(uint seed)
        {
            _state[0] = seed;
            for (int i = 1; i < 64; i++)
            {
                uint prev = _state[i - 1];
                _state[i] = unchecked(0x6611BC19u * (prev ^ (prev >> 30)) + (uint)i);
            }
        }

        public void XorState(byte[]? material)
        {
            if (material is null) return;
            int words = Math.Min(material.Length / 4, 64);
            for (int i = 0; i < words; i++)
                _state[i] ^= BinaryPrimitives.ReadUInt32LittleEndian(material.AsSpan(i * 4));
        }

        public uint Next()
        {
            if (_pos >= 64)
            {
                for (int i = 0; i < 63; i++)
                {
                    uint y = (_state[i] & 0x80000000u) | ((_state[i + 1] & 0x7FFFFFFFu) >> 1);
                    _state[i] = _state[(i + 39) % 64] ^ y
                                ^ ((_state[i + 1] & 1) != 0 ? 0x9908B0DFu : 0u);
                }

                uint z = (_state[63] & 0x80000000u) | ((_state[0] & 0x7FFFFFFFu) >> 1);
                _state[63] = _state[38] ^ z ^ ((_state[62] & 1) != 0 ? 0x9908B0DFu : 0u);
                _pos = 0;
            }

            uint value = _state[_pos++];
            value ^= value >> 11;
            value ^= (value << 7) & 0x9C4F88E3u;
            value ^= (value << 15) & 0xE7F70000u;
            return value ^ (value >> 18);
        }

        public ulong Next64() => Next() | ((ulong)Next() << 32);
    }

    public void Dispose() { }

    /// <summary>
    /// Index data a member's cipher needs: the decoded name bytes (3.0 seeds
    /// the MT state from them), the decoded name string, and the cipher type.
    /// </summary>
    internal sealed record QlieEntry(byte[] NameBytes, string Name, int Encryption);
}

/// <summary>
/// QLIE's "1PC" compressed stream: a byte-pair dictionary followed by a symbol
/// stream. Audio is stored raw, but scripts and images use it, so it is decoded
/// here rather than handed back as garbage.
/// </summary>
internal static class QlieCodec
{
    /// <summary>
    /// Decompresses a "1PC\xFF" stream, or returns null when the input is not
    /// one or the declared size cannot be met.
    /// </summary>
    public static byte[]? TryDecompress1Pc(byte[] data, long expectedSize)
    {
        if (data.Length < 12) return null;
        if (!data.AsSpan(0, 4).SequenceEqual("1PC\xFF"u8)) return null;

        uint declared = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(8));
        if (declared == 0 || declared > 64 * 1024 * 1024) return null;
        if (expectedSize > 0 && declared != expectedSize) return null;

        var output = new byte[declared];
        int written = 0;
        int pos = 12;
        var left = new byte[256];
        var right = new byte[256];

        while (pos < data.Length && written < output.Length)
        {
            for (int i = 0; i < 256; i++)
            {
                left[i] = (byte)i;
                right[i] = 0;
            }

            int index = 0;
            while (index < 256)
            {
                if (pos >= data.Length) return null;
                int count = data[pos++];
                if (count > 127)
                {
                    index += count - 127;
                    count = 0;
                }

                if (index > 256) return null;
                if (index > 255) break;
                if (index + count + 1 > 256) return null;

                for (int i = 0; i <= count; i++)
                {
                    if (pos >= data.Length) return null;
                    left[index] = data[pos++];
                    if (left[index] != index)
                    {
                        if (pos >= data.Length) return null;
                        right[index] = data[pos++];
                    }

                    index++;
                }
            }

            int width = (data[4] & 1) != 0 ? 2 : 4;
            if (pos + width > data.Length) return null;

            long countSymbols = width == 2
                ? BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(pos))
                : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(pos));
            pos += width;

            if (countSymbols < 0 || pos + countSymbols > data.Length) return null;

            for (long i = 0; i < countSymbols && written < output.Length; i++)
            {
                if (!Expand(data[pos + i], left, right, output, ref written)) return null;
            }

            pos += (int)countSymbols;
        }

        return written == output.Length ? output : null;
    }

    /// <summary>
    /// Expands one dictionary symbol. The walk is iterative because a symbol
    /// can reference another pair; cycles are rejected rather than followed.
    /// </summary>
    private static bool Expand(byte symbol, byte[] left, byte[] right, byte[] output, ref int written)
    {
        Span<int> stack = stackalloc int[64];
        int depth = 0;
        stack[depth++] = symbol;

        int guard = 0;
        while (depth > 0)
        {
            if (++guard > 1_000_000) return false;

            int current = stack[--depth];
            if (left[current] == current)
            {
                if (written >= output.Length) return false;
                output[written++] = (byte)current;
            }
            else
            {
                if (depth + 2 > stack.Length) return false;
                stack[depth++] = right[current];
                stack[depth++] = left[current];
            }
        }

        return true;
    }
}
