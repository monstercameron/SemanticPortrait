using NAudio.Wave;

namespace SemanticPortrait.App.Services;

/// <summary>
/// Mic capture + WAV playback for the optional voice feature (NAudio — plain Win32 WASAPI,
/// no package identity needed for an unpackaged app). Records 16 kHz mono PCM, the shape the
/// Whisper sidecar expects.
/// </summary>
public sealed class VoiceAudio : IDisposable
{
    private WaveInEvent? _waveIn;
    private WaveFileWriter? _writer;
    private WaveOutEvent? _playback;
    private AudioFileReader? _reader;

    public bool Recording => _waveIn is not null;

    /// <summary>Start capturing the default mic to a temp WAV; returns the path.</summary>
    public string StartRecording()
    {
        StopPlayback();
        StopRecording();   // defensive: never two captures
        var path = Path.Combine(Path.GetTempPath(), $"sp_voice_{Guid.NewGuid():N}.wav");
        _waveIn = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1) };
        _writer = new WaveFileWriter(path, _waveIn.WaveFormat);
        _waveIn.DataAvailable += (_, e) => { try { _writer?.Write(e.Buffer, 0, e.BytesRecorded); } catch { } };
        _waveIn.StartRecording();
        return path;
    }

    /// <summary>Stop the capture and flush the file (safe to call when not recording).</summary>
    public void StopRecording()
    {
        try { _waveIn?.StopRecording(); } catch { }
        _waveIn?.Dispose(); _waveIn = null;
        try { _writer?.Dispose(); } catch { }
        _writer = null;
    }

    /// <summary>Play a WAV (stops any previous playback first).</summary>
    public void Play(string wavPath, Action? onDone = null)
    {
        StopPlayback();
        _reader = new AudioFileReader(wavPath);
        _playback = new WaveOutEvent();
        _playback.Init(_reader);
        _playback.PlaybackStopped += (_, _) => { StopPlayback(); onDone?.Invoke(); };
        _playback.Play();
    }

    public bool Playing => _playback?.PlaybackState == PlaybackState.Playing;

    public void StopPlayback()
    {
        try { _playback?.Stop(); } catch { }
        _playback?.Dispose(); _playback = null;
        _reader?.Dispose(); _reader = null;
    }

    public void Dispose() { StopRecording(); StopPlayback(); }
}
