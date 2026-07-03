using System.Net.Http;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class OpenAICompatClientTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_compat_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    public OpenAICompatClientTests() { _db = new Db(_path); _db.OpenPlaintext(); }
    public void Dispose() { _db.DestroyFile(); }

    private OpenAICompatChatClient NewClient(string id = "deepseek") => new(
        new HttpClient(), new UsageTracker(), new LlmConfig(_db),
        id, "DeepSeek", "https://api.deepseek.com/v1", requiresKey: true);

    [Fact]
    public async Task Missing_key_short_circuits_via_onError()
    {
        var c = NewClient();
        if (c.HasKey) return;   // dev .env supplies a key — offline assertion doesn't apply
        string? err = null; var text = "";
        await foreach (var t in c.StreamReplyAsync("sys", new[] { new ChatMessage("user", "hi") }, onError: e => err = e))
            text += t;
        Assert.Contains("no DeepSeek API key", text);
        Assert.Equal(text, err);
    }

    [Fact]
    public void Identity_comes_from_the_catalog()
    {
        var c = NewClient();
        Assert.Equal("deepseek", c.ProviderId);
        Assert.StartsWith("DeepSeek", c.DisplayName);
        Assert.Equal("deepseek-chat", c.ModelName);   // catalog default
    }

    [Fact]
    public void Stretch_providers_are_connected_in_the_catalog()
    {
        foreach (var id in new[] { "moonshot", "zhipu", "deepseek" })
        {
            var prov = ModelCatalog.Find(id);
            Assert.NotNull(prov);
            Assert.True(prov!.Connected, id);
            Assert.NotEmpty(prov.Models);
        }
    }

    [Fact]
    public void LMStudio_still_needs_no_key_and_is_free()
    {
        var lm = new LMStudioClient(new HttpClient(), new UsageTracker(), new LlmConfig(_db));
        Assert.True(lm.HasKey);
        Assert.Equal(0, lm.Pricing.InputPerM);
    }
}
