using System.Buffers.Binary;
using System.Text;

namespace MusicBox.Core.Audio;

/// <summary>
/// Lightweight audio metadata probing without external decoders. Reads the
/// container headers of Ogg Vorbis/Opus, MP3 (with or without ID3), WAV and
/// FLAC to determine the duration shown in the playlist.
/// </summary>
public static class AudioProbe
{
    public static readonly string[] PlayableExtensions =
        [".ogg", ".opus", ".mp3", ".wav", ".flac", ".m4a", ".aac", ".wma"];

    /// <summary>
    /// Containers an archive can hold that are audio but not decodable here
    /// (the engine's own codec). They are listed for information only.
    /// </summary>
    public static readonly string[] UndecodableAudioExtensions =
        [".mio", ".ems", ".lac", ".owp"];

    public static bool IsPlayableExtension(string name) =>
        PlayableExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    public static bool IsUndecodableAudio(string name) =>
        UndecodableAudioExtensions.Contains(Path.GetExtension(name).ToLowerInvariant());

    /// <summary>Returns duration in seconds, or 0 when it cannot be determined.</summary>
    public static double GetDuration(Stream stream)
    {
        try
        {
            if (!stream.CanSeek) return 0;
            long start = stream.Position;

            Span<byte> head = stackalloc byte[12];
            int read = stream.Read(head);
            stream.Position = start;
            if (read < 12) return 0;

            if (head[..4].SequenceEqual("OggS"u8))
                return ProbeOgg(stream);

            if (head[..4].SequenceEqual("RIFF"u8) && head[8..12].SequenceEqual("WAVE"u8))
                return ProbeWav(stream);

            if (head[..4].SequenceEqual("fLaC"u8))
                return ProbeFlac(stream);

            if (head[..3].SequenceEqual("ID3"u8))
                return ProbeMp3(stream, skipId3: true);

            if (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0)
                return ProbeMp3(stream, skipId3: false);

            return 0;
        }
        catch
        {
            return 0;
        }
    }

    private static double ProbeOgg(Stream stream)
    {
        long length = stream.Length;
        if (length < 64) return 0;

        // Read the last 64 KB and find the final page header, which carries the
        // absolute granule position (sample count).
        int tailSize = (int)Math.Min(64 * 1024, length);
        var tail = new byte[tailSize];
        stream.Position = length - tailSize;
        stream.ReadExactly(tail);

        int lastPage = -1;
        for (int i = tailSize - 14; i >= 0; i--)
        {
            if (tail[i] == 'O' && tail[i + 1] == 'g' && tail[i + 2] == 'g' && tail[i + 3] == 'S')
            {
                lastPage = i;
                break;
            }
        }

        if (lastPage < 0) return 0;

        long granule = BinaryPrimitives.ReadInt64LittleEndian(tail.AsSpan(lastPage + 6));
        if (granule <= 0) return 0;

        // Identify codec from the first page: "vorbis" or "OpusHead".
        var header = new byte[Math.Min(128, length)];
        stream.Position = 0;
        stream.ReadExactly(header);

        bool isOpus = IndexOf(header, "OpusHead"u8) >= 0;
        bool isVorbis = IndexOf(header, "vorbis"u8) >= 0;

        if (isOpus) return granule / 48000.0;
        if (isVorbis) return ProbeVorbisRate(header, granule);

        return 0;
    }

    private static double ProbeVorbisRate(byte[] header, long granule)
    {
        // Identification packet: 1-byte packet type, "vorbis", uint32 version,
        // byte channels, uint32 sample rate. So the rate sits 11 bytes after
        // the start of the "vorbis" marker.
        int index = IndexOf(header, "vorbis"u8);
        if (index < 0 || index + 15 > header.Length) return 0;

        int sampleRate = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(index + 11));
        return sampleRate is > 1000 and < 1_000_000 ? granule / (double)sampleRate : 0;
    }

    private static double ProbeWav(Stream stream)
    {
        using var reader = new BinaryReader(stream, Encoding.ASCII, leaveOpen: true);
        stream.Position = 12;

        int byteRate = 0;
        long dataSize = 0;

        while (stream.Position + 8 <= stream.Length)
        {
            string chunk = Encoding.ASCII.GetString(reader.ReadBytes(4));
            int size = reader.ReadInt32();

            if (chunk == "fmt " && size >= 16)
            {
                reader.ReadInt16(); // format
                reader.ReadInt16(); // channels
                reader.ReadInt32(); // sample rate
                byteRate = reader.ReadInt32();
                stream.Position += Math.Max(0, size - 12);
            }
            else if (chunk == "data")
            {
                dataSize = size;
                break;
            }
            else
            {
                stream.Position += size + (size & 1);
            }
        }

        return byteRate > 0 ? dataSize / (double)byteRate : 0;
    }

    private static double ProbeFlac(Stream stream)
    {
        // "fLaC" (4 bytes) + metadata block header (4 bytes), then the 34-byte
        // STREAMINFO block. Within it: sample rate is a 20-bit field starting
        // at byte 10, total samples a 36-bit field starting mid-byte 13.
        var block = new byte[34];
        stream.Position = 8;
        int read = stream.Read(block);
        if (read < 18) return 0;

        long sampleRate = ((long)(block[10] & 0xFF) << 12)
                          | ((long)(block[11] & 0xFF) << 4)
                          | (uint)((block[12] >> 4) & 0xF);
        long totalSamples = ((long)(block[13] & 0x0F) << 32)
                            | ((long)(block[14] & 0xFF) << 24)
                            | ((long)(block[15] & 0xFF) << 16)
                            | ((long)(block[16] & 0xFF) << 8)
                            | (block[17] & 0xFFL);

        return sampleRate > 0 ? totalSamples / (double)sampleRate : 0;
    }

    private static double ProbeMp3(Stream stream, bool skipId3)
    {
        stream.Position = 0;
        if (skipId3)
        {
            var header = new byte[10];
            stream.ReadExactly(header);
            int size = ((header[6] & 0x7F) << 21) | ((header[7] & 0x7F) << 14)
                       | ((header[8] & 0x7F) << 7) | (header[9] & 0x7F);
            stream.Position = 10 + size;
        }

        long audioStart = stream.Position;
        var frame = new byte[4];
        if (stream.Read(frame) != 4) return 0;
        if (frame[0] != 0xFF || (frame[1] & 0xE0) != 0xE0) return 0;

        int versionBits = (frame[1] >> 3) & 3; // 3 = MPEG1, 2 = MPEG2, 0 = MPEG2.5
        int layerBits = (frame[1] >> 1) & 3;   // 1 = Layer III
        int bitrateIndex = (frame[2] >> 4) & 0xF;
        int sampleRateIndex = (frame[2] >> 2) & 3;
        int padding = (frame[2] >> 1) & 1;

        if (layerBits != 1 || bitrateIndex is 0 or 15 || sampleRateIndex == 3) return 0;

        int[] mpeg1Bitrates = [0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0];
        int[] mpeg2Bitrates = [0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0];
        int[] sampleRates = [44100, 48000, 32000];

        int bitrate = versionBits == 3
            ? mpeg1Bitrates[bitrateIndex]
            : mpeg2Bitrates[bitrateIndex];
        int sampleRate = sampleRates[sampleRateIndex] >> (versionBits == 3 ? 0 : versionBits == 2 ? 1 : 2);

        if (bitrate <= 0 || sampleRate <= 0) return 0;

        int frameLength = (versionBits == 3 ? 144 : 72) * bitrate * 1000 / sampleRate + padding;
        long audioBytes = stream.Length - audioStart;
        if (frameLength <= 0) return 0;

        int samplesPerFrame = versionBits == 3 ? 1152 : 576;
        long frames = audioBytes / frameLength;
        return frames * samplesPerFrame / (double)sampleRate;
    }

    private static int IndexOf(byte[] haystack, ReadOnlySpan<byte> needle)
    {
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            if (haystack.AsSpan(i, needle.Length).SequenceEqual(needle)) return i;
        }

        return -1;
    }

    /// <summary>
    /// Validates that a stream starts with a recognisable audio container. Used
    /// to drop encrypted or misidentified archive entries before they reach the
    /// player.
    /// </summary>
    public static bool LooksLikeAudio(Stream stream)
    {
        try
        {
            if (!stream.CanSeek) return true;
            long start = stream.Position;
            Span<byte> head = stackalloc byte[12];
            int read = stream.Read(head);
            stream.Position = start;
            if (read < 4) return false;

            return head[..4].SequenceEqual("OggS"u8)
                   || head[..4].SequenceEqual("RIFF"u8)
                   || head[..4].SequenceEqual("fLaC"u8)
                   || head[..3].SequenceEqual("ID3"u8)
                   || (head[0] == 0xFF && (head[1] & 0xE0) == 0xE0);
        }
        catch
        {
            return false;
        }
    }
}
