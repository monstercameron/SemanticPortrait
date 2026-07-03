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
    private bool _transcribing;
    private Msg? _speaking;          // the reply currently being synthesized/spoken
    private string? _recPath;

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

    /// <summary>Mic button: first press records, second press stops + transcribes into the draft.</summary>
    private async Task ToggleMic()
    {
        if (_voice is null || _transcribing) return;
        _voiceAudio ??= new VoiceAudio();
        if (!_recording)
        {
            try { _recPath = _voiceAudio.StartRecording(); _recording = true; }
            catch (Exception ex) { _messages.Add(new() { Role = "sys", Text = $"🎙 mic unavailable — {ex.Message}" }); }
            return;
        }
        _voiceAudio.StopRecording();
        _recording = false; _transcribing = true; StateHasChanged();
        try
        {
            var text = _recPath is null ? null : await _voice.TranscribeAsync(_recPath);
            if (!string.IsNullOrWhiteSpace(text))
            {
                _draft = string.IsNullOrWhiteSpace(_draft) ? text : _draft.TrimEnd() + " " + text;
                _focusNext = true;
            }
            else _messages.Add(new() { Role = "sys", Text = "🎙 didn't catch anything — try again a little closer to the mic" });
        }
        finally
        {
            if (_recPath is not null) { try { File.Delete(_recPath); } catch { } _recPath = null; }
            _transcribing = false;
            StateHasChanged();
        }
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
