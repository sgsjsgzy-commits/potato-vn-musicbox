using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using GalgameManager.WinApp.Base.Contracts;
using GalgameManager.WinApp.Base.Contracts.PluginUi;
using GalgameManager.WinApp.Base.Models;
using Microsoft.UI.Dispatching;
using PotatoVN.MusicBox.Models;
using PotatoVN.MusicBox.Services;

namespace PotatoVN.MusicBox;

/// <summary>
/// PotatoVN MusicBox: browse and play the music shipped inside installed games
/// without launching them. The plugin registers a sidebar entry that opens a
/// dedicated player page, and keeps its own scan cache so reopening is instant.
/// </summary>
public partial class Plugin : IPlugin, IPluginSetting
{
    public static IPotatoVnApi HostApi { get; private set; } = null!;

    /// <summary>
    /// The single plugin instance, exposed for pages/controls the host creates
    /// by type name (they cannot receive constructor arguments).
    /// </summary>
    public static Plugin Current { get; private set; } = null!;

    private IPotatoVnApi _hostApi = null!;
    private PluginData _data = new();

    public MusicLibraryService Library { get; private set; } = null!;

    /// <summary>
    /// Player shared by every page instance, so navigating away from the music
    /// box (or reopening it) keeps the current song going. Created on first
    /// access, which must happen on the UI thread (the progress timer needs a
    /// dispatcher queue).
    /// </summary>
    public PlaybackService Playback => _playback ??= new PlaybackService(Library, _data);
    private PlaybackService? _playback;

    /// <summary>Persisted settings shared with the player page and settings UI.</summary>
    public PluginData Settings => _data;

    public PluginInfo Info { get; } = new()
    {
        Id = new Guid("43537402-ca22-49ac-a82d-b23af37b65d8"),
        Name = "音乐盒",
        Description = "无需启动游戏，直接浏览并播放游戏自带的 BGM。\n支持 XP3、LINK、YPF、RPA、BGI、NSA 等常见 Galgame 归档格式。",
    };

    public async Task InitializeAsync(IPotatoVnApi hostApi)
    {
        _hostApi = hostApi;
        HostApi = hostApi;
        Current = this;

        // Required before any XAML control is constructed: the plugin lives in
        // the host's plugin folder, not at the ms-appx root.
        XamlResourceLocatorFactory.PackagePath = hostApi.GetPluginPath();

        await LoadDataAsync();

        var cache = CreateCacheStore();
        Library = new MusicLibraryService(_data, cache);
        MigrateLegacyCache(cache);

        _data.PropertyChanged += (_, _) => SaveData();

        InitUi();
    }

    public Task OnUninstallAsync(bool deleteData, Action<TimeSpan> extendWaitHandler, CancellationToken cts)
    {
        // Nothing global to release: the player page owns its own media element.
        return Task.CompletedTask;
    }

    private async Task LoadDataAsync()
    {
        try
        {
            var json = await _hostApi.GetDataAsync();
            if (!string.IsNullOrWhiteSpace(json))
            {
                var loaded = System.Text.Json.JsonSerializer.Deserialize<PluginData>(json);
                if (loaded is not null) _data = Migrate(loaded);
            }
        }
        catch (Exception e)
        {
            _hostApi.DeveloperEvent(msg: $"音乐盒：读取插件数据失败，已使用默认设置。{e.Message}");
            _data = new PluginData();
        }
    }

    private static PluginData Migrate(PluginData data)
    {
        // Future schema migrations branch on data.Version here.
        data.Version = PluginData.CurrentVersion;
        data.Libraries ??= new();
        return data;
    }

    /// <summary>
    /// Creates the cache store at the configured folder, or next to the first
    /// local game so a big library stays off the system drive.
    /// </summary>
    private LibraryCacheStore CreateCacheStore()
    {
        string? configured = _data.CacheFolder;
        if (!string.IsNullOrWhiteSpace(configured))
            return new LibraryCacheStore(configured);

        var gamePaths = HostApi.GetAllGames()
            .Where(g => g.IsLocalGame && !string.IsNullOrWhiteSpace(g.LocalPath))
            .Select(g => g.LocalPath!)
            .ToList();


        return new LibraryCacheStore(LibraryCacheStore.DefaultRoot(gamePaths));
    }

    /// <summary>
    /// Version 1 stored scan results inside the plugin data blob. Move them to
    /// the new per-game files once, then drop them from the blob so saving the
    /// play position stays cheap.
    /// </summary>
    private void MigrateLegacyCache(LibraryCacheStore cache)
    {
        if (_data.Libraries.Count == 0) return;

        foreach (var (key, cached) in _data.Libraries)
        {
            if (!cache.TryGet(key, out _)) cache.Set(key, cached);
        }

        _data.Libraries.Clear();
        SaveData();
    }

    private DispatcherQueueTimer? _saveTimer;

    /// <summary>
    /// Coalesces writes: playback position changes every few seconds, and the
    /// scan cache can be megabytes, so serialising on every change would stall
    /// the UI thread.
    /// </summary>
    private void SaveData()
    {
        if (_saveTimer is null)
        {
            var queue = DispatcherQueue.GetForCurrentThread();
            if (queue is null)
            {
                SaveDataNow();
                return;
            }

            _saveTimer = queue.CreateTimer();
            _saveTimer.Interval = TimeSpan.FromSeconds(2);
            _saveTimer.IsRepeating = false;
            _saveTimer.Tick += (_, _) => SaveDataNow();
        }

        _saveTimer.Stop();
        _saveTimer.Start();
    }

    private void SaveDataNow()
    {
        try
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_data);
            _ = _hostApi.SaveDataAsync(json);
        }
        catch (Exception e)
        {
            _hostApi.DeveloperEvent(msg: $"音乐盒：保存插件数据失败。{e.Message}");
        }
    }

    protected Guid Id => Info.Id;
}
