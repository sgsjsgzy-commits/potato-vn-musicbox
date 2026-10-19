using System.Collections.Concurrent;
using MusicBox.Core.Archives;
using MusicBox.Core.Models;
using MusicBox.Core.Scanning;
using PotatoVN.MusicBox.Models;

namespace PotatoVN.MusicBox.Services;

/// <summary>
/// Owns the per-game scan cache and turns cached entries back into playable
/// tracks. Scanning is expensive (thousands of files), so results are cached
/// per game and invalidated when the install folder changes.
/// </summary>
public sealed class MusicLibraryService
{
    private readonly PluginData _data;
    private readonly LibraryCacheStore _cache;
    private readonly ConcurrentDictionary<string, GameMusicLibrary> _memory = new();
    private readonly SemaphoreSlim _scanLock = new(1, 1);

    public MusicLibraryService(PluginData data, LibraryCacheStore? cache = null)
    {
        _data = data;
        _cache = cache ?? new LibraryCacheStore();
        _cache.Load();
    }

    /// <summary>Where scan caches are written.</summary>
    public string CacheRoot => _cache.Root;

    public static string MakeGameKey(Guid gameId) => gameId.ToString("N");

    public bool TryGetCached(string gameKey, out GameMusicLibrary library)
    {
        if (_memory.TryGetValue(gameKey, out library!)) return true;

        if (!_cache.TryGet(gameKey, out var cached)) return false;

        library = FromCache(cached);
        _memory[gameKey] = library;
        return true;
    }

    /// <summary>True when the cache exists but the install folder was touched since.</summary>
    public bool IsStale(string gameKey, string gamePath)
    {
        if (!_cache.TryGet(gameKey, out var cached)) return true;

        if (!string.Equals(cached.GamePath, gamePath, StringComparison.OrdinalIgnoreCase)) return true;

        try
        {
            var lastWrite = Directory.GetLastWriteTimeUtc(gamePath);
            return lastWrite > cached.ScannedAt.ToUniversalTime();
        }
        catch
        {
            return false;
        }
    }

    public async Task<GameMusicLibrary> ScanAsync(
        Guid gameId,
        string gameName,
        string gamePath,
        IProgress<string>? progress = null,
        CancellationToken token = default)
    {
        string key = MakeGameKey(gameId);

        // Only one scan at a time: archive probing is IO heavy.
        await _scanLock.WaitAsync(token);
        try
        {
            var scanner = new MusicScanner();
            var options = new MusicScanner.ScanOptions
            {
                IncludeVoice = _data.IncludeVoice,
                IncludeSoundEffects = _data.IncludeSoundEffects,
                ProbeDuration = _data.ProbeDuration,
            };

            var library = await Task.Run(
                () => scanner.Scan(gameName, gamePath, gameId, options, progress, token),
                token);

            _memory[key] = library;
            _cache.Set(key, ToCache(library));
            return library;
        }
        finally
        {
            _scanLock.Release();
        }
    }

    public void RemoveGame(Guid gameId)
    {
        string key = MakeGameKey(gameId);
        _memory.TryRemove(key, out _);
        _cache.Remove(key);
    }

    public void ClearAll()
    {
        _memory.Clear();
        _cache.Clear();
    }

    /// <summary>
    /// Moves the cache next to the game library the first time a game is
    /// scanned, so a large collection is not stored on the system drive.
    /// </summary>
    public void AdoptLibraryRoot(string folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return;
        _cache.Configure(folder);
    }

    /// <summary>
    /// Opens the decoded audio payload for a track. The caller owns the stream.
    /// </summary>
    public Stream OpenTrack(GameTrack track)
    {
        var source = track.Source;

        if (source.IsLooseFile)
        {
            return new FileStream(source.FilePath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete);
        }

        var reader = ArchiveRegistry.TryOpen(source.ArchivePath)
                     ?? throw new FileNotFoundException("归档无法打开", source.ArchivePath);

        // The index is re-read on every open: cheap for local disks, and it keeps
        // offsets correct if the game was patched after the cache was written.
        // The scanner may append an extension learned from the payload (indexes
        // that store bare ordinals), so fall back to matching the stem.
        var entry = reader.Entries.FirstOrDefault(e =>
                        string.Equals(e.EntryPath, source.EntryPath, StringComparison.OrdinalIgnoreCase))
                    ?? reader.Entries.FirstOrDefault(e =>
                        string.Equals(Path.GetFileNameWithoutExtension(e.EntryPath),
                            Path.GetFileNameWithoutExtension(source.EntryPath),
                            StringComparison.OrdinalIgnoreCase))
                    ?? throw new FileNotFoundException("归档中找不到该曲目", source.EntryPath);

        var stream = reader.OpenEntryStream(entry);

        // Wrap so the archive reader is disposed together with the stream.
        return new ReaderBoundStream(stream, reader);
    }

    private static GameMusicLibrary FromCache(CachedLibrary cached)
    {
        var library = new GameMusicLibrary
        {
            GameName = cached.GameName,
            GamePath = cached.GamePath,
            ScannedAt = cached.ScannedAt,
        };

        foreach (var item in cached.Tracks)
        {
            library.Tracks.Add(new GameTrack
            {
                Id = item.Id,
                Title = item.Title,
                FileName = item.FileName,
                RelativeFolder = item.RelativeFolder,
                Container = item.Container,
                ArchiveName = item.ArchiveName,
                Kind = (TrackKind)item.Kind,
                DurationSeconds = item.DurationSeconds,
                SearchText = $"{item.Title} {item.RelativeFolder}".ToLowerInvariant(),
                Source = new ArchiveEntry
                {
                    ArchivePath = item.ArchivePath,
                    EntryPath = item.EntryPath,
                    FilePath = item.FilePath,
                    Offset = item.Offset,
                    StoredLength = item.StoredLength,
                    UnpackedLength = item.UnpackedLength,
                    IsCompressed = item.IsCompressed,
                    IsEncrypted = item.IsEncrypted,
                },
            });
        }

        library.Warnings.AddRange(cached.Warnings);
        return library;
    }

    private static CachedLibrary ToCache(GameMusicLibrary library)
    {
        var cached = new CachedLibrary
        {
            GameName = library.GameName,
            GamePath = library.GamePath,
            ScannedAt = library.ScannedAt,
        };

        foreach (var track in library.Tracks)
        {
            cached.Tracks.Add(new CachedTrack
            {
                Id = track.Id,
                Title = track.Title,
                FileName = track.FileName,
                RelativeFolder = track.RelativeFolder,
                Container = track.Container,
                ArchiveName = track.ArchiveName,
                Kind = (int)track.Kind,
                DurationSeconds = track.DurationSeconds,
                ArchivePath = track.Source.ArchivePath,
                EntryPath = track.Source.EntryPath,
                FilePath = track.Source.FilePath,
                Offset = track.Source.Offset,
                StoredLength = track.Source.StoredLength,
                UnpackedLength = track.Source.UnpackedLength,
                IsCompressed = track.Source.IsCompressed,
                IsEncrypted = track.Source.IsEncrypted,
            });
        }

        cached.Warnings.AddRange(library.Warnings);
        return cached;
    }

    private sealed class ReaderBoundStream(Stream inner, IArchiveReader reader) : Stream
    {
        public override bool CanRead => inner.CanRead;
        public override bool CanSeek => inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => inner.Length;
        public override long Position { get => inner.Position; set => inner.Position = value; }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override int Read(Span<byte> buffer) => inner.Read(buffer);
        public override long Seek(long offset, SeekOrigin origin) => inner.Seek(offset, origin);
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                inner.Dispose();
                reader.Dispose();
            }

            base.Dispose(disposing);
        }
    }
}
