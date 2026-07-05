using System.Runtime.CompilerServices;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class MaskingTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_mask_{Guid.NewGuid():N}.db");
    private Db NewDb() { var d = new Db(_path); d.OpenPlaintext(); return d; }
    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    [Fact]
    public void Masks_structured_pii_and_round_trips()
    {
        var db = NewDb();
        var m = new RegexMasker(db, () => true);
        const string text = "Email me at sam@example.com or call 555-123-4567.";

        var masked = m.Mask(text);
        Assert.DoesNotContain("sam@example.com", masked);
        Assert.DoesNotContain("555-123-4567", masked);
        Assert.Contains("EMAIL_1", masked);
        Assert.Contains("PHONE_1", masked);

        Assert.Equal(text, m.Unmask(masked));     // fully reversible
    }

    [Fact]
    public void Tokens_are_stable_per_value()
    {
        var db = NewDb();
        var m = new RegexMasker(db, () => true);
        var a = m.Mask("write sam@example.com");
        var b = m.Mask("again sam@example.com");
        Assert.Equal(a.Replace("write ", ""), b.Replace("again ", ""));   // same token both times
    }

    [Fact]
    public void Masks_registered_entity_names_and_round_trips()
    {
        var db = NewDb();
        db.RegisterEntityAlias("Sarah Chen", "Sarah", "person");   // canonical + a nickname mention
        db.RegisterEntityAlias("Baptist Health", null, "org");
        var m = new RegexMasker(db, () => true);
        const string text = "Sarah drove me to Baptist Health on Tuesday.";

        var masked = m.Mask(text);
        Assert.DoesNotContain("Sarah", masked);           // free-form name masked (regex can't catch it)
        Assert.DoesNotContain("Baptist Health", masked);
        Assert.Contains("PERSON_1", masked);
        Assert.Contains("ORG_1", masked);
        Assert.Equal(text, m.Unmask(masked));             // fully reversible
    }

    [Fact]
    public void Entity_not_yet_registered_is_not_masked()
    {
        // Honest limit: the registry can only mask names the analyst has already registered — the
        // very first mention of a new person still egresses in the clear.
        var db = NewDb();
        var m = new RegexMasker(db, () => true);
        const string text = "A new friend Priya showed up today.";
        Assert.Equal(text, m.Mask(text));
    }

    [Fact]
    public void Disabled_is_pass_through()
    {
        var db = NewDb();
        var m = new RegexMasker(db, () => false);
        const string text = "sam@example.com";
        Assert.Equal(text, m.Mask(text));
        Assert.Equal(text, m.Unmask(text));
    }

    [Fact]
    public void Dates_and_timestamps_are_not_masked_as_phones()
    {
        var db = NewDb();
        var m = new RegexMasker(db, () => true);
        const string text = "we met on 2026-06-19 10:30 and again on 6/18/2026.";
        Assert.Equal(text, m.Mask(text));
    }

    [Fact]
    public void Real_phone_next_to_a_date_still_masks()
    {
        var db = NewDb();
        var m = new RegexMasker(db, () => true);
        var masked = m.Mask("on 2026-03-15 she gave me 555-123-4567");
        Assert.Contains("2026-03-15", masked);
        Assert.DoesNotContain("555-123-4567", masked);
    }

    [Fact]
    public void Unmask_does_not_bleed_into_longer_tokens()
    {
        var db = NewDb();
        var m = new RegexMasker(db, () => true);
        var token = db.GetOrCreateAlias("PERSON", "Alice");   // PERSON_1
        Assert.Equal("PERSON_1", token);

        Assert.Equal("saw Alice today", m.Unmask("saw PERSON_1 today"));
        Assert.Equal("saw PERSON_12 today", m.Unmask("saw PERSON_12 today"));   // unknown token untouched
    }

    /// <summary>Captures the text handed to the inner embedder (the wire payload).</summary>
    private sealed class CapturingEmbedder : IEmbedder
    {
        public string? LastText;
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        { LastText = text; return Task.FromResult<float[]?>(new float[] { 1f }); }
    }

    [Fact]
    public async Task Embedding_input_is_masked_when_enabled()
    {
        var db = NewDb();
        var inner = new CapturingEmbedder();
        var e = new MaskingEmbedder(inner, new RegexMasker(db, () => true));

        await e.EmbedAsync("reach me at sam@example.com");
        Assert.DoesNotContain("sam@example.com", inner.LastText);
        Assert.Contains("EMAIL_1", inner.LastText);
    }

    [Fact]
    public async Task Embedding_input_passes_through_when_disabled()
    {
        var db = NewDb();
        var inner = new CapturingEmbedder();
        var e = new MaskingEmbedder(inner, new RegexMasker(db, () => false));

        await e.EmbedAsync("reach me at sam@example.com");
        Assert.Equal("reach me at sam@example.com", inner.LastText);
    }

    // Fake provider that echoes preset chunks (ignores inputs) to test streaming unmask.
    private sealed class ChunkProvider : IChatProvider
    {
        private readonly string[] _chunks;
        public ChunkProvider(params string[] chunks) { _chunks = chunks; }
        public string ProviderId => "fake";
        public string DisplayName => "Fake";
        public bool HasKey => true;
        public string ModelName => "fake";
        public ModelPricing Pricing => default;
        public async IAsyncEnumerable<string> StreamReplyAsync(string systemPrompt,
            IEnumerable<ChatMessage> history, IReadOnlyList<object>? tools = null,
            Func<string, string, Task<string>>? toolExecutor = null, Action<string>? onReasoning = null,
            string effort = "low", Action<string>? onError = null, [EnumeratorCancellation] CancellationToken ct = default)
        { foreach (var c in _chunks) { await Task.Yield(); yield return c; } }
    }

    [Fact]
    public async Task Decorator_unmasks_token_split_across_chunks()
    {
        var db = NewDb();
        var m = new RegexMasker(db, () => true);
        var token = m.Mask("sam@example.com").Trim();      // creates EMAIL_1 alias
        Assert.Equal("EMAIL_1", token);

        // Inner streams the token split across two chunks.
        var inner = new ChunkProvider("reply: EMA", "IL_1 — done");
        var masked = new MaskingChatProvider(inner, m);

        var sb = new System.Text.StringBuilder();
        await foreach (var t in masked.StreamReplyAsync("sys", Array.Empty<ChatMessage>()))
            sb.Append(t);

        Assert.Equal("reply: sam@example.com — done", sb.ToString());
    }

    [Fact]
    public async Task Decorator_masks_tool_results_and_unmasks_tool_args()
    {
        var db = NewDb();
        var m = new RegexMasker(db, () => true);
        var token = m.Mask("sam@example.com").Trim();

        string? sawArgs = null;
        Task<string> Exec(string name, string args) { sawArgs = args; return Task.FromResult("found sam@example.com"); }

        // Provider that drives one tool call then ends; capture what the wrapped executor returns.
        string? toolResult = null;
        var inner = new ToolDriver(async exec => toolResult = await exec("search", token));   // model passes the TOKEN
        var masked = new MaskingChatProvider(inner, m);

        await foreach (var _ in masked.StreamReplyAsync("sys", Array.Empty<ChatMessage>(), tools: new object[] { }, toolExecutor: Exec)) { }

        Assert.Equal("sam@example.com", sawArgs);          // tool received the REAL value (unmasked)
        Assert.Equal("found EMAIL_1", toolResult);          // result was masked before going back to the model
    }

    // Provider that invokes the (wrapped) tool executor once, then yields nothing.
    private sealed class ToolDriver : IChatProvider
    {
        private readonly Func<Func<string, string, Task<string>>, Task> _run;
        public ToolDriver(Func<Func<string, string, Task<string>>, Task> run) { _run = run; }
        public string ProviderId => "fake";
        public string DisplayName => "Fake";
        public bool HasKey => true;
        public string ModelName => "fake";
        public ModelPricing Pricing => default;
        public async IAsyncEnumerable<string> StreamReplyAsync(string systemPrompt,
            IEnumerable<ChatMessage> history, IReadOnlyList<object>? tools = null,
            Func<string, string, Task<string>>? toolExecutor = null, Action<string>? onReasoning = null,
            string effort = "low", Action<string>? onError = null, [EnumeratorCancellation] CancellationToken ct = default)
        { if (toolExecutor is not null) await _run(toolExecutor); yield break; }
    }
}
