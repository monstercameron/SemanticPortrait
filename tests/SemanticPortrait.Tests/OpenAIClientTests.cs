using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>
/// Pins the /v1/responses SSE frame parser. The load-bearing cases are the FAILURE events:
/// OpenAI reports server-side failures (e.g. insufficient_quota) as events INSIDE a 200 stream —
/// unmapped, they fell through to null and a failed round was indistinguishable from a clean
/// empty reply ("the agent just stopped", live 2026-07-02).
/// </summary>
public class OpenAIClientTests
{
    [Fact]
    public void Response_failed_maps_to_an_error_with_the_nested_message()
    {
        var ev = OpenAIClient.ParseFrame(
            """{"type":"response.failed","response":{"error":{"code":"insufficient_quota","message":"You exceeded your current quota"}}}""");
        Assert.NotNull(ev);
        Assert.NotNull(ev!.Error);
        Assert.Contains("You exceeded your current quota", ev.Error);
        Assert.Contains("response.failed", ev.Error);
    }

    [Fact]
    public void Response_incomplete_maps_to_an_error_with_the_reason()
    {
        var ev = OpenAIClient.ParseFrame(
            """{"type":"response.incomplete","response":{"incomplete_details":{"reason":"max_output_tokens"}}}""");
        Assert.NotNull(ev?.Error);
        Assert.Contains("incomplete: max_output_tokens", ev!.Error);
    }

    [Fact]
    public void Top_level_error_event_maps_with_flat_or_nested_message()
    {
        var flat = OpenAIClient.ParseFrame("""{"type":"error","message":"server exploded"}""");
        Assert.Contains("server exploded", flat!.Error);

        // The real quota failure arrives with the message nested under error.message.
        var nested = OpenAIClient.ParseFrame(
            """{"type":"error","error":{"type":"insufficient_quota","message":"check your plan and billing"}}""");
        Assert.Contains("check your plan and billing", nested!.Error);
    }

    [Fact]
    public void Failure_events_without_a_message_still_surface_as_errors()
    {
        // Never let a malformed failure payload regress to silence — worst case, raw JSON shows.
        var ev = OpenAIClient.ParseFrame("""{"type":"response.failed"}""");
        Assert.NotNull(ev?.Error);
    }

    [Fact]
    public void Text_and_reasoning_deltas_map()
    {
        Assert.Equal("hel", OpenAIClient.ParseFrame("""{"type":"response.output_text.delta","delta":"hel"}""")!.Text);
        Assert.Equal("thinking…", OpenAIClient.ParseFrame(
            """{"type":"response.reasoning_summary_text.delta","delta":"thinking…"}""")!.Reasoning);
    }

    [Fact]
    public void Function_call_lifecycle_maps()
    {
        var added = OpenAIClient.ParseFrame(
            """{"type":"response.output_item.added","item":{"type":"function_call","id":"fc_1","call_id":"call_1","name":"recall"}}""");
        Assert.Equal("fc_1", added!.ItemAdded!.ItemId);
        Assert.Equal("call_1", added.ItemAdded.CallId);
        Assert.Equal("recall", added.ItemAdded.Name);

        // Non-function items (e.g. a message item) are not tool calls.
        Assert.Null(OpenAIClient.ParseFrame(
            """{"type":"response.output_item.added","item":{"type":"message","id":"msg_1"}}"""));

        var args = OpenAIClient.ParseFrame(
            """{"type":"response.function_call_arguments.delta","item_id":"fc_1","delta":"{\"q\":"}""");
        Assert.Equal(("fc_1", "{\"q\":"), args!.ArgsDelta!.Value);
    }

    [Fact]
    public void Completed_maps_usage_including_cached_tokens()
    {
        var ev = OpenAIClient.ParseFrame(
            """{"type":"response.completed","response":{"usage":{"input_tokens":120,"output_tokens":45,"input_tokens_details":{"cached_tokens":100}}}}""");
        Assert.Equal((120L, 45L, 100L), ev!.Usage!.Value);
    }

    [Fact]
    public void Garbage_and_unknown_frames_return_null_not_throw()
    {
        Assert.Null(OpenAIClient.ParseFrame("not json at all"));
        Assert.Null(OpenAIClient.ParseFrame("""{"no":"type"}"""));
        Assert.Null(OpenAIClient.ParseFrame("""{"type":"response.some_future_event"}"""));
        Assert.Null(OpenAIClient.ParseFrame(""));
    }
}
