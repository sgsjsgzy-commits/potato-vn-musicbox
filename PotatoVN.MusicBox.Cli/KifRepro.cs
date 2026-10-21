using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Archives;

/// <summary>
/// Reproduction harness for the CatSystem2 name cipher: encrypts a candidate
/// plain name with the engine's rule and compares against the stored field.
/// </summary>
internal static class KifRepro
{
    public static string Run(string intPath, string exePath)
    {
        var output = new StringBuilder();
        string vcode = FindStartupVcode(intPath) ?? ReadVcode2(exePath);
        output.AppendLine($"v_code: \"{vcode}\"");
        uint tocSeed = GenTocSeed(vcode);
        output.AppendLine($"toc seed: {tocSeed:X8}");

        var data = File.ReadAllBytes(intPath);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));

        var storedNames = new List<string>();
        for (int i = 1; i < count; i++)
        {
            var fieldProbe = data.AsSpan(0x50 + (i - 1) * 0x48, 0x40);
            int nulProbe = fieldProbe.IndexOf((byte)0);
            storedNames.Add(Encoding.ASCII.GetString(fieldProbe[..(nulProbe < 0 ? fieldProbe.Length : nulProbe)]));
        }

        // The only per-entry parameter is the starting shift, so recover it
        // directly: choose the shift whose decoded name carries a known
        // extension. No v_code needed.
        string[] extensions = [".ogg", ".wav", ".png", ".ks", ".csv", ".tjs", ".txt", ".anm", ".fes", ".dat", ".bin", ".int"];
        for (int i = 0; i < storedNames.Count; i++)
        {
            var matches = new List<string>();
            for (int shift = 0; shift < 52; shift++)
            {
                string decoded = DecodeWithShift(storedNames[i], shift);
                foreach (var ext in extensions)
                {
                    if (decoded.EndsWith(ext, StringComparison.OrdinalIgnoreCase))
                    {
                        matches.Add($"{shift}:{decoded}");
                        break;
                    }
                }
            }

            output.AppendLine($"{i + 1,3} {storedNames[i],-34} -> {string.Join(" | ", matches)}");
        }

        return output.ToString();
    }

    private static string Decode(string stored, uint seed)
    {
        const string forward = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        const string reverse = "zyxwvutsrqponmlkjihgfedcbaZYXWVUTSRQPONMLKJIHGFEDCBA";

        var random = new Cs2Random(seed);
        uint key = random.Next();
        int shift = (byte)((key >> 24) + (key >> 16) + (key >> 8) + key);

        var chars = stored.ToCharArray();
        for (int p = 0; p < chars.Length; p++)
        {
            int index = 0;
            while (index < 0x34)
            {
                if (reverse[(shift + index) % 0x34] == chars[p]) break;
                if (reverse[(shift + index + 1) % 0x34] == chars[p]) { index += 1; break; }
                if (reverse[(shift + index + 2) % 0x34] == chars[p]) { index += 2; break; }
                if (reverse[(shift + index + 3) % 0x34] == chars[p]) { index += 3; break; }
                index += 4;
            }

            if (index < 0x34) chars[p] = forward[index];
            shift++;
        }

        return new string(chars);
    }

    private static string DecodeWithShift(string stored, int shift0)
    {
        const string forward = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        const string reverse = "zyxwvutsrqponmlkjihgfedcbaZYXWVUTSRQPONMLKJIHGFEDCBA";

        var chars = stored.ToCharArray();
        int shift = shift0;
        for (int p = 0; p < chars.Length; p++)
        {
            int index = 0;
            while (index < 0x34)
            {
                if (reverse[(shift + index) % 0x34] == chars[p]) break;
                if (reverse[(shift + index + 1) % 0x34] == chars[p]) { index += 1; break; }
                if (reverse[(shift + index + 2) % 0x34] == chars[p]) { index += 2; break; }
                if (reverse[(shift + index + 3) % 0x34] == chars[p]) { index += 3; break; }
                index += 4;
            }

            if (index < 0x34) chars[p] = forward[index];
            shift++;
        }

        return new string(chars);
    }

    private static string? FindStartupVcode(string intPath)
    {
        // startup.xml sits next to the archive in the game's config folder.
        foreach (var root in new[]
                 {
                     Path.Combine(Path.GetDirectoryName(intPath)!, "config"),
                     Path.GetDirectoryName(intPath)!,
                     Path.Combine(Path.GetDirectoryName(intPath)!, "..", "config"),
                 })
        {
            string path = Path.Combine(root, "startup.xml");
            if (!File.Exists(path)) continue;
            var match = System.Text.RegularExpressions.Regex.Match(
                File.ReadAllText(path), @"<v_code>\s*([^<]+?)\s*</v_code>");
            if (match.Success) return match.Groups[1].Value;
        }

        return null;
    }

    private static string Obfuscate(string plain, uint seed)
    {
        const string forward = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        const string reverse = "zyxwvutsrqponmlkjihgfedcbaZYXWVUTSRQPONMLKJIHGFEDCBA";

        var random = new Cs2Random(seed);
        uint key = random.Next();
        int shift = (byte)((key >> 24) + (key >> 16) + (key >> 8) + key);

        var chars = plain.ToCharArray();
        for (int p = 0; p < chars.Length; p++)
        {
            // The engine maps forward -> reverse with a rolling index.
            int index = forward.IndexOf(chars[p]);
            if (index >= 0) chars[p] = reverse[(shift + index) % 0x34];
            shift++;
        }

        return new string(chars);
    }

    private static string ReadVcode2(string exePath)
    {
        var pe = File.ReadAllBytes(exePath);
        int lfanew = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(lfanew + 6));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(lfanew + 20));
        int sectionTable = lfanew + 24 + optSize;

        int rsrcOffset = 0;
        for (int i = 0; i < sections; i++)
        {
            int entry = sectionTable + i * 40;
            string name = Encoding.ASCII.GetString(pe, entry, 8).TrimEnd('\0');
            if (name == ".rsrc")
                rsrcOffset = (int)BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 20));
        }

        byte[]? keyBytes = FindResource(pe, rsrcOffset, "KEY_CODE", "KEY");
        byte[]? dataBytes = FindResource(pe, rsrcOffset, "V_CODE2", "DATA");
        if (keyBytes is null || dataBytes is null) return string.Empty;

        for (int i = 0; i < keyBytes.Length; i++) keyBytes[i] ^= 0xCD;
        new Blowfish(keyBytes).Decrypt(dataBytes, dataBytes.Length);
        int end = Array.IndexOf(dataBytes, (byte)0);
        return Encoding.ASCII.GetString(dataBytes, 0, end < 0 ? dataBytes.Length : end);
    }

    private static byte[]? FindResource(byte[] pe, int rsrcOffset, string typeName, string dataName)
    {
        int root = rsrcOffset;
        int typeCount = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(root + 12));

        for (int i = 0; i < typeCount; i++)
        {
            int entry = root + 16 + i * 8;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 4));
            if ((id & 0x80000000) == 0 || (offset & 0x80000000) == 0) continue;
            if (ReadResourceString(pe, root + (int)(id & 0x7FFFFFFF)) != typeName) continue;

            int nameDir = root + (int)(offset & 0x7FFFFFFF);
            int nameCount = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(nameDir + 12));
            for (int j = 0; j < nameCount; j++)
            {
                int nameEntry = nameDir + 16 + j * 8;
                uint nameId = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(nameEntry));
                uint langOffset = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(nameEntry + 4));
                if ((nameId & 0x80000000) == 0 || (langOffset & 0x80000000) == 0) continue;
                if (ReadResourceString(pe, root + (int)(nameId & 0x7FFFFFFF)) != dataName) continue;

                int langDir = root + (int)(langOffset & 0x7FFFFFFF);
                int langEntry = langDir + 16;
                uint dataEntry = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(langEntry + 4));
                int dataDir = root + (int)(dataEntry & 0x7FFFFFFF);
                uint rva = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(dataDir));
                uint size = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(dataDir + 4));
                int offsetInFile = (int)RvaToOffset(pe, rva);
                if (offsetInFile <= 0) return null;

                var result = new byte[(size + 7) & ~7u];
                Array.Copy(pe, offsetInFile, result, 0, size);
                return result;
            }
        }

        return null;
    }

    private static string ReadResourceString(byte[] pe, int offset)
    {
        int length = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(offset));
        return Encoding.Unicode.GetString(pe, offset + 2, length * 2);
    }

    private static long RvaToOffset(byte[] pe, uint rva)
    {
        int lfanew = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(lfanew + 6));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(lfanew + 20));
        int sectionTable = lfanew + 24 + optSize;

        for (int i = 0; i < sections; i++)
        {
            int entry = sectionTable + i * 40;
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 8));
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 12));
            uint rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 20));
            if (rva >= virtualAddress && rva < virtualAddress + virtualSize)
                return rawPointer + (rva - virtualAddress);
        }

        return -1;
    }

    private static uint GenTocSeed(string gameId)
    {
        const uint magic = 0x4C11DB7;
        uint seed = uint.MaxValue;

        foreach (char character in gameId)
        {
            seed ^= (uint)character << 24;
            for (int i = 0; i < 8; i++)
                seed = (seed & 0x80000000) != 0 ? (seed * 2) ^ magic : seed * 2;
            seed = ~seed;
        }

        return seed;
    }
}
