using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>The pure parts of the optional voice path: CLI-output parsing and speech text prep.
/// (Actual NPU synthesis/transcription runs only on the dev machine with the sidecar present.)</summary>
public class SidecarVoiceTests
{
    [Fact]
    public void Parses_stt_text_from_cli_log_line()
    {
        const string log = """
        2026-07-03 15:02:37 INFO httpx: HTTP Request: HEAD https://example/config.json "HTTP/1.1 200 OK"
        2026-07-03 15:02:38 INFO whispertome.cli: stt_text='Hello there, testing one two.' language=en sample_rate=44100 duration_ms=2807 latency_ms=304.9 provider=QNNExecutionProvider:npu-plugin
        """;
        Assert.Equal("Hello there, testing one two.", SidecarVoice.ParseSttText(log));
    }

    [Fact]
    public void Missing_or_empty_transcription_is_null()
    {
        Assert.Null(SidecarVoice.ParseSttText("no transcript here"));
        Assert.Null(SidecarVoice.ParseSttText("stt_text='' language=en"));
    }

    [Fact]
    public void Unavailable_sidecar_reports_unavailable_and_never_throws()
    {
        var v = new SidecarVoice(@"C:\does\not\exist\python.exe", @"C:\does\not\exist");
        Assert.False(v.Available);
        Assert.Null(v.TranscribeAsync("x.wav").Result);
        Assert.Null(v.SpeakAsync("hi", "out.wav").Result);
    }

    [Fact]
    public void Speech_prep_strips_markdown_and_keeps_link_labels()
    {
        var s = SidecarVoice.StripForSpeech("**Bold** and _soft_ with a [link label](https://x) and `code`\n\n```\nblock\n```\nend.");
        Assert.Equal("Bold and soft with a link label and code code omitted. end.", s);
    }

    [Fact]
    public void Long_replies_cap_at_a_sentence_boundary()
    {
        var longText = string.Concat(Enumerable.Repeat("This is a sentence about the sky. ", 60));
        var s = SidecarVoice.StripForSpeech(longText);
        Assert.True(s.Length < 1000);
        Assert.EndsWith("…and more in the written reply.", s);
        Assert.Contains("sky.", s);
    }
}
