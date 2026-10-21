using System.Buffers.Binary;
using System.Text;
using MusicBox.Core.Archives;

/// <summary>
/// Scratch probe for the CatSystem2 KIF name cipher: reads the v_code from the
/// game's executable resources and tries to reproduce the obfuscated names.
/// </summary>
internal static class KifProbe
{
    public static string Probe(string intPath, string exePath)
    {
        var output = new StringBuilder();
        string vcode = FindStartupVcode(intPath) ?? FindVcode2(exePath, output);
        output.AppendLine($"v_code: \"{vcode}\"");

        uint tocSeed = GenTocSeed(vcode);
        output.AppendLine($"toc seed: {tocSeed:X8}");

        var data = File.ReadAllBytes(intPath);
        uint count = BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(4));
        output.AppendLine($"entries: {count}");

        var names = new List<string>();
        for (int i = 1; i < count && i <= 40; i++)
        {
            var raw = data.AsSpan(0x50 + (i - 1) * 0x48, 0x40);
            int nul = raw.IndexOf((byte)0);
            string stored = Encoding.ASCII.GetString(raw[..(nul < 0 ? raw.Length : nul)]);
            string decoded = Unobfuscate(stored, tocSeed + (uint)i);
            names.Add($"{i,3}  {stored,-34} -> {decoded}");
        }

        foreach (var line in names) output.AppendLine(line);
        return output.ToString();
    }

    private static string? FindStartupVcode(string intPath)
    {
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

    private static string FindVcode2(string exePath, StringBuilder output)
    {
        // Minimal PE resource walk: find the RT_RCDATA type and read the KEY and
        // DATA entries, which hold the v_code encrypted with Blowfish.
        var pe = File.ReadAllBytes(exePath);
        int lfanew = BinaryPrimitives.ReadInt32LittleEndian(pe.AsSpan(0x3C));
        int sections = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(lfanew + 6));
        int optSize = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(lfanew + 20));
        int sectionTable = lfanew + 24 + optSize;

        int rsrcOffset = 0;
        uint rsrcRva = 0;
        for (int i = 0; i < sections; i++)
        {
            int entry = sectionTable + i * 40;
            uint virtualSize = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 8));
            uint virtualAddress = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 12));
            uint rawSize = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 16));
            uint rawPointer = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 20));
            if (virtualSize == 0) virtualSize = rawSize;
            if (rsrcRva >= virtualAddress && rsrcRva < virtualAddress + rawSize && rsrcOffset == 0)
            {
                // rsrcRva is resolved below; placeholder to keep the loop simple.
            }

            // Look for the .rsrc section by name.
            string name = Encoding.ASCII.GetString(pe, entry, 8).TrimEnd('\0');
            if (name == ".rsrc")
            {
                rsrcOffset = (int)rawPointer;
                rsrcRva = virtualAddress;
            }
        }

        if (rsrcOffset == 0)
        {
            output.AppendLine("no .rsrc section");
            return string.Empty;
        }

        int typeDir = rsrcOffset;
        int typeCount = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(typeDir + 12));
        uint keyName = 0, dataName = 0;
        uint keyType = 0, dataType = 0;

        for (int i = 0; i < typeCount; i++)
        {
            int entry = typeDir + 16 + i * 8;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 4));
            if ((offset & 0x80000000) == 0) continue;

            int nameDir = rsrcOffset + (int)(offset & 0x7FFFFFFF);
            int nameCount = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(nameDir + 12));
            for (int j = 0; j < nameCount; j++)
            {
                int nameEntry = nameDir + 16 + j * 8;
                uint nameId = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(nameEntry));
                if ((nameId & 0x80000000) != 0) continue;
                if (nameId == 1) { keyType = id; keyName = nameId; }
                if (nameId == 2) { dataType = id; dataName = nameId; }
            }
        }

        byte[]? keyBytes = ReadResource(pe, rsrcOffset, keyType, keyName);
        byte[]? dataBytes = ReadResource(pe, rsrcOffset, dataType, dataName);
        if (keyBytes is null || dataBytes is null)
        {
            output.AppendLine($"missing resources key={keyType}/{keyName} data={dataType}/{dataName}");
            return string.Empty;
        }

        for (int i = 0; i < keyBytes.Length; i++) keyBytes[i] ^= 0xCD;
        output.AppendLine($"key: {Convert.ToHexString(keyBytes)}  data: {Convert.ToHexString(dataBytes)}");

        var blowfish = new Blowfish(keyBytes);
        blowfish.Decrypt(dataBytes, (dataBytes.Length + 7) & ~7);
        int end = Array.IndexOf(dataBytes, (byte)0);
        if (end < 0) end = dataBytes.Length;
        return Encoding.ASCII.GetString(dataBytes, 0, end);
    }

    private static byte[]? ReadResource(byte[] pe, int rsrcOffset, uint typeId, uint nameId)
    {
        int typeDir = rsrcOffset;
        int typeCount = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(typeDir + 12));
        for (int i = 0; i < typeCount; i++)
        {
            int entry = typeDir + 16 + i * 8;
            uint id = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry));
            uint offset = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(entry + 4));
            if (id != typeId || (offset & 0x80000000) == 0) continue;

            int nameDir = rsrcOffset + (int)(offset & 0x7FFFFFFF);
            int nameCount = BinaryPrimitives.ReadUInt16LittleEndian(pe.AsSpan(nameDir + 12));
            for (int j = 0; j < nameCount; j++)
            {
                int nameEntry = nameDir + 16 + j * 8;
                uint name = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(nameEntry));
                uint langOffset = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(nameEntry + 4));
                if ((langOffset & 0x80000000) == 0) continue;

                int langDir = rsrcOffset + (int)(langOffset & 0x7FFFFFFF);
                int langEntry = langDir + 16;
                uint dataEntry = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(langEntry + 4));
                int dataDir = rsrcOffset + (int)(dataEntry & 0x7FFFFFFF);
                uint dataRva = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(dataDir));
                uint dataSize = BinaryPrimitives.ReadUInt32LittleEndian(pe.AsSpan(dataDir + 4));
                int dataOffset = (int)RvaToOffset(pe, dataRva);
                if (dataOffset <= 0 || dataOffset + (long)dataSize > pe.Length) return null;

                var result = new byte[(dataSize + 7) & ~7u];
                Array.Copy(pe, dataOffset, result, 0, dataSize);
                return result;
            }
        }

        return null;
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
            {
                seed = (seed & 0x80000000) != 0 ? (seed * 2) ^ magic : seed * 2;
            }

            seed = ~seed;
        }

        return seed;
    }

    private static string Unobfuscate(string text, uint seed)
    {
        const string forward = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz";
        const string reverse = "zyxwvutsrqponmlkjihgfedcbaZYXWVUTSRQPONMLKJIHGFEDCBA";

        var random = new Cs2Random(seed);
        uint key = random.Next();
        int shift = (byte)((key >> 24) + (key >> 16) + (key >> 8) + key);

        var chars = text.ToCharArray();
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
}
