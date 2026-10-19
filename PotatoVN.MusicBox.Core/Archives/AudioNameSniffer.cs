namespace MusicBox.Core.Archives;

/// <summary>
/// Some indexes store bare ordinals ("001") instead of file names. The payload
/// signature is then the only way to know whether an entry is playable, and the
/// scanner filters by extension — so give such entries a truthful suffix.
/// </summary>
internal static class AudioNameSniffer
{
    /// <summary>
    /// Returns the entry name with an audio extension appended when it has none
    /// and the payload starts with a known audio container.
    /// </summary>
    public static string WithAudioExtension(string name, ReadOnlySpan<byte> head)
    {
        if (HasExtension(name) || head.Length < 4) return name;

        string suffix = head switch
        {
            [0x4F, 0x67, 0x67, 0x53, ..] => ".ogg",
            [0x52, 0x49, 0x46, 0x46, ..] => ".wav",
            [0x66, 0x4C, 0x61, 0x43, ..] => ".flac",
            [0x49, 0x44, 0x33, ..] => ".mp3",
            [0xFF, 0xFB, ..] => ".mp3",
            [0xFF, 0xF3, ..] => ".mp3",
            [0xFF, 0xF2, ..] => ".mp3",
            _ => string.Empty,
        };

        return suffix.Length == 0 ? name : name + suffix;
    }

    private static bool HasExtension(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Length > 1 && extension.Length <= 5;
    }
}
