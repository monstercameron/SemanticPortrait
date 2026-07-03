using System.Runtime.CompilerServices;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class CompactorTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_compact_{Guid.NewGuid():N}.db");
    private Db NewDb() { var d = new Db(_path); d.OpenPlaintext(); return d; }
    public void Dispose() { try { if (File.Exists(_path)) File.Delete(_path); } catch { } }

    /// <summary>Streams fixed output; optionally signals a provider error (like an unreachable server).</summary>
    private sealed class FakeChat : IChatProvider
    {
        private readonly string _out; private readonly bool _error;
        public FakeChat(string output, bool error = false) { _out = output; _error = error; }
        public string ProviderId => "fake";
        public string DisplayName => "Fake";
        public bool HasKey => true;
        public string ModelName => "fake";
        public ModelPricing Pricing => default;
        public async IAsyncEnumerable<string> StreamReplyAsync(string systemPrompt,
            IEnumerable<ChatMessage> history, IReadOnlyList<object>? tools = null,
            Func<string, string, Task<string>>? toolExecutor = null, Action<string>? onReasoning = null,
            string effort = "low", Action<string>? onError = null,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            await Task.Yield();
            if (_error) onError?.Invoke(_out);
            yield return _out;
        }
    }

    private static Compactor NewCompactor(Db db, IChatProvider chat) =>
        new(db, new ProviderRegistry(new[] { chat }));

    private static void AddAgedMessage(Db db, DateTime nowUtc) =>
        db.AddMessage("user", "an old entry", nowUtc.AddDays(-3).ToString("o"));

    [Fact]
    public async Task Provider_error_does_not_overwrite_summary_or_advance_window()
    {
        var db = NewDb();
        var now = DateTime.UtcNow;
        db.SetCompaction("the real summary", now.AddDays(-5).ToString("o"));
        AddAgedMessage(db, now);

        await NewCompactor(db, new FakeChat("[LM Studio unreachable]", error: true)).EnsureCompactedAsync(now);

        var c = db.GetCompaction();
        Assert.NotNull(c);
        Assert.Equal("the real summary", c!.Value.Summary);                       // untouched
        Assert.Equal(now.AddDays(-5).ToString("o"), c.Value.ThroughUtc);          // not advanced
    }

    [Fact]
    public async Task Successful_stream_folds_aged_messages_into_summary()
    {
        var db = NewDb();
        var now = DateTime.UtcNow;
        AddAgedMessage(db, now);

        await NewCompactor(db, new FakeChat("updated summary")).EnsureCompactedAsync(now);

        var c = db.GetCompaction();
        Assert.NotNull(c);
        Assert.Equal("updated summary", c!.Value.Summary);
        Assert.Equal(now.AddDays(-3).ToString("o"), c.Value.ThroughUtc);          // advanced to newest aged
    }
}
