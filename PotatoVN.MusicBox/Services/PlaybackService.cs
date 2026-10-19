using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Dispatching;
using MusicBox.Core.Models;
using PotatoVN.MusicBox.Models;

namespace PotatoVN.MusicBox.Services;

public enum RepeatMode
{
    None,
    All,
    One,
}

/// <summary>
/// Queue + transport for the music player, backed by <see cref="AudioEngine"/>.
///
/// Tracks are opened lazily straight from their archive entry, so starting a
/// 300 MB XP3 costs one index read. Samples are decoded on demand while the
/// file is on disk; nothing is copied to a temp folder.
///
/// The service lives on the plugin, not on the page: navigating away from the
/// music box keeps the current song playing.
/// </summary>
public sealed partial class PlaybackService : ObservableObject, IDisposable
{
    private readonly AudioEngine _engine = new();
    private readonly DispatcherQueue _dispatcher;
    private readonly DispatcherQueueTimer _timer;
    private readonly MusicLibraryService _library;
    private readonly PluginData? _settings;
    private readonly Random _random = new();

    private readonly List<GameTrack> _queue = [];
    private readonly List<int> _order = [];
    private int _orderPosition = -1;
    private Stream? _currentStream;
    private int _failureStreak;
    private int _lastSavedSecond = -1;
    private bool _disposed;

    [ObservableProperty] private GameTrack? _currentTrack;
    [ObservableProperty] private bool _isPlaying;
    [ObservableProperty] private double _positionSeconds;
    [ObservableProperty] private double _durationSeconds;
    [ObservableProperty] private double _volume = 0.8;
    [ObservableProperty] private bool _isMuted;
    [ObservableProperty] private bool _shuffle;
    [ObservableProperty] private RepeatMode _repeatMode = RepeatMode.All;

    /// <summary>Raised whenever the title, play state or position changes (UI thread).</summary>
    public event EventHandler? StateChanged;

    /// <summary>Raised when a different track becomes current (UI thread).</summary>
    public event EventHandler? TrackChanged;

    public IReadOnlyList<GameTrack> Queue => _queue;
    public bool HasTrack => CurrentTrack is not null;

    public PlaybackService(MusicLibraryService library, PluginData? settings = null)
    {
        _library = library;
        _settings = settings;
        _dispatcher = DispatcherQueue.GetForCurrentThread()
                      ?? throw new InvalidOperationException("PlaybackService must be created on the UI thread.");

        _engine.PlaybackStopped += OnEngineStopped;

        _timer = _dispatcher.CreateTimer();
        _timer.Interval = TimeSpan.FromMilliseconds(250);
        _timer.Tick += (_, _) => UpdatePosition();
        _timer.Start();
    }

    // ---------------------------------------------------------------- queue

    /// <summary>Replaces the queue and starts at <paramref name="startIndex"/>.</summary>
    public void SetQueue(IEnumerable<GameTrack> tracks, int startIndex = 0)
    {
        _queue.Clear();
        _queue.AddRange(tracks);
        _failureStreak = 0;
        RebuildOrder();

        if (_queue.Count == 0)
        {
            Stop();
            return;
        }

        startIndex = Math.Clamp(startIndex, 0, _queue.Count - 1);
        _orderPosition = _order.IndexOf(startIndex);
        if (_orderPosition < 0) _orderPosition = 0;
        PlayCurrent();
    }

    public void EnqueueNext(GameTrack track)
    {
        int insertAt = _orderPosition + 1;
        _queue.Add(track);
        _order.Insert(Math.Min(insertAt, _order.Count), _queue.Count - 1);
    }

    public void PlayTrack(GameTrack track)
    {
        int index = _queue.IndexOf(track);
        if (index < 0)
        {
            SetQueue([track], 0);
            return;
        }

        _orderPosition = _order.IndexOf(index);
        if (_orderPosition < 0) _orderPosition = 0;
        PlayCurrent();
    }

    public void TogglePlayPause()
    {
        if (CurrentTrack is null) return;

        if (IsPlaying)
        {
            _engine.Pause();
            IsPlaying = false;
            SavePosition();
        }
        else if (PositionSeconds >= DurationSeconds - 0.5)
        {
            // The output drained: reload the track instead of poking a stopped
            // device, which is not guaranteed to resume.
            PlayCurrent();
            return;
        }
        else
        {
            _engine.Play();
            IsPlaying = true;
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void Next()
    {
        if (_queue.Count == 0) return;

        if (RepeatMode == RepeatMode.One)
        {
            Seek(0);
            _engine.Play();
            IsPlaying = true;
            StateChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        Advance(rewindAtEnd: RepeatMode == RepeatMode.All);
        PlayCurrent();
    }

    public void Previous()
    {
        if (_queue.Count == 0) return;

        // Standard behaviour: restart the track unless we are near its start.
        if (PositionSeconds > 3)
        {
            Seek(0);
            return;
        }

        if (_orderPosition > 0)
        {
            _orderPosition--;
        }
        else if (RepeatMode == RepeatMode.All)
        {
            _orderPosition = _order.Count - 1;
        }
        else
        {
            Seek(0);
            return;
        }

        PlayCurrent();
    }

    public void Seek(double seconds)
    {
        if (CurrentTrack is null) return;

        seconds = Math.Clamp(seconds, 0, Math.Max(DurationSeconds, 0));
        _engine.Seek(seconds);
        PositionSeconds = seconds;
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    public void SetShuffle(bool value)
    {
        if (Shuffle == value) return;
        Shuffle = value;
        RebuildOrder(keepCurrent: true);
    }

    public void SetRepeatMode(RepeatMode mode) => RepeatMode = mode;

    public void Stop()
    {
        _timer.Stop();
        _engine.Stop();
        DisposeStream();

        IsPlaying = false;
        CurrentTrack = null;
        PositionSeconds = 0;
        DurationSeconds = 0;
        _timer.Start();

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    // ------------------------------------------------------------- playback

    private void PlayCurrent()
    {
        if (_orderPosition < 0 || _orderPosition >= _order.Count) return;

        var track = _queue[_order[_orderPosition]];

        if (!OpenTrack(track))
        {
            // A broken entry should not stall the queue, but a fully broken
            // queue must not spin forever either. Stop once every slot in the
            // queue has failed, otherwise skip on.
            if (++_failureStreak >= _queue.Count)
            {
                _failureStreak = 0;
                Stop();
                return;
            }

            int previous = _orderPosition;
            Advance(rewindAtEnd: true);

            // Advance() wraps around; guard against landing on the same slot.
            if (_orderPosition == previous) { Stop(); return; }

            _dispatcher.TryEnqueue(PlayCurrent);
            return;
        }

        _failureStreak = 0;
        CurrentTrack = track;
        _settings?.ApplyLastTrack(track.Id, 0);
        TrackChanged?.Invoke(this, EventArgs.Empty);
        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Opens the track payload and starts it. Returns false when undecodable.</summary>
    private bool OpenTrack(GameTrack track)
    {
        _engine.Stop();
        DisposeStream();

        Stream? stream = null;
        try
        {
            stream = _library.OpenTrack(track);
            if (!_engine.Load(stream, track.FileName))
            {
                stream.Dispose();
                return false;
            }

            _currentStream = stream;

            double duration = _engine.DurationSeconds;
            if (duration <= 0) duration = track.DurationSeconds;
            if (duration <= 0) duration = 0;
            DurationSeconds = duration;

            if (track.DurationSeconds <= 0 && duration > 0) track.DurationSeconds = duration;

            PositionSeconds = 0;
            _lastSavedSecond = -1;
            _engine.Volume = (float)Math.Clamp(Volume, 0, 1);
            _engine.IsMuted = IsMuted;
            _engine.Play();
            IsPlaying = true;
            return true;
        }
        catch
        {
            stream?.Dispose();
            return false;
        }
    }

    private void Advance(bool rewindAtEnd)
    {
        if (_orderPosition < _order.Count - 1)
        {
            _orderPosition++;
        }
        else if (rewindAtEnd)
        {
            _orderPosition = 0;
            if (Shuffle) ReshuffleOrder();
        }
        else
        {
            _engine.Pause();
            Seek(0);
            IsPlaying = false;
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

    private void DisposeStream()
    {
        _currentStream?.Dispose();
        _currentStream = null;
    }

    // --------------------------------------------------------------- events

    /// <summary>
    /// Fires on the playback thread when the device drains. Stop()/track switches
    /// bump the engine generation, so those never reach here.
    /// </summary>
    private void OnEngineStopped(object? sender, EventArgs e)
    {
        _dispatcher.TryEnqueue(() =>
        {
            if (_disposed) return;

            if (RepeatMode == RepeatMode.One && CurrentTrack is not null)
            {
                // The device has drained; reload rather than resuming a stopped
                // output.
                PlayCurrent();
                return;
            }

            IsPlaying = false;

            if (RepeatMode == RepeatMode.None && _orderPosition >= _order.Count - 1)
            {
                // End of the queue: stay on the last track, rewound.
                Seek(0);
                StateChanged?.Invoke(this, EventArgs.Empty);
                return;
            }

            Advance(rewindAtEnd: RepeatMode == RepeatMode.All);
            PlayCurrent();
        });
    }

    private void UpdatePosition()
    {
        if (_disposed || CurrentTrack is null) return;

        double position = _engine.PositionSeconds;
        bool playing = _engine.IsPlaying;

        PositionSeconds = position;
        if (IsPlaying != playing)
        {
            IsPlaying = playing;
            if (!playing) SavePosition();
        }

        // Persist the resume point occasionally rather than on every tick.
        int second = (int)position;
        if (playing && second != _lastSavedSecond && second % 5 == 0)
        {
            _lastSavedSecond = second;
            _settings?.ApplyLastTrack(CurrentTrack.Id, position);
        }

        StateChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SavePosition()
    {
        if (CurrentTrack is not null)
            _settings?.ApplyLastTrack(CurrentTrack.Id, PositionSeconds);
    }

    // --------------------------------------------------------------- helpers

    private void RebuildOrder(bool keepCurrent = false)
    {
        int currentIndex = keepCurrent && _orderPosition >= 0 && _orderPosition < _order.Count
            ? _order[_orderPosition]
            : -1;

        _order.Clear();
        for (int i = 0; i < _queue.Count; i++) _order.Add(i);

        if (Shuffle)
        {
            for (int i = _order.Count - 1; i > 0; i--)
            {
                int j = _random.Next(i + 1);
                (_order[i], _order[j]) = (_order[j], _order[i]);
            }

            if (currentIndex >= 0)
            {
                int pos = _order.IndexOf(currentIndex);
                if (pos > 0) (_order[0], _order[pos]) = (_order[pos], _order[0]);
            }
        }

        if (currentIndex >= 0)
        {
            _orderPosition = _order.IndexOf(currentIndex);
            if (_orderPosition < 0) _orderPosition = 0;
        }
    }

    private void ReshuffleOrder()
    {
        var remaining = _order.Skip(_orderPosition + 1).ToList();
        for (int i = remaining.Count - 1; i > 0; i--)
        {
            int j = _random.Next(i + 1);
            (remaining[i], remaining[j]) = (remaining[j], remaining[i]);
        }

        _order.RemoveRange(_orderPosition + 1, _order.Count - _orderPosition - 1);
        _order.AddRange(remaining);
    }

    partial void OnVolumeChanged(double value)
    {
        _engine.Volume = (float)Math.Clamp(value, 0, 1);
        if (value > 0 && IsMuted) IsMuted = false;
        if (_settings is not null) _settings.Volume = value;
    }

    partial void OnIsMutedChanged(bool value) => _engine.IsMuted = value;

    partial void OnShuffleChanged(bool value)
    {
        if (_settings is not null) _settings.Shuffle = value;
    }

    partial void OnRepeatModeChanged(RepeatMode value)
    {
        if (_settings is not null) _settings.RepeatMode = value.ToStorage();
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        SavePosition();
        _timer.Stop();
        _engine.PlaybackStopped -= OnEngineStopped;
        _engine.Dispose();
        DisposeStream();
    }
}

internal static class RepeatModeExtensions
{
    public static string ToStorage(this RepeatMode mode) => mode switch
    {
        RepeatMode.None => "none",
        RepeatMode.One => "one",
        _ => "all",
    };
}
