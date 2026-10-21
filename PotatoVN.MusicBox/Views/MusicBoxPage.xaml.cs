using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using CommunityToolkit.Mvvm.ComponentModel;
using GalgameManager.Models;
using GalgameManager.WinApp.Base.Models.Plugin;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using Microsoft.UI.Xaml.Navigation;
using MusicBox.Core.Models;
using PotatoVN.MusicBox.Services;

namespace PotatoVN.MusicBox.Views;

/// <summary>
/// The music box page: pick a game on the left, browse its extracted soundtrack
/// on the right, and control playback from the bar at the bottom.
/// </summary>
public sealed partial class MusicBoxPage : Page
{
    private readonly ObservableCollection<GameItem> _games = [];
    private readonly ObservableCollection<TrackItem> _tracks = [];
    private readonly List<TrackItem> _allTracks = [];

    private PlaybackService _playback = null!;
    private GameMusicLibrary? _library;
    private Controls.PlayerBar? _playerBar;
    private GameItem? _selectedGame;
    private CancellationTokenSource? _scanCts;
    private string _trackFilter = string.Empty;
    private TrackKind? _kindFilter;

    public MusicBoxPage()
    {
        XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);

        GameList.ItemsSource = _games;
        TrackList.ItemsSource = _tracks;

        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        ActualThemeChanged += (_, _) => ApplyHeroTheme();
    }

    /// <summary>Hero wash is brighter in light theme, darker in dark theme.</summary>
    private void ApplyHeroTheme()
    {
        bool dark = ActualTheme == ElementTheme.Dark;
        HeroStopTop.Color = dark
            ? Windows.UI.Color.FromArgb(0x66, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0x55, 0xFF, 0xFF, 0xFF);
        HeroStopMid.Color = dark
            ? Windows.UI.Color.FromArgb(0xB0, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF);
        HeroStopBottom.Color = dark
            ? Windows.UI.Color.FromArgb(0xD8, 0x00, 0x00, 0x00)
            : Windows.UI.Color.FromArgb(0xF7, 0xFF, 0xFF, 0xFF);
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        // The player belongs to the plugin, not the page: playback survives
        // navigation, and reopening the page just re-attaches the transport bar.
        _playback = Plugin.Current.Playback;

        if (_playerBar is null)
        {
            _playerBar = new Controls.PlayerBar { Playback = _playback };
            _playerBar.QueueRequested += (_, _) => ShowQueue();
            PlayerHost.Content = _playerBar;
        }
        else
        {
            _playerBar.Playback = _playback;
        }

        _playback.Volume = Plugin.Current.Settings.Volume;
        _playback.IsMuted = false;
        _playback.SetShuffle(Plugin.Current.Settings.Shuffle);
        _playback.SetRepeatMode(ParseRepeat(Plugin.Current.Settings.RepeatMode));
        _playback.TrackChanged += OnTrackChanged;

        PopulateGames();
        UpdateNowPlayingMarker();
        ApplyHeroTheme();

        if (_playerBar is not null) _playerBar.Cover = _selectedGame?.Cover;
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _scanCts?.Cancel();
        _playback.TrackChanged -= OnTrackChanged;

        // Detach the bar but leave the audio engine running.
        if (_playerBar is not null) _playerBar.Playback = null;
    }

    private void OnTrackChanged(object? sender, EventArgs e)
    {
        UpdateNowPlayingMarker();

        if (_playerBar is not null)
            _playerBar.Cover = _selectedGame?.Cover;
    }

    /// <summary>Moves the now-playing indicator onto the current row.</summary>
    private void UpdateNowPlayingMarker()
    {
        var current = _playback.CurrentTrack;

        foreach (var item in _allTracks)
            item.IsCurrent = current is not null && ReferenceEquals(item.Track, current);

        if (current is not null && _tracks.FirstOrDefault(t => t.IsCurrent) is { } row)
            TrackList.ScrollIntoView(row);
    }

    private static RepeatMode ParseRepeat(string value) => value switch
    {
        "none" => RepeatMode.None,
        "one" => RepeatMode.One,
        _ => RepeatMode.All,
    };

    private static string FormatRepeat(RepeatMode mode) => mode switch
    {
        RepeatMode.None => "none",
        RepeatMode.One => "one",
        _ => "all",
    };

    // ------------------------------------------------------------ game list

    private void PopulateGames()
    {
        _games.Clear();

        var games = Plugin.HostApi.GetAllGames()
            .Where(g => g.IsLocalGame && !string.IsNullOrWhiteSpace(g.LocalPath))
            .OrderBy(g => g.Name.Value, StringComparer.CurrentCultureIgnoreCase);

        foreach (var game in games)
        {
            _games.Add(new GameItem
            {
                Game = game,
                Name = game.Name.Value ?? "未命名游戏",
                Subtitle = game.Developer.Value ?? string.Empty,
                Cover = LoadCover(game),
                TrackCountText = Plugin.Current.Library.TryGetCached(MusicLibraryService.MakeGameKey(game.Uuid), out var cached)
                    ? $"{cached.Bgm.Count()} 首"
                    : string.Empty,
            });
        }

        EmptyState.Visibility = _games.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (_games.Count == 0)
        {
            EmptyTitle.Text = "游戏库为空";
            EmptyDetail.Text = "请先在 PotatoVN 中添加本地游戏，音乐盒会从安装目录中提取 BGM。";
            return;
        }

        // Restore the previously selected game when possible.
        var last = _games.FirstOrDefault(g =>
            string.Equals(g.Game.Uuid.ToString("N"), Plugin.Current.Settings.LastGameId, StringComparison.OrdinalIgnoreCase));
        if (last is not null)
        {
            GameList.SelectedItem = last;
            _ = SelectGameAsync(last);
        }
    }

    private static ImageSource? LoadCover(Galgame game)
    {
        try
        {
            var path = game.ImagePath.Value;
            if (string.IsNullOrEmpty(path) || path.StartsWith("ms-appx:", StringComparison.OrdinalIgnoreCase))
                return null;

            return new BitmapImage(new Uri(path));
        }
        catch
        {
            return null;
        }
    }

    private void GameSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;

        string query = sender.Text.Trim();
        var filtered = string.IsNullOrEmpty(query)
            ? _games.ToList()
            : _games.Where(g => g.Name.Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToList();

        GameList.ItemsSource = filtered;
    }

    private async void GameList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is GameItem item) await SelectGameAsync(item);
    }

    private async Task SelectGameAsync(GameItem item)
    {
        _selectedGame = item;
        GameList.SelectedItem = item;
        Plugin.Current.Settings.LastGameId = item.Game.Uuid.ToString("N");

        AlbumTitle.Text = item.Name;
        AlbumSubtitle.Text = item.Game.LocalPath ?? string.Empty;
        AlbumCover.Source = item.Cover;
        AlbumCoverPlaceholder.Visibility = item.Cover is null ? Visibility.Visible : Visibility.Collapsed;
        HeroBackdrop.Source = item.Cover;

        if (_playerBar is not null && _playback.CurrentTrack is not null)
            _playerBar.Cover = item.Cover;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        var token = _scanCts.Token;

        string key = MusicLibraryService.MakeGameKey(item.Game.Uuid);
        bool hasCache = Plugin.Current.Library.TryGetCached(key, out var cached);

        if (hasCache && !Plugin.Current.Library.IsStale(key, item.Game.LocalPath!))
        {
            ApplyLibrary(cached);
            return;
        }

        // Show cached content immediately, then refresh in the background.
        if (hasCache) ApplyLibrary(cached);

        await ScanGameAsync(item, token);
    }

    private async Task ScanGameAsync(GameItem item, CancellationToken token)
    {
        SetScanning(true, hasCache: _library is not null);

        try
        {
            var progress = new Progress<string>(message => EmptyDetail.Text = message);
            var library = await Plugin.Current.Library.ScanAsync(
                item.Game.Uuid,
                item.Name,
                item.Game.LocalPath!,
                progress,
                token);

            if (token.IsCancellationRequested) return;

            ApplyLibrary(library);
            item.TrackCountText = $"{library.Bgm.Count()} 首";
        }
        catch (OperationCanceledException)
        {
            // A new game was selected while scanning.
        }
        catch (Exception ex)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = "扫描失败";
            EmptyDetail.Text = ex.Message;
        }
        finally
        {
            SetScanning(false, hasCache: _library is not null);
        }
    }

    private void SetScanning(bool scanning, bool hasCache)
    {
        ScanProgress.IsActive = scanning;
        RescanButton.IsEnabled = !scanning;

        if (!hasCache)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = scanning ? "正在扫描游戏目录…" : "还没有音乐";
            if (scanning) EmptyDetail.Text = "正在解析归档文件…";
        }
    }

    private void ApplyLibrary(GameMusicLibrary library)
    {
        _library = library;
        _allTracks.Clear();

        int index = 1;
        foreach (var track in library.Tracks)
        {
            _allTracks.Add(new TrackItem(track, index++));
        }

        ApplyTrackFilter();

        if (library.Tracks.Count == 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = "没有找到可播放的音乐";
            EmptyDetail.Text = library.Warnings.Count > 0
                ? string.Join("\n", library.Warnings.Take(6))
                : "该游戏的音频可能被加密，或全部位于不支持的归档格式中。";
        }
        else
        {
            EmptyState.Visibility = Visibility.Collapsed;
        }

        if (library.Warnings.Count > 0)
        {
            WarningBar.Message = string.Join("\n", library.Warnings.Take(8));
            WarningBar.IsOpen = true;
        }
        else
        {
            WarningBar.IsOpen = false;
        }

        AlbumSubtitle.Text = $"{library.Bgm.Count()} 首 BGM · {library.Tracks.Count} 首曲目 · {library.ScanDuration.TotalSeconds:F1}s";
    }

    private void ApplyTrackFilter()
    {
        _tracks.Clear();

        IEnumerable<TrackItem> filtered = _allTracks;

        if (_kindFilter is { } kind)
            filtered = filtered.Where(t => t.Track.Kind == kind);

        if (!string.IsNullOrWhiteSpace(_trackFilter))
        {
            string query = _trackFilter.ToLowerInvariant();
            filtered = filtered.Where(t =>
                t.Track.SearchText.Contains(query, StringComparison.OrdinalIgnoreCase)
                || t.Title.Contains(query, StringComparison.CurrentCultureIgnoreCase));
        }

        foreach (var item in filtered) _tracks.Add(item);

        if (_tracks.Count == 0 && _allTracks.Count > 0)
        {
            EmptyState.Visibility = Visibility.Visible;
            EmptyTitle.Text = "没有匹配的曲目";
            EmptyDetail.Text = "试试清空搜索或切换筛选条件。";
        }
        else if (_tracks.Count > 0)
        {
            EmptyState.Visibility = Visibility.Collapsed;
        }
    }

    private void TrackSearchBox_TextChanged(AutoSuggestBox sender, AutoSuggestBoxTextChangedEventArgs args)
    {
        if (args.Reason != AutoSuggestionBoxTextChangeReason.UserInput) return;
        _trackFilter = sender.Text.Trim();
        ApplyTrackFilter();
    }

    private void KindFilter_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (KindFilter.SelectedItem is not ComboBoxItem item) return;

        _kindFilter = item.Tag switch
        {
            "bgm" => TrackKind.Bgm,
            "voice" => TrackKind.Voice,
            "se" => TrackKind.SoundEffect,
            _ => null,
        };

        ApplyTrackFilter();
    }

    // -------------------------------------------------------------- playback

    private void TrackList_ItemClick(object sender, ItemClickEventArgs e)
    {
        if (e.ClickedItem is not TrackItem item) return;

        var queue = _tracks.Select(t => t.Track).ToList();
        int index = _tracks.IndexOf(item);
        _playback.SetQueue(queue, Math.Max(index, 0));
    }

    private void PlayAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count == 0) return;

        _playback.SetShuffle(false);
        _playback.SetQueue(_tracks.Select(t => t.Track), 0);
    }

    private void ShuffleAllButton_Click(object sender, RoutedEventArgs e)
    {
        if (_tracks.Count == 0) return;

        _playback.SetShuffle(true);
        _playback.SetQueue(_tracks.Select(t => t.Track), Random.Shared.Next(_tracks.Count));
    }

    private async void RescanButton_Click(object sender, RoutedEventArgs e)
    {
        if (_selectedGame is null) return;

        _scanCts?.Cancel();
        _scanCts = new CancellationTokenSource();
        await ScanGameAsync(_selectedGame, _scanCts.Token);
    }

    private async void ShowQueue()
    {
        if (_playback.Queue.Count == 0) return;

        var list = new ListView
        {
            ItemsSource = _playback.Queue.Select((t, i) => $"{i + 1}. {t.Title}").ToList(),
            SelectionMode = ListViewSelectionMode.None,
        };

        var dialog = new ContentDialog
        {
            Title = "播放队列",
            CloseButtonText = "关闭",
            Content = new ScrollViewer { Content = list, MaxHeight = 420 },
            XamlRoot = XamlRoot,
            RequestedTheme = RequestedTheme,
        };

        dialog.Resources["ContentDialogMinWidth"] = 480d;
        dialog.Resources["ContentDialogMaxWidth"] = 560d;
        await dialog.ShowAsync();
    }
}

/// <summary>Row model for the game rail.</summary>
public sealed partial class GameItem : ObservableObject
{
    public Galgame Game { get; init; } = null!;
    [ObservableProperty] private string _name = string.Empty;
    [ObservableProperty] private string _subtitle = string.Empty;
    [ObservableProperty] private string _trackCountText = string.Empty;
    public ImageSource? Cover { get; init; }
}

/// <summary>Row model for the track list.</summary>
public sealed partial class TrackItem : ObservableObject
{
    public TrackItem(GameTrack track, int index)
    {
        Track = track;
        IndexText = index.ToString();
    }

    public GameTrack Track { get; }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(IndexVisibility))]
    [NotifyPropertyChangedFor(nameof(PlayingVisibility))]
    [NotifyPropertyChangedFor(nameof(TitleWeight))]
    private bool _isCurrent;

    public string IndexText { get; }
    public string Title => Track.Title;
    public string Container => Track.Container;

    public Visibility IndexVisibility => IsCurrent ? Visibility.Collapsed : Visibility.Visible;
    public Visibility PlayingVisibility => IsCurrent ? Visibility.Visible : Visibility.Collapsed;
    public string TitleWeight => IsCurrent ? "SemiBold" : "Normal";

    public string Subtitle => string.IsNullOrEmpty(Track.ArchiveName)
        ? Track.Album
        : $"{Track.Album} · {Track.ArchiveName}";

    public string Duration => Track.DurationSeconds > 0
        ? TimeSpan.FromSeconds(Track.DurationSeconds).ToString(@"m\:ss")
        : "--:--";
}
