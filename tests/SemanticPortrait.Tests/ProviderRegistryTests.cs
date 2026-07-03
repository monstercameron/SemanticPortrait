using System.Runtime.CompilerServices;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class ProviderRegistryTests
{
    private sealed class StubProvider : IChatProvider
    {
        public StubProvider(string id) { ProviderId = id; }
        public string ProviderId { get; }
        public string DisplayName => ProviderId;
        public bool HasKey => true;
        public string ModelName => ProviderId;
        public ModelPricing Pricing => default;
        public async IAsyncEnumerable<string> StreamReplyAsync(string systemPrompt,
            IEnumerable<ChatMessage> history, IReadOnlyList<object>? tools = null,
            Func<string, string, Task<string>>? toolExecutor = null, Action<string>? onReasoning = null,
            string effort = "low", Action<string>? onError = null, [EnumeratorCancellation] CancellationToken ct = default)
        { await Task.Yield(); yield break; }
    }

    [Fact]
    public void Active_defaults_to_first_when_unset()
    {
        var reg = new ProviderRegistry(new[] { new StubProvider("openai"), new StubProvider("claude") });
        Assert.Equal("openai", reg.Active.ProviderId);
        Assert.Equal(2, reg.Available.Count);
    }

    [Fact]
    public void Active_follows_persisted_selection()
    {
        string? selected = null;
        var reg = new ProviderRegistry(
            new[] { new StubProvider("openai"), new StubProvider("claude") },
            readSelected: () => selected, writeSelected: id => selected = id);

        Assert.True(reg.Select("claude"));
        Assert.Equal("claude", reg.Active.ProviderId);

        Assert.False(reg.Select("nope"));            // unknown id ignored
        Assert.Equal("claude", reg.Active.ProviderId);
    }

    [Fact]
    public void Unknown_persisted_id_falls_back_to_first()
    {
        var reg = new ProviderRegistry(new[] { new StubProvider("openai") }, readSelected: () => "ghost");
        Assert.Equal("openai", reg.Active.ProviderId);
    }
}
