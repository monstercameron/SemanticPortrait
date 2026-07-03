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

    // ---- continuous dictation: listen → segment on pauses → emit utterance WAVs ------------
    // Whisper's QNN graph consumes ≤30s windows, so an open-ended recording must be CHUNKED:
    // an energy gate cuts the stream at natural pauses (like the WhisperToMeDictate segmenter),
    // with a hard cut before the window limit. Each utterance lands as its own 16k mono WAV via
    // the callback; capture keeps running until StopContinuous.
    private WaveInEvent? _cont;
    private readonly object _segGate = new();
    private readonly List<byte> _seg = new();
    private readonly List<byte> _preRoll = new();
    private bool _inSpeech; private int _speechMs, _silenceMs;
    private Action<string>? _onUtterance;

    private const double SpeechRms = 350;      // int16 scale; typical room noise sits well under this
    private const int PreRollMs = 400;         // keep the syllable that TRIGGERED speech detection
    private const int EndSilenceMs = 900;      // a natural pause commits the utterance
    private const int MinSpeechMs = 300;       // shorter bursts are noise, not words
    private const int MaxUtteranceMs = 25000;  // hard cut safely inside Whisper's 30s window

    public bool Listening => _cont is not null;

    public void StartContinuous(Action<string> onUtterance)
    {
        StopPlayback(); StopRecording(); StopContinuous();
        lock (_segGate) { _seg.Clear(); _preRoll.Clear(); _inSpeech = false; _speechMs = _silenceMs = 0; }
        _onUtterance = onUtterance;
        _cont = new WaveInEvent { WaveFormat = new WaveFormat(16000, 16, 1), BufferMilliseconds = 100 };
        _cont.DataAvailable += OnContinuousData;
        _cont.StartRecording();
    }

    /// <summary>Stop listening; any trailing speech flushes as a final utterance.</summary>
    public void StopContinuous()
    {
        if (_cont is null) return;
        try { _cont.StopRecording(); } catch { }
        _cont.Dispose(); _cont = null;
        lock (_segGate)
        {
            if (_seg.Count / 32 >= MinSpeechMs) EmitLocked();
            else { _seg.Clear(); _inSpeech = false; }
            _preRoll.Clear();
        }
        _onUtterance = null;
    }

    private void OnContinuousData(object? s, WaveInEventArgs e)
    {
        double sum = 0; int n = e.BytesRecorded / 2;
        for (int i = 0; i + 1 < e.BytesRecorded; i += 2)
        { short v = BitConverter.ToInt16(e.Buffer, i); sum += (double)v * v; }
        bool loud = n > 0 && Math.Sqrt(sum / n) > SpeechRms;

        lock (_segGate)
        {
            if (!_inSpeech)
            {
                for (int i = 0; i < e.BytesRecorded; i++) _preRoll.Add(e.Buffer[i]);
                var excess = _preRoll.Count - PreRollMs * 32;
                if (excess > 0) _preRoll.RemoveRange(0, excess);
                if (loud)
                {
                    _inSpeech = true; _speechMs = 100; _silenceMs = 0;
                    _seg.AddRange(_preRoll); _preRoll.Clear();
                }
                return;
            }

            for (int i = 0; i < e.BytesRecorded; i++) _seg.Add(e.Buffer[i]);
            if (loud) { _speechMs += 100; _silenceMs = 0; } else _silenceMs += 100;

            var segMs = _seg.Count / 32;   // 32 bytes per ms at 16 kHz, 16-bit mono
            if (segMs >= MaxUtteranceMs || (_silenceMs >= EndSilenceMs && _speechMs >= MinSpeechMs))
                EmitLocked();
            else if (_silenceMs >= EndSilenceMs)
            { _seg.Clear(); _inSpeech = false; _speechMs = _silenceMs = 0; }   // noise blip, not words
        }
    }

    private void EmitLocked()
    {
        var bytes = _seg.ToArray();
        _seg.Clear(); _inSpeech = false; _speechMs = _silenceMs = 0;
        var cb = _onUtterance;
        if (cb is null || bytes.Length == 0) return;
        var path = Path.Combine(Path.GetTempPath(), $"sp_utt_{Guid.NewGuid():N}.wav");
        _ = Task.Run(() =>
        {
            try
            {
                using (var w = new WaveFileWriter(path, new WaveFormat(16000, 16, 1)))
                    w.Write(bytes, 0, bytes.Length);
                cb(path);
            }
            catch { try { File.Delete(path); } catch { } }
        });
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

    public void Dispose() { StopContinuous(); StopRecording(); StopPlayback(); }
}
