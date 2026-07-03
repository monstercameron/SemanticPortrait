using Microsoft.JSInterop;
using SemanticPortrait.App.Services;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Components.Pages;

// Optional voice (design §M5): mic → NPU Whisper → composer text, and reply → NPU Supertonic →
// speakers. Everything rides the WhisperToMe sidecar (SidecarVoice); when it isn't installed the
// buttons simply don't render — the app is fully usable without voice.
public partial class Home
{
    private SidecarVoice? _voice;
    private VoiceAudio? _voiceAudio;
    private bool _voiceAvailable;
    private bool _recording;
    private int _sttPending;         // utterance chunks awaiting transcription
    private Msg? _speaking;          // the reply currently being synthesized/spoken
    private readonly SemaphoreSlim _sttGate = new(1, 1);   // one sidecar process at a time
    private double _micLevel;        // live input level 0..1 (meter next to "listening…")
    private DateTime _lastMeterPaint = DateTime.MinValue;

    // 10 Hz from the audio thread → throttle repaints to ~5 Hz so the meter is smooth, not spammy
    private void OnMicLevel(double level)
    {
        _micLevel = level;
        if ((DateTime.UtcNow - _lastMeterPaint).TotalMilliseconds < 180) return;
        _lastMeterPaint = DateTime.UtcNow;
        _ = InvokeAsync(StateHasChanged).Guard("mic-meter");
    }

    private void InitVoice()
    {
        string? Pref(string key)
        {
            var v = Microsoft.Maui.Storage.Preferences.Default.Get(key, "");
            return string.IsNullOrWhiteSpace(v) ? null : v;
        }
        _voice = new SidecarVoice(Pref("voice_py"), Pref("voice_dir"));
        _voiceAvailable = _voice.Available;
    }

    /// <summary>Mic button: press to START a continuous listen — speech is segmented at natural
    /// pauses and each utterance transcribes into the composer as you go — press again to STOP.
    /// Only the explicit stop ends the session (a pause just commits a chunk).</summary>
    private void ToggleMic()
    {
        if (_voice is null) return;
        _voiceAudio ??= new VoiceAudio();
        if (!_recording)
        {
            try
            {
                _voiceAudio.LevelChanged -= OnMicLevel;
                _voiceAudio.LevelChanged += OnMicLevel;
                _voiceAudio.StartContinuous(OnUtterance);
                _recording = true;
            }
            catch (Exception ex) { _messages.Add(new() { Role = "sys", Text = $"🎙 mic unavailable — {ex.Message}" }); }
            return;
        }
        _voiceAudio.StopContinuous();   // trailing speech flushes as a final utterance
        _voiceAudio.LevelChanged -= OnMicLevel;
        _recording = false; _micLevel = 0;
        StateHasChanged();
    }

    /// <summary>One segmented utterance (called off the audio thread): queue it through the
    /// sidecar one-at-a-time and append the text to the composer as it arrives.</summary>
    private void OnUtterance(string wavPath)
    {
        Interlocked.Increment(ref _sttPending);
        _ = InvokeAsync(StateHasChanged).Guard("voice-chunk-ui");
        _ = Task.Run(async () =>
        {
            try
            {
                await _sttGate.WaitAsync();
                try
                {
                    var text = await _voice!.TranscribeAsync(wavPath);
                    if (!string.IsNullOrWhiteSpace(text))
                        await InvokeAsync(async () =>
                        {
                            _draft = string.IsNullOrWhiteSpace(_draft) ? text : _draft.TrimEnd() + " " + text;
                            // Land it VISIBLY for review — Blazor's re-render alone can leave the
                            // textarea stale after async work.
                            try { await JS.InvokeVoidAsync("spSetComposer", _input, _draft); } catch { }
                            StateHasChanged();
                        });
                }
                finally { _sttGate.Release(); }
            }
            finally
            {
                try { File.Delete(wavPath); } catch { }
                Interlocked.Decrement(ref _sttPending);
                _ = InvokeAsync(StateHasChanged).Guard("voice-chunk-done");
            }
        }).Guard("voice-utterance");
    }

    /// <summary>Speaker button on a reply: synthesize + play; pressing again stops.</summary>
    private async Task SpeakMessage(Msg m)
    {
        if (_voice is null) return;
        _voiceAudio ??= new VoiceAudio();
        if (_speaking == m) { _voiceAudio.StopPlayback(); _speaking = null; return; }

        _speaking = m; StateHasChanged();
        var outPath = Path.Combine(Path.GetTempPath(), $"sp_tts_{Guid.NewGuid():N}.wav");
        var wav = await _voice.SpeakAsync(SidecarVoice.StripForSpeech(m.Text), outPath);
        if (_speaking != m) { if (wav is not null) try { File.Delete(wav); } catch { } return; }   // cancelled meanwhile
        if (wav is null)
        {
            _messages.Add(new() { Role = "sys", Text = "🔊 voice synthesis failed (is the sidecar healthy?)" });
            _speaking = null; StateHasChanged(); return;
        }
        _voiceAudio.Play(wav, onDone: () =>
        {
            try { File.Delete(wav); } catch { }
            _ = InvokeAsync(() => { if (_speaking == m) _speaking = null; StateHasChanged(); }).Guard("voice-done");
        });
        StateHasChanged();
    }

}
