using System.Runtime.CompilerServices;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class AnalysisQueueTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_queue_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    public AnalysisQueueTests() { _db = new Db(_path); _db.OpenPlaintext(); }
    public void Dispose() { _db.DestroyFile(); }

    [Fact]
    public void Queue_is_fifo_and_complete_removes()
    {
        var a = _db.EnqueueAnalysis(1, "first");
        _db.EnqueueAnalysis(2, "second");
        Assert.Equal(2, _db.PendingAnalysisCount());

        var next = _db.NextPendingAnalysis();
        Assert.Equal(a, next!.Value.Id);
        Assert.Equal("first", next.Value.Payload);

        _db.CompletePendingAnalysis(a);
        Assert.Equal(1, _db.PendingAnalysisCount());
        Assert.Equal("second", _db.NextPendingAnalysis()!.Value.Payload);
    }

    [Fact]
    public void Failed_attempts_are_counted_but_item_stays()
    {
        var id = _db.EnqueueAnalysis(7, "entry");
        _db.BumpPendingAnalysisAttempts(id);
        _db.BumpPendingAnalysisAttempts(id);
        var item = _db.NextPendingAnalysis();
        Assert.Equal(2, item!.Value.Attempts);
        Assert.Equal(7, item.Value.EntryId);
    }

    /// <summary>Provider that always fails — simulates being offline.</summary>
    private sealed class OfflineChat : IChatProvider
    {
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
            const string err = "[provider unreachable]";
            onError?.Invoke(err);
            yield return err;
        }
    }

    [Fact]
    public async Task Reflect_reports_provider_failure_so_callers_can_queue()
    {
        var emb = new FakeEmbedder();
        var analyst = new AnalystSubagent(
            new ProviderRegistry(new IChatProvider[] { new OfflineChat() }),
            new MemoryTools(_db, emb), new ProfileTools(new ProfileStore(_db, Path.GetTempFileName())),
            new GraphTools(_db, emb), new EntryTools(_db, emb), new PredictionTools(_db),
            new RecallTools(new RecallEngine(_db, emb, new EntityResolver(_db, emb))), new TraceLog());

        var failed = false;
        await analyst.ReflectAsync(1, "an entry", "", "", onProviderError: _ => failed = true);
        Assert.True(failed);
    }

    [Fact]
    public void Items_at_the_retry_cap_are_parked_not_selected()
    {
        var poison = _db.EnqueueAnalysis(1, "poison");
        var healthy = _db.EnqueueAnalysis(2, "healthy");
        for (var i = 0; i < Db.MaxAnalysisAttempts; i++) _db.BumpPendingAnalysisAttempts(poison);

        // Parked item is skipped (FIFO would otherwise pick it), and drops out of the count.
        Assert.Equal(healthy, _db.NextPendingAnalysis()!.Value.Id);
        Assert.Equal(1, _db.PendingAnalysisCount());

        _db.CompletePendingAnalysis(healthy);
        Assert.Null(_db.NextPendingAnalysis());   // only the parked item remains — queue reads empty
        Assert.Equal(0, _db.PendingAnalysisCount());
    }
}
