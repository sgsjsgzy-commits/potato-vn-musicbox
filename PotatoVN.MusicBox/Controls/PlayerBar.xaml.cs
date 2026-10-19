using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using PotatoVN.MusicBox.Services;

namespace PotatoVN.MusicBox.Controls;

/// <summary>
/// Bottom transport bar. Binds itself to a <see cref="PlaybackService"/> and
/// stays in sync through its <c>StateChanged</c> event.
/// </summary>
public sealed partial class PlayerBar : UserControl
{
    // Segoe Fluent Icons codepoints.
    private const string GlyphPlay = "\uE768";
    private const string GlyphPause = "\uE769";
    private const string GlyphVolumeMute = "\uE74F";
    private const string GlyphVolumeLow = "\uE992";
    private const string GlyphVolumeHigh = "\uE993";
    private const string GlyphRepeatAll = "\uE8EE";
    private const string GlyphRepeatOne = "\uE8ED";

    private PlaybackService? _playback;
    private bool _updatingSlider;
    private bool _updatingVolume;
    private bool _seeking;

    public event EventHandler? QueueRequested;

    public PlayerBar()
    {
        XamlResourceLocatorFactory.PluginControlInit(ref _contentLoaded, this);
        Unloaded += (_, _) => Detach();
        RepeatIcon.Glyph = GlyphRepeatAll;
        PlayPauseIcon.Glyph = GlyphPlay;
    }

    public PlaybackService? Playback
    {
        get => _playback;
        set
        {
            Detach();
            _playback = value;
            Attach();
        }
    }

    /// <summary>Artwork for the now-playing block; falls back to the music icon when null.</summary>
    public ImageSource? Cover
    {
        get => CoverImage.Source;
        set
        {
            CoverImage.Source = value;
            CoverPlaceholder.Visibility = value is null ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void Attach()
    {
        if (_playback is null) return;

        _playback.StateChanged += OnStateChanged;

        _updatingVolume = true;
        VolumeSlider.Value = _playback.Volume;
        _updatingVolume = false;

        UpdateShuffleVisual();
        UpdateRepeatVisual();
        Refresh();
    }

    private void Detach()
    {
        if (_playback is not null) _playback.StateChanged -= OnStateChanged;
    }

    private void OnStateChanged(object? sender, EventArgs e) => Refresh();

    private void Refresh()
    {
        if (_playback is null) return;

        var track = _playback.CurrentTrack;
        if (track is null)
        {
            TitleText.Text = "未在播放";
            SubtitleText.Text = "选择一首曲目开始";
            AlbumText.Text = string.Empty;
            Cover = null;
            PlayPauseIcon.Glyph = GlyphPlay;
            PositionText.Text = "0:00";
            DurationText.Text = "0:00";
            return;
        }

        TitleText.Text = track.Title;
        SubtitleText.Text = string.IsNullOrEmpty(track.ArchiveName)
            ? track.Container
            : $"{track.ArchiveName} · {track.Container}";
        AlbumText.Text = track.Album;

        if (CoverImage.Source is null) CoverPlaceholder.Visibility = Visibility.Visible;

        PlayPauseIcon.Glyph = _playback.IsPlaying ? GlyphPause : GlyphPlay;

        // While the user drags the thumb, the timer must not fight it.
        if (!_seeking)
        {
            _updatingSlider = true;
            PositionSlider.Maximum = Math.Max(_playback.DurationSeconds, 1);
            PositionSlider.Value = Math.Clamp(_playback.PositionSeconds, 0, PositionSlider.Maximum);
            _updatingSlider = false;
        }

        PositionText.Text = FormatTime(_seeking ? PositionSlider.Value : _playback.PositionSeconds);
        DurationText.Text = FormatTime(_playback.DurationSeconds);

        VolumeIcon.Glyph = _playback.IsMuted || _playback.Volume <= 0
            ? GlyphVolumeMute
            : _playback.Volume < 0.4 ? GlyphVolumeLow : GlyphVolumeHigh;
    }

    private static string FormatTime(double seconds)
    {
        if (double.IsNaN(seconds) || seconds <= 0) return "0:00";
        var time = TimeSpan.FromSeconds(seconds);
        return time.TotalHours >= 1
            ? $"{(int)time.TotalHours}:{time.Minutes:00}:{time.Seconds:00}"
            : $"{time.Minutes}:{time.Seconds:00}";
    }

    private void UpdateShuffleVisual()
    {
        if (_playback is null) return;
        ShuffleIcon.Foreground = _playback.Shuffle
            ? (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"]
            : (Brush)Application.Current.Resources["TextFillColorPrimaryBrush"];
    }

    private void UpdateRepeatVisual()
    {
        if (_playback is null) return;

        RepeatIcon.Glyph = _playback.RepeatMode == RepeatMode.One ? GlyphRepeatOne : GlyphRepeatAll;
        RepeatIcon.Foreground = _playback.RepeatMode == RepeatMode.None
            ? (Brush)Application.Current.Resources["TextFillColorTertiaryBrush"]
            : (Brush)Application.Current.Resources["AccentTextFillColorPrimaryBrush"];

        ToolTipService.SetToolTip(RepeatButton, _playback.RepeatMode switch
        {
            RepeatMode.None => "不循环",
            RepeatMode.All => "列表循环",
            _ => "单曲循环",
        });
    }

    private void PlayPauseButton_Click(object sender, RoutedEventArgs e) => _playback?.TogglePlayPause();

    private void PreviousButton_Click(object sender, RoutedEventArgs e) => _playback?.Previous();

    private void NextButton_Click(object sender, RoutedEventArgs e) => _playback?.Next();

    private void PositionSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingSlider || _playback is null) return;

        // Seek live while dragging; the engine clamps and keeps buffering happy.
        _seeking = true;
        PositionText.Text = FormatTime(e.NewValue);
        _playback.Seek(e.NewValue);
        _seeking = false;
    }

    private void VolumeSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
    {
        if (_updatingVolume || _playback is null) return;

        // Raising the slider clears mute (handled by the service); dropping it
        // to zero is just silence, not a mute toggle.
        _playback.Volume = e.NewValue;
        if (e.NewValue > 0) _playback.IsMuted = false;
        Refresh();
    }

    private void MuteButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playback is null) return;
        _playback.IsMuted = !_playback.IsMuted;
        Refresh();
    }

    private void ShuffleButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playback is null) return;
        _playback.SetShuffle(!_playback.Shuffle);
        UpdateShuffleVisual();
    }

    private void RepeatButton_Click(object sender, RoutedEventArgs e)
    {
        if (_playback is null) return;

        _playback.SetRepeatMode(_playback.RepeatMode switch
        {
            RepeatMode.None => RepeatMode.All,
            RepeatMode.All => RepeatMode.One,
            _ => RepeatMode.None,
        });

        UpdateRepeatVisual();
    }

    private void QueueButton_Click(object sender, RoutedEventArgs e) =>
        QueueRequested?.Invoke(this, EventArgs.Empty);
}
