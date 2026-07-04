using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class ClaudeClientTests
{
    // The app's tool specs are flat Responses-style anonymous objects; the Claude provider must
    // reshape them into Anthropic Tool definitions without losing schema detail.
    [Fact]
    public void ToTools_converts_flat_specs_to_anthropic_tools()
    {
        var spec = new
        {
            type = "function",
            name = "save_note",
            description = "Save a durable insight.",
            parameters = new
            {
                type = "object",
                properties = new { text = new { type = "string", description = "The insight." } },
                required = new[] { "text" },
                additionalProperties = false,
            },
        };

        var tools = ClaudeClient.ToTools(new object[] { spec });

        Assert.NotNull(tools);
        var union = Assert.Single(tools!);
        Assert.True(union.TryPickTool(out var tool));
        Assert.Equal("save_note", tool!.Name);
        Assert.Equal("Save a durable insight.", tool.Description);
        Assert.NotNull(tool.InputSchema.Properties);
        Assert.True(tool.InputSchema.Properties!.ContainsKey("text"));
        Assert.Equal(new[] { "text" }, tool.InputSchema.Required);
    }

    [Fact]
    public void ToTools_handles_null_and_empty()
    {
        Assert.Null(ClaudeClient.ToTools(null));
        Assert.Null(ClaudeClient.ToTools(Array.Empty<object>()));
    }

    [Fact]
    public void Catalog_lists_claude_as_connected_with_pricing()
    {
        var prov = ModelCatalog.Find("anthropic");
        Assert.NotNull(prov);
        Assert.True(prov!.Connected);
        Assert.Contains(prov.Models, m => m.Id == "claude-opus-4-8" && m.Pricing is not null);
        Assert.All(prov.Models, m => Assert.NotNull(m.Pricing));
    }

    [Fact]
    public async Task Provider_reports_missing_key_via_onError()
    {
        // No key configured → the stream must surface the problem through BOTH channels
        // (bracketed text for the chat, onError for persisting callers).
        var db = new Db(Path.Combine(Path.GetTempPath(), $"sp_claude_{Guid.NewGuid():N}.db"));
        db.OpenPlaintext();
        try
        {
            var client = new ClaudeClient(new UsageTracker(), new LlmConfig(db));
            if (client.HasKey) return;   // a dev .env supplies a key — the offline assertion doesn't apply
            string? err = null;
            var text = "";
            var task = Task.Run(async () =>
            {
                await foreach (var t in client.StreamReplyAsync("sys",
                    new[] { new ChatMessage("user", "hi") }, onError: e => err = e))
                    text += t;
            });
            await task.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Contains("no Anthropic API key", text);
            Assert.Equal(text, err);
        }
        finally { db.DestroyFile(); }
    }
}
