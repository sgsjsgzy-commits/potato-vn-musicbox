using System;
using System.IO;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using NVorbis;

namespace PotatoVN.MusicBox.Services;

/// <summary>
/// Audio backend for the player.
///
/// The decoder is picked per container instead of relying on one system codec
/// set:
///  - Ogg Vorbis: NVorbis (managed). Stock Windows ships no Vorbis decoder and
///    Media Foundation rejects it with 0xC00D36C4 SourceNotSupported.
///  - MP3/WAV: NAudio's frame readers (no system codec needed for PCM; MP3 uses
///    the ACM decoder present on every Windows install).
///  - FLAC/M4A/AAC/WMA: Media Foundation, which handles these natively on
///    Windows 10 and later.
///
/// Volume is applied in software, so it behaves identically on every device.
/// </summary>
public sealed class AudioEngine : IDisposable
{
    private WaveOutEvent? _output;
    private WaveStream? _reader;
    private VolumeSampleProvider? _volumeProvider;
    private readonly object _sync = new();
    private float _volume = 0.8f;
    private bool _muted;
    private volatile bool _disposed;

    /// <summary>
    /// Bumped on every stop we initiate. The handler captured at Load time only
    /// forwards the event while its generation is still current, so our own
    /// Stop()/track switch never looks like a natural track end.
    ///
    /// Deliberately read without taking <see cref="_sync"/>: the callback runs on
    /// the playback thread, and Stop() waits for that thread while holding the
    /// lock, so locking here would deadlock.
    /// </summary>
    private volatile int _generation;

    /// <summary>Raised when a track drains naturally. Fires on a background thread.</summary>
    public event EventHandler? PlaybackStopped;

    public bool IsPlaying
    {
        get
        {
            lock (_sync) return _output?.PlaybackState == PlaybackState.Playing;
        }
    }

    public double PositionSeconds
    {
        get
        {
            lock (_sync) return _reader?.CurrentTime.TotalSeconds ?? 0;
        }
    }

    public double DurationSeconds
    {
        get
        {
            lock (_sync) return _reader?.TotalTime.TotalSeconds ?? 0;
        }
    }

    public float Volume
    {
        get => _volume;
        set
        {
            _volume = Math.Clamp(value, 0f, 1f);
            ApplyVolume();
        }
    }

    public bool IsMuted
    {
        get => _muted;
        set
        {
            _muted = value;
            ApplyVolume();
        }
    }

    /// <summary>
    /// Loads a track from an already-open stream (archive entry or local file).
    /// Returns false when the container/codec is unsupported.
    /// </summary>
    public bool Load(Stream stream, string fileNameHint)
    {
        // Tear down the previous track outside the lock (Stop joins the playback
        // thread), then build the new decoder without holding it either: probing
        // an Ogg header or an archive slice can take tens of milliseconds.
        TakeOver();

        WaveStream? reader = null;
        WaveOutEvent? output = null;
        try
        {
            reader = CreateReader(stream, fileNameHint);
            if (reader is null) return false;

            var volumeProvider = new VolumeSampleProvider(ToSampleProvider(reader));

            // Opening the output device can block; do it outside the lock.
            output = new WaveOutEvent { DesiredLatency = 200 };
            output.Init(volumeProvider);

            lock (_sync)
            {
                _generation++;
                int generation = _generation;
                output.PlaybackStopped += (_, _) =>
                {
                    if (_disposed || generation != _generation) return;
                    PlaybackStopped?.Invoke(this, EventArgs.Empty);
                };

                _reader = reader;
                _volumeProvider = volumeProvider;
                _output = output;
                ApplyVolume();
            }

            // Ownership transferred to the engine: do not dispose them below.
            reader = null;
            output = null;
            return true;
        }
        catch
        {
            reader?.Dispose();
            output?.Dispose();
            return false;
        }
    }

    private static WaveStream? CreateReader(Stream stream, string fileNameHint)
    {
        string extension = Path.GetExtension(fileNameHint).ToLowerInvariant();

        WaveStream? reader = extension switch
        {
            ".ogg" => TryCreateVorbis(stream) ?? TryCreateMediaFoundation(stream),
            ".opus" => TryCreateVorbis(stream) ?? TryCreateMediaFoundation(stream),
            ".mp3" => TryCreateMp3(stream) ?? TryCreateMediaFoundation(stream),
            ".wav" => TryCreateWav(stream) ?? TryCreateMediaFoundation(stream),
            ".flac" or ".m4a" or ".aac" or ".wma" or ".aif" or ".aiff" => TryCreateMediaFoundation(stream),
            _ => null,
        };

        return reader ?? TryCreateBySignature(stream);
    }

    /// <summary>Reads the container magic when the file name has no useful extension.</summary>
    private static WaveStream? TryCreateBySignature(Stream stream)
    {
        try
        {
            long origin = stream.Position;
            Span<byte> head = stackalloc byte[4];
            stream.ReadExactly(head);
            stream.Position = origin;

            if (head.SequenceEqual("OggS"u8)) return TryCreateVorbis(stream);
            if (head.SequenceEqual("fLaC"u8)) return TryCreateMediaFoundation(stream);
            if (head.SequenceEqual("RIFF"u8)) return TryCreateWav(stream);
            return TryCreateMediaFoundation(stream);
        }
        catch
        {
            return null;
        }
    }

    private static WaveStream? TryCreateVorbis(Stream stream)
    {
        try
        {
            // NVorbis decodes but does not own the stream; the caller disposes it.
            return new OggVorbisWaveStream(new VorbisReader(stream, closeOnDispose: false));
        }
        catch
        {
            return null;
        }
    }

    private static WaveStream? TryCreateMp3(Stream stream)
    {
        try
        {
            // BytesToFrames uses the ACM decoder that ships with Windows, so no
            // extra native library is needed. The source stream stays open.
            return new Mp3FileReaderBase(stream, frame => new AcmMp3FrameDecompressor(frame));
        }
        catch
        {
            return null;
        }
    }

    private static WaveStream? TryCreateWav(Stream stream)
    {
        try
        {
            return new WaveFileReader(stream);
        }
        catch
        {
            return null;
        }
    }

    private static WaveStream? TryCreateMediaFoundation(Stream stream)
    {
        try
        {
            return new StreamMediaFoundationReader(
                stream,
                new MediaFoundationReader.MediaFoundationReaderSettings { RepositionInRead = true });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Normalises the reader into an IEEE-float sample provider so the software
    /// volume stage can sit on top regardless of the source bit depth.
    /// </summary>
    private static ISampleProvider ToSampleProvider(WaveStream reader) =>
        reader.WaveFormat.Encoding == WaveFormatEncoding.IeeeFloat
            ? reader.ToSampleProvider()
            : new WaveToSampleProvider(reader);

    public void Play()
    {
        lock (_sync) _output?.Play();
    }

    public void Pause()
    {
        lock (_sync) _output?.Pause();
    }

    public void Seek(double seconds)
    {
        WaveStream? reader;
        lock (_sync) reader = _reader;
        if (reader is null) return;

        // Seeking an Ogg stream re-reads page headers from the archive; doing it
        // under the lock would block the playback thread's volume updates.
        double total = reader.TotalTime.TotalSeconds;
        double clamped = Math.Clamp(seconds, 0, Math.Max(total - 0.05, 0));
        reader.CurrentTime = TimeSpan.FromSeconds(clamped);
    }

    public void Stop()
    {
        // Never call WaveOut.Stop() while holding _sync: it joins the playback
        // thread, and that thread may be inside ApplyVolume/PositionSeconds.
        TakeOver();
    }

    /// <summary>
    /// Detaches the current output and reader under the lock, then disposes them
    /// outside it so the playback thread is never blocked on our lock.
    /// </summary>
    private void TakeOver()
    {
        WaveOutEvent? output;
        WaveStream? reader;

        lock (_sync)
        {
            _generation++;
            output = _output;
            reader = _reader;
            _output = null;
            _volumeProvider = null;
            _reader = null;
        }

        try
        {
            output?.Stop();
        }
        catch
        {
            // The device may already be gone (unplugged headset, device change).
        }

        output?.Dispose();
        reader?.Dispose();
    }


    private void ApplyVolume()
    {
        lock (_sync)
        {
            if (_volumeProvider is null) return;
            _volumeProvider.Volume = _muted ? 0f : _volume;
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        TakeOver();
    }
}

/// <summary>
/// NAudio WaveStream adapter over NVorbis, so Ogg Vorbis plays without any
/// system codec. Handles seeking and reports an accurate duration.
/// </summary>
internal sealed class OggVorbisWaveStream : WaveStream
{
    private readonly VorbisReader _reader;
    private readonly WaveFormat _waveFormat;
    private float[] _sampleBuffer = [];

    public OggVorbisWaveStream(VorbisReader reader)
    {
        _reader = reader;
        _waveFormat = WaveFormat.CreateIeeeFloatWaveFormat(reader.SampleRate, reader.Channels);
    }

    public override WaveFormat WaveFormat => _waveFormat;

    public override long Length =>
        (long)(_reader.TotalTime.TotalSeconds * _reader.SampleRate) * _waveFormat.BlockAlign;

    public override long Position
    {
        get => (long)(_reader.TimePosition.TotalSeconds * _reader.SampleRate) * _waveFormat.BlockAlign;
        set => _reader.TimePosition = TimeSpan.FromSeconds(
            (double)value / _waveFormat.BlockAlign / _reader.SampleRate);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        // NVorbis works in float samples; convert to the requested byte count.
        int samplesRequested = count / 4;
        if (samplesRequested <= 0) return 0;

        if (_sampleBuffer.Length < samplesRequested) _sampleBuffer = new float[samplesRequested];

        int samplesRead = _reader.ReadSamples(_sampleBuffer, 0, samplesRequested);
        if (samplesRead <= 0) return 0;

        Buffer.BlockCopy(_sampleBuffer, 0, buffer, offset, samplesRead * 4);
        return samplesRead * 4;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) _reader.Dispose();
        base.Dispose(disposing);
    }
}
