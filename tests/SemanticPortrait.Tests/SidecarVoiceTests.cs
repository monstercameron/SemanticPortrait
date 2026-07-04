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
    public void Apostrophes_flip_python_repr_to_double_quotes_and_still_parse()
    {
        // %r quoting: a contraction switches repr to double quotes — the bug that ate every longer sentence.
        Assert.Equal("I don't want to talk about work today, it's been a lot.",
            SidecarVoice.ParseSttText(
                "INFO whispertome.cli: stt_text=\"I don't want to talk about work today, it's been a lot.\" language=en duration_ms=8100"));
    }

    [Fact]
    public void Escaped_quotes_inside_the_repr_unescape()
    {
        // both quote kinds present → repr uses single quotes and escapes the internal ones
        Assert.Equal("He said \"don't\" again.",
            SidecarVoice.ParseSttText(@"stt_text='He said ""don\'t"" again.' language=en"));
    }

    [Fact]
    public async Task Unavailable_sidecar_reports_unavailable_and_never_throws()
    {
        var v = new SidecarVoice(@"C:\does\not\exist\python.exe", @"C:\does\not\exist");
        Assert.False(v.Available);
        Assert.Null(await v.TranscribeAsync("x.wav"));
        Assert.Null(await v.SpeakAsync("hi", "out.wav"));
    }

    [Fact]
    public async Task Voice_setup_tool_reports_missing_runtime_honestly()
    {
        var tools = new VoiceTools(new SidecarVoice(@"C:\does\not\exist\python.exe", @"C:\does\not\exist"));
        Assert.True(tools.Handles("voice_setup"));
        var res = await tools.ExecuteAsync("voice_setup", "{\"action\":\"status\"}");
        Assert.Contains("not present", res);
        // download without a runtime must not pretend to work
        var dl = await tools.ExecuteAsync("voice_setup", "{\"action\":\"download\",\"confirmed\":true}");
        Assert.Contains("not present", dl);
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
