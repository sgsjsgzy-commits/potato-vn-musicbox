using System.Security.Cryptography;
using System.Text;
using MusicBox.Core.Archives;
using MusicBox.Core.Audio;
using MusicBox.Core.Models;

namespace MusicBox.Core.Scanning;

/// <summary>
/// Walks a game installation folder and produces a <see cref="GameMusicLibrary"/>.
///
/// Strategy:
/// 1. Loose audio files are picked up directly (fast, no archive parsing).
/// 2. Archive files are probed by signature; readable indexes yield their audio
///    entries. Encrypted/unreadable entries are skipped with a warning.
/// 3. Entries are classified into BGM / voice / SE by their folder and name.
/// </summary>
public sealed class MusicScanner
{
    private static readonly string[] ArchiveExtensions =
    [
        ".xp3", ".arc", ".pak", ".pac", ".noa", ".ypf", ".rpa", ".nsa",
        ".bin", ".int", ".afa", ".owp", ".pck",
    ];

    /// <summary>Folders that almost always contain voice clips rather than music.</summary>
    private static readonly string[] VoiceMarkers =
        ["voice", "vocal", "cv", "pcm", "ボイス", "配音"];

    /// <summary>Exact folder names that mean sound effects.</summary>
    private static readonly string[] SeMarkers =
        ["se", "sfx", "soundeffect", "sound_effect", "soundeffects", "効果音", "音效"];

    private static readonly string[] BgmMarkers =
        ["bgm", "music", "ost", "soundtrack", "サウンド", "音楽", "音乐"];

    /// <summary>
    /// Directories that never contain playable game music and are expensive to
    /// walk (engine caches, browser data, videos).
    /// </summary>
    private static readonly HashSet<string> SkipDirectories = new(StringComparer.OrdinalIgnoreCase)
    {
        "node_modules", ".git", "__pycache__", "cache", "temp", "tmp", "logs", "save", "savedata",
        "crash", "EBWebView", "_PluginXamlHotReload", "runtimes", "movie", "movies", "video", "videos",
    };

    public sealed record ScanOptions
    {
        /// <summary>Include voice clips (usually thousands of tiny files).</summary>
        public bool IncludeVoice { get; init; } = false;

        /// <summary>Include sound effects.</summary>
        public bool IncludeSoundEffects { get; init; } = false;

        /// <summary>Probe duration by reading container headers (slower for archives).</summary>
        public bool ProbeDuration { get; init; } = true;

        /// <summary>Maximum archive file size to probe, in bytes.</summary>
        public long MaxArchiveSize { get; init; } = 8L * 1024 * 1024 * 1024;
    }

    public GameMusicLibrary Scan(
        string gameName,
        string gamePath,
        Guid gameId = default,
        ScanOptions? options = null,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        options ??= new ScanOptions();
        var started = DateTime.Now;
        var library = new GameMusicLibrary
        {
            GameName = gameName,
            GamePath = gamePath,
            GameId = gameId,
        };

        if (!Directory.Exists(gamePath))
        {
            library.Warnings.Add($"游戏目录不存在：{gamePath}");
            return library;
        }

        var seenIds = new HashSet<string>(StringComparer.Ordinal);
        var archiveFiles = new List<string>();

        foreach (var file in EnumerateFiles(gamePath, token))
        {
            token.ThrowIfCancellationRequested();

            string extension = Path.GetExtension(file).ToLowerInvariant();

            if (AudioProbe.IsPlayableExtension(extension))
            {
                progress?.Report($"发现音频：{Path.GetFileName(file)}");
                AddLooseFile(library, gamePath, file, seenIds, options);
            }
            else if (ArchiveExtensions.Contains(extension))
            {
                archiveFiles.Add(file);
            }
        }

        foreach (var archivePath in archiveFiles)
        {
            token.ThrowIfCancellationRequested();
            progress?.Report($"解析归档：{Path.GetFileName(archivePath)}");
            ScanArchive(library, gamePath, archivePath, seenIds, options);
        }

        RemoveDuplicates(library);
        SortTracks(library);
        library.ScanDuration = DateTime.Now - started;
        return library;
    }

    /// <summary>
    /// Games often ship the same track in several archives (e.g. a "bgm" and a
    /// "patch" archive). Keep the first occurrence per file name + duration so
    /// the playlist does not repeat itself.
    /// </summary>
    private static void RemoveDuplicates(GameMusicLibrary library)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unique = new List<GameTrack>(library.Tracks.Count);

        foreach (var track in library.Tracks)
        {
            string key = $"{track.FileName}|{(int)Math.Round(track.DurationSeconds)}|{track.Kind}";
            if (seen.Add(key)) unique.Add(track);
        }

        library.Tracks.Clear();
        library.Tracks.AddRange(unique);
    }

    private static IEnumerable<string> EnumerateFiles(string root, CancellationToken token)
    {
        var stack = new Stack<string>();
        stack.Push(root);

        while (stack.Count > 0)
        {
            token.ThrowIfCancellationRequested();
            string dir = stack.Pop();

            string[] subdirs;
            string[] files;
            try
            {
                subdirs = Directory.GetDirectories(dir);
                files = Directory.GetFiles(dir);
            }
            catch
            {
                continue;
            }

            foreach (var file in files) yield return file;

            foreach (var sub in subdirs)
            {
                var name = Path.GetFileName(sub);
                if (SkipDirectories.Contains(name)) continue;
                if (name.StartsWith('.')) continue;
                stack.Push(sub);
            }
        }
    }

    private static void AddLooseFile(
        GameMusicLibrary library,
        string gameRoot,
        string file,
        HashSet<string> seenIds,
        ScanOptions options)
    {
        string relative = Path.GetRelativePath(gameRoot, file);
        string folder = Path.GetDirectoryName(relative)?.Replace('\\', '/') ?? string.Empty;
        var kind = Classify(folder, Path.GetFileName(file));
        if (!ShouldInclude(kind, options)) return;

        string id = MakeId(file, relative);
        if (!seenIds.Add(id)) return;

        var track = new GameTrack
        {
            Id = id,
            Title = MakeTitle(Path.GetFileNameWithoutExtension(file)),
            FileName = Path.GetFileName(file),
            RelativeFolder = folder,
            Container = "文件",
            Kind = kind,
            SearchText = MakeSearchText(Path.GetFileNameWithoutExtension(file), folder),
            Source = new ArchiveEntry
            {
                FilePath = file,
                StoredLength = SafeLength(file),
                UnpackedLength = SafeLength(file),
            },
        };

        if (options.ProbeDuration) track.DurationSeconds = ProbeFile(file);
        library.Tracks.Add(track);
    }

    private static void ScanArchive(
        GameMusicLibrary library,
        string gameRoot,
        string archivePath,
        HashSet<string> seenIds,
        ScanOptions options)
    {
        long archiveLength;
        try
        {
            archiveLength = new FileInfo(archivePath).Length;
        }
        catch
        {
            return;
        }

        if (archiveLength <= 0 || archiveLength > options.MaxArchiveSize) return;

        using IArchiveReader? reader = ArchiveRegistry.TryOpen(archivePath);
        if (reader is null)
        {
            // Only warn for files that actually look like archives; random .dat
            // files (saves, configs) are expected to fail and are not noise.
            if (ArchiveRegistry.HasKnownSignature(archivePath))
                library.Warnings.Add($"无法解析归档：{Path.GetFileName(archivePath)}");
            return;
        }

        string archiveName = Path.GetFileName(archivePath);
        int skippedEncrypted = 0;
        int undecodableAudio = 0;

        if (reader.IndexProtectionNote is { } note)
            library.Warnings.Add($"{archiveName}：{note}");

        // Archives whose index cannot expose names (CatSystem2's encrypted KIF,
        // Favorite's numbered entries) label their music by ordinal. In that
        // case the archive name decides what the payload is.
        bool archiveIsMusic = IsMusicArchiveName(archiveName);
        bool archiveIsVoice = IsVoiceArchiveName(archiveName);

        foreach (var entry in reader.Entries)
        {
            // Indexes that only store ordinals need the payload signature to
            // decide whether the entry is audio at all.
            var entryForScan = entry;
            if (!AudioProbe.IsPlayableExtension(entry.Name) && !HasExtension(entry.Name))
            {
                string probed = ProbeName(reader, entry);
                if (probed == entry.Name) continue;
                entryForScan = entry.WithName(probed);
            }

            if (!AudioProbe.IsPlayableExtension(entryForScan.Name))
            {
                // Audio in an engine-private codec (Entis MIO) cannot be decoded
                // here; count it so the UI can explain the empty result.
                if (AudioProbe.IsUndecodableAudio(entryForScan.Name)) undecodableAudio++;
                continue;
            }

            var kind = Classify(entryForScan.EntryPath, entryForScan.Name);
            if (kind == TrackKind.Unknown)
            {
                if (archiveIsVoice) kind = TrackKind.Voice;
                else if (archiveIsMusic) kind = TrackKind.Bgm;
            }

            if (!ShouldInclude(kind, options)) continue;

            if (entryForScan.IsEncrypted && !reader.CanDecryptPayload)
            {
                skippedEncrypted++;
                continue;
            }

            string id = MakeId(archivePath, entryForScan.EntryPath);
            if (!seenIds.Add(id)) continue;

            var track = new GameTrack
            {
                Id = id,
                Title = MakeTitle(Path.GetFileNameWithoutExtension(entryForScan.Name)),
                FileName = entryForScan.Name,
                RelativeFolder = Path.GetDirectoryName(entryForScan.EntryPath.Replace('\\', '/'))?.Replace('\\', '/')
                                 ?? string.Empty,
                Container = reader.FormatName,
                ArchiveName = archiveName,
                Kind = kind,
                SearchText = MakeSearchText(Path.GetFileNameWithoutExtension(entryForScan.Name), entryForScan.EntryPath),
                Source = entryForScan,
            };

            if (options.ProbeDuration)
            {
                try
                {
                    using var stream = reader.OpenEntryStream(entryForScan);
                    if (!AudioProbe.LooksLikeAudio(stream)) continue;
                    track.DurationSeconds = AudioProbe.GetDuration(stream);
                }
                catch
                {
                    // Encrypted payloads can throw; keep the entry but without a duration.
                }
            }

            library.Tracks.Add(track);
        }

        if (skippedEncrypted > 0)
            library.Warnings.Add($"{archiveName}：跳过 {skippedEncrypted} 个加密音频（无法解密）");

        if (undecodableAudio > 0)
            library.Warnings.Add($"{archiveName}：{undecodableAudio} 个曲目使用引擎私有编码（MIO），无法播放");
    }

    private static bool HasExtension(string name)
    {
        string extension = Path.GetExtension(name);
        return extension.Length > 1;
    }

    /// <summary>
    /// Reads the first bytes of an entry to learn its container. Returns the
    /// original name when the payload is not recognisable audio.
    /// </summary>
    private static string ProbeName(IArchiveReader reader, ArchiveEntry entry)
    {
        try
        {
            using var stream = reader.OpenEntryStream(entry);
            Span<byte> head = stackalloc byte[4];
            int read = stream.Read(head);
            if (read < 4) return entry.Name;

            return AudioNameSniffer.WithAudioExtension(entry.Name, head);
        }
        catch
        {
            return entry.Name;
        }
    }

    private static bool IsMusicArchiveName(string archiveName)
    {
        string name = Path.GetFileNameWithoutExtension(archiveName).ToLowerInvariant();
        return BgmMarkers.Any(m => name.Contains(m));
    }

    private static bool IsVoiceArchiveName(string archiveName)
    {
        string name = Path.GetFileNameWithoutExtension(archiveName).ToLowerInvariant();
        return VoiceMarkers.Any(m => name.Contains(m));
    }

    private static bool ShouldInclude(TrackKind kind, ScanOptions options) => kind switch
    {
        TrackKind.Bgm => true,
        TrackKind.Voice => options.IncludeVoice,
        TrackKind.SoundEffect => options.IncludeSoundEffects,
        _ => options.IncludeSoundEffects,
    };

    /// <summary>
    /// Heuristic classification. Path structure wins over file name, because
    /// "bgm/voice_01.ogg" is still music while "voice/bgm_01.ogg" is a line.
    /// </summary>
    public static TrackKind Classify(string folderOrPath, string fileName)
    {
        string path = folderOrPath.Replace('\\', '/').ToLowerInvariant();
        string name = Path.GetFileNameWithoutExtension(fileName).ToLowerInvariant();
        string[] segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        bool nameLooksLikeMusic = name.StartsWith("bgm") || name.StartsWith("mus")
                                  || name.Contains("theme") || name.StartsWith("title");

        // Check directory segments from the deepest one outward.
        for (int i = segments.Length - 1; i >= 0; i--)
        {
            string segment = segments[i];

            if (VoiceMarkers.Any(m => segment == m || segment.Contains(m))) return TrackKind.Voice;
            if (SeMarkers.Any(m => segment == m || segment.StartsWith(m))) return TrackKind.SoundEffect;

            // A bare "sound" folder is usually SFX (Ren'Py: audio/sound), but
            // Japanese engines also use sound/ for music — let the file name
            // decide in that ambiguous case.
            if (segment == "sound")
                return nameLooksLikeMusic ? TrackKind.Bgm : TrackKind.SoundEffect;

            if (BgmMarkers.Any(m => segment == m || segment.Contains(m))) return TrackKind.Bgm;
        }

        if (name.StartsWith("bgm") || name.StartsWith("b_") || name.StartsWith("mus")
            || name.Contains("music") || name.Contains("theme"))
            return TrackKind.Bgm;

        if (name.StartsWith("v_") || name.StartsWith("vo_") || name.StartsWith("voice")
            || name.Contains("_v_") || name.Contains("voice"))
            return TrackKind.Voice;

        if (name.StartsWith("se_") || name.StartsWith("sfx") || name.StartsWith("sound_")
            || name.StartsWith("sys_"))
            return TrackKind.SoundEffect;

        return TrackKind.Unknown;
    }

    /// <summary>
    /// Turns engine file names into something readable: strips common prefixes
    /// ("bgm_", "BGM") and numeric padding, keeps the meaningful part.
    /// </summary>
    public static string MakeTitle(string fileNameWithoutExtension)
    {
        string title = fileNameWithoutExtension.Trim();
        if (string.IsNullOrEmpty(title)) return "未命名曲目";

        title = title.Replace('_', ' ').Replace('-', ' ').Trim();

        // "bgm 001" → keep as-is but collapse repeated whitespace.
        var parts = title.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        title = string.Join(' ', parts);

        return title.Length > 80 ? title[..80] : title;
    }

    private static string MakeSearchText(string name, string folder) =>
        $"{name} {folder}".ToLowerInvariant();

    private static string MakeId(string source, string entryPath)
    {
        var hash = SHA1.HashData(Encoding.UTF8.GetBytes($"{source}\0{entryPath}"));
        return Convert.ToHexString(hash.AsSpan(0, 10)).ToLowerInvariant();
    }

    private static long SafeLength(string path)
    {
        try
        {
            return new FileInfo(path).Length;
        }
        catch
        {
            return 0;
        }
    }

    private static double ProbeFile(string path)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            return AudioProbe.GetDuration(fs);
        }
        catch
        {
            return 0;
        }
    }

    private static void SortTracks(GameMusicLibrary library)
    {
        var ordered = library.Tracks
            .OrderBy(t => t.Kind)
            .ThenBy(t => t.RelativeFolder, StringComparer.OrdinalIgnoreCase)
            .ThenBy(t => t.FileName, Comparer<string>.Create((a, b) => string.CompareOrdinal(NaturalOrder(a), NaturalOrder(b))))
            .ToList();

        library.Tracks.Clear();
        library.Tracks.AddRange(ordered);
    }

    /// <summary>Sorts "track2" before "track10" (natural / numeric-aware order).</summary>
    private static string NaturalOrder(string name)
    {
        var builder = new StringBuilder(name.Length + 8);
        int index = 0;
        while (index < name.Length)
        {
            if (char.IsDigit(name[index]))
            {
                int start = index;
                while (index < name.Length && char.IsDigit(name[index])) index++;
                int length = index - start;
                builder.Append('0', Math.Max(0, 10 - length));
                builder.Append(name, start, length);
            }
            else
            {
                builder.Append(char.ToLowerInvariant(name[index]));
                index++;
            }
        }

        return builder.ToString();
    }
}
