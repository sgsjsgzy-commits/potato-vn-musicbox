using System.Text;
using MusicBox.Core.Archives;
using MusicBox.Core.Audio;
using MusicBox.Core.Models;
using MusicBox.Core.Scanning;

Console.OutputEncoding = Encoding.UTF8;

if (args.Length == 0)
{
    Console.WriteLine("""
        PotatoVN.MusicBox scanner test bench

        Commands:
          scan <game folder> [--voice] [--se] [--no-probe]   Scan a game folder
          archive <file>                                      Dump archive index
          play <file|archive::entry>                          Verify a payload is audio
        """);
    return;
}

switch (args[0])
{
    case "scan":
        RunScan(args);
        break;
    case "archive":
        RunArchive(args[1]);
        break;
    case "play":
        RunPlay(args[1]);
        break;
    case "probe":
        RunProbe(args[1], args.Length > 2 ? args[2] : null);
        break;
    default:
        Console.WriteLine($"Unknown command: {args[0]}");
        break;
}

static void RunScan(string[] args)
{
    string path = args[1];
    var options = new MusicScanner.ScanOptions
    {
        IncludeVoice = args.Contains("--voice"),
        IncludeSoundEffects = args.Contains("--se"),
        ProbeDuration = !args.Contains("--no-probe"),
    };

    var scanner = new MusicScanner();
    var progress = new Progress<string>(msg => Console.WriteLine($"  · {msg}"));
    var library = scanner.Scan(Path.GetFileName(path.TrimEnd('\\', '/')), path, options: options, progress: progress);

    Console.WriteLine();
    Console.WriteLine($"游戏: {library.GameName}");
    Console.WriteLine($"用时: {library.ScanDuration.TotalSeconds:F1}s");
    Console.WriteLine($"曲目: {library.Tracks.Count} (BGM {library.Bgm.Count()}, 语音 {library.Voice.Count()}, SE {library.SoundEffects.Count()})");

    foreach (var warning in library.Warnings)
        Console.WriteLine($"警告: {warning}");

    Console.WriteLine();
    foreach (var track in library.Tracks.Take(200))
    {
        Console.WriteLine($"  [{track.Kind,-11}] {track.DurationSeconds,7:F1}s  {track.Container,-8} {track.Album}/{track.FileName}");
    }

    if (library.Tracks.Count > 200)
        Console.WriteLine($"  ... 其余 {library.Tracks.Count - 200} 条省略");
}

static void RunProbe(string root, string? reportPath)
{
    string[] extensions =
        [".xp3", ".arc", ".pak", ".pac", ".noa", ".ypf", ".rpa", ".nsa", ".dat", ".bin", ".pck", ".int", ".afa", ".owp"];

    var report = new StringBuilder();
    int total = 0, ok = 0, fail = 0;
    foreach (var file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
    {
        var ext = Path.GetExtension(file).ToLowerInvariant();
        if (!extensions.Contains(ext)) continue;

        long size;
        try { size = new FileInfo(file).Length; } catch { continue; }
        if (size < 32) continue;

        total++;
        string head;
        try
        {
            using var fs = File.OpenRead(file);
            var buf = new byte[16];
            int read = fs.Read(buf, 0, buf.Length);
            head = Convert.ToHexString(buf.AsSpan(0, read));
        }
        catch { head = "?"; }

        using var reader = ArchiveRegistry.TryOpen(file);
        string status = reader is null ? "FAIL" : $"ok:{reader.Entries.Count}";
        if (reader is null) fail++; else ok++;

        report.AppendLine($"{status}\t{size}\t{head}\t{file}");
    }

    if (reportPath is not null) File.WriteAllText(reportPath, report.ToString(), new UTF8Encoding(false));
    Console.Error.WriteLine($"\n{total} files, {ok} parsed, {fail} failed");
}

static void RunArchive(string path)
{
    using var reader = ArchiveRegistry.TryOpen(path);
    if (reader is null)
    {
        Console.WriteLine("Not a supported archive.");
        return;
    }

    Console.WriteLine($"Format: {reader.FormatName}, entries: {reader.Entries.Count}");
    foreach (var entry in reader.Entries.Take(50))
    {
        Console.WriteLine($"  {entry.EntryPath}  offset={entry.Offset} stored={entry.StoredLength} unpacked={entry.UnpackedLength} enc={entry.IsEncrypted} cmp={entry.IsCompressed}");
    }
}

static void RunPlay(string target)
{
    string archive = target;
    string entryPath = string.Empty;
    int split = target.IndexOf("::", StringComparison.Ordinal);
    if (split > 0)
    {
        archive = target[..split];
        entryPath = target[(split + 2)..];
    }

    using var reader = ArchiveRegistry.TryOpen(archive);
    if (reader is null)
    {
        Console.WriteLine("Not a supported archive.");
        return;
    }

    var entry = reader.Entries.FirstOrDefault(e => e.EntryPath == entryPath)
                ?? reader.Entries.FirstOrDefault(e => e.Name == Path.GetFileName(entryPath));
    if (entry is null)
    {
        Console.WriteLine($"Entry not found: {entryPath}");
        return;
    }

    using var stream = reader.OpenEntryStream(entry);
    Console.WriteLine($"Audio: {AudioProbe.LooksLikeAudio(stream)}");
    Console.WriteLine($"Duration: {AudioProbe.GetDuration(stream):F1}s");

    // Dump the first bytes so the container can be identified by eye.
    stream.Position = 0;
    var head = new byte[16];
    stream.ReadExactly(head);
    Console.WriteLine($"Header: {Convert.ToHexString(head)}");
}
