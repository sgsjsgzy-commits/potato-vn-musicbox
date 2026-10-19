using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using PotatoVN.MusicBox.Models;

namespace PotatoVN.MusicBox.Services;

/// <summary>
/// Scan results on disk, one JSON file per game.
///
/// They deliberately live outside the plugin data blob: a large collection can
/// hold tens of thousands of track records, and the player writes its resume
/// position every few seconds. Keeping the two apart means saving progress is a
/// few hundred bytes instead of re-serialising the whole cache.
///
/// The default location is a <c>.musicbox</c> folder next to the games (same
/// drive as the install folders), which keeps a big library off the system
/// drive. <see cref="Configure"/> can point it somewhere else.
/// </summary>
public sealed class LibraryCacheStore
{
    /// <summary>Folder created inside the galgame library root.</summary>
    public const string FolderName = ".musicbox";

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
    };

    private readonly Dictionary<string, CachedLibrary> _entries = new(StringComparer.OrdinalIgnoreCase);
    private string _root;

    public LibraryCacheStore(string? root = null)
    {
        _root = root ?? DefaultRoot();
    }

    public IEnumerable<KeyValuePair<string, CachedLibrary>> Entries => _entries;

    /// <summary>Folder the cache files are written to.</summary>
    public string Root => _root;

    /// <summary>
    /// Points the store at another folder and reloads. Existing files are left
    /// where they are, so a game cached under the old path is simply rescanned.
    /// </summary>
    public void Configure(string root)
    {
        if (string.IsNullOrWhiteSpace(root)) return;
        if (string.Equals(Path.GetFullPath(root), Path.GetFullPath(_root), StringComparison.OrdinalIgnoreCase))
            return;

        _root = Path.GetFullPath(root);
        Load();
    }

    /// <summary>
    /// Picks a cache folder beside the game library, so a big cache does not
    /// land on the system drive. Uses the deepest folder shared by every game
    /// (typically <c>E:\gal</c>), falling back to LOCALAPPDATA when no game is
    /// known yet.
    /// </summary>
    public static string DefaultRoot(IEnumerable<string>? gamePaths = null)
    {
        string? libraryRoot = FindCommonRoot(gamePaths);
        if (libraryRoot is not null) return Path.Combine(libraryRoot, FolderName);

        return Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "PotatoVN.MusicBox",
            "cache");
    }

    /// <summary>
    /// Finds the folder that holds most of the games — typically <c>E:\gal</c>.
    ///
    /// A strict "common parent of every game" is not good enough: a single game
    /// installed elsewhere (<c>E:\4Jpov_…</c>) collapses it to the drive root.
    /// So we take the parent of each game and pick the one shared by the most
    /// games, which is the library folder the user actually thinks of.
    /// </summary>
    private static string? FindCommonRoot(IEnumerable<string>? gamePaths)
    {
        var directories = gamePaths?
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => p.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar))
            .Where(p => p.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (directories is null || directories.Count == 0) return null;

        // One level up from each game: E:\gal\A\A -> E:\gal\A, E:\gal\A -> E:\gal.
        var parents = directories
            .Select(ParentOrNull)
            .Where(p => p is not null)
            .Select(p => p!)
            .ToList();

        if (parents.Count == 0) return null;

        // Candidate folders ordered by how many games sit under them. The
        // library root wins over per-game folders because it covers more games.
        var candidates = parents
            .SelectMany(p => Ancestors(p))
            .GroupBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Select(g => (Path: g.Key, Count: g.Count()))
            .Where(c => !IsDriveRoot(c.Path))
            .OrderByDescending(c => c.Count)
            .ThenByDescending(c => c.Path.Length)
            .ToList();

        return candidates.Count > 0 ? candidates[0].Path : null;
    }

    private static IEnumerable<string> Ancestors(string path)
    {
        string? current = path;
        while (!string.IsNullOrEmpty(current) && !IsDriveRoot(current))
        {
            yield return current;
            current = Path.GetDirectoryName(current);
        }
    }

    private static string? ParentOrNull(string path)
    {
        try
        {
            return Path.GetDirectoryName(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    private static bool IsDriveRoot(string path) =>
        string.Equals(path, Path.GetPathRoot(path), StringComparison.OrdinalIgnoreCase);

    private static string? SafeRoot(string path)
    {
        try
        {
            return Path.GetPathRoot(Path.GetFullPath(path));
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Loads every cache file. Unreadable files are skipped, not fatal.</summary>
    public void Load()
    {
        _entries.Clear();

        if (!Directory.Exists(_root)) return;

        foreach (string path in Directory.EnumerateFiles(_root, "*.json"))
        {
            try
            {
                var cached = JsonSerializer.Deserialize<CachedLibrary>(File.ReadAllText(path));
                if (cached is null) continue;

                string key = Path.GetFileNameWithoutExtension(path);
                _entries[key] = cached;
            }
            catch
            {
                // A stale or half-written file must not break startup.
            }
        }
    }

    public bool TryGet(string gameKey, out CachedLibrary cached) =>
        _entries.TryGetValue(gameKey, out cached!);

    public void Set(string gameKey, CachedLibrary cached)
    {
        _entries[gameKey] = cached;
        Save(gameKey);
    }

    public void Remove(string gameKey)
    {
        _entries.Remove(gameKey);
        TryDelete(PathFor(gameKey));
    }

    public void Clear()
    {
        _entries.Clear();

        if (!Directory.Exists(_root)) return;
        foreach (string path in Directory.EnumerateFiles(_root, "*.json").ToList())
            TryDelete(path);
    }

    private void Save(string gameKey)
    {
        try
        {
            Directory.CreateDirectory(_root);

            // Write to a sibling file first, then swap: a crash mid-write leaves
            // the previous cache intact instead of a truncated one.
            string path = PathFor(gameKey);
            string temp = path + ".tmp";
            File.WriteAllText(temp, JsonSerializer.Serialize(_entries[gameKey], JsonOptions));
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            // Caching is an optimisation; scanning again is always an option.
        }
    }

    private string PathFor(string gameKey)
    {
        // Guard against a malformed key escaping the cache folder.
        foreach (char invalid in Path.GetInvalidFileNameChars())
            gameKey = gameKey.Replace(invalid, '_');

        return Path.Combine(_root, gameKey + ".json");
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }
    }
}
