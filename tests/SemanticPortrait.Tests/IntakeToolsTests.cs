using System.Text.Json;
using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>The deterministic intake counter: monotonic count, idempotent re-marking, skip/abort
/// semantics, arc math in the status line, and zero-context once finished.</summary>
public class IntakeToolsTests : IDisposable
{
    private readonly string _path = Path.Combine(Path.GetTempPath(), $"sp_intake_{Guid.NewGuid():N}.db");
    private readonly Db _db;
    private readonly IntakeTools _tools;

    public IntakeToolsTests()
    {
        _db = new Db(_path);
        _db.OpenPlaintext();
        _tools = new IntakeTools(_db);
    }

    public void Dispose() { _db.DestroyFile(); }

    private Task<string> Mark(int q, string status = "answered") =>
        _tools.ExecuteAsync("record_intake_progress", JsonSerializer.Serialize(new { question = q, status }));

    [Fact]
    public async Task Counts_deterministically_and_names_the_next_question()
    {
        Assert.Contains("0/20", _tools.StatusLine());
        Assert.Contains("next is Q1", _tools.StatusLine());

        var r = await Mark(1);
        Assert.Contains("1/20", r);
        Assert.Contains("next is Q2", r);

        await Mark(2); await Mark(3);
        var line = _tools.StatusLine();
        Assert.Contains("3/20", line);
        Assert.Contains("Q4", line);
        Assert.Contains("arc 2", line);   // Q4 opens the life-snapshot arc
    }

    [Fact]
    public async Task Completing_an_arc_nudges_the_batch_handoff()
    {
        Assert.DoesNotContain("COMPLETE", await Mark(1));
        Assert.DoesNotContain("COMPLETE", await Mark(2));
        var r = await Mark(3);                                   // arc 1 = Q1-3
        Assert.Contains("Arc 1 (basics) is COMPLETE", r);
        Assert.Contains("send_to_analysis", r);

        // Out-of-order resolution: the nudge fires on whichever question CLOSES the arc.
        await Mark(5); await Mark(6); await Mark(7); await Mark(8);
        Assert.Contains("Arc 2 (life snapshot) is COMPLETE", await Mark(4));
    }

    [Fact]
    public async Task Remarking_the_same_question_never_double_counts()
    {
        await Mark(5); await Mark(5); await Mark(5, "skipped");
        Assert.Contains("1/20", _tools.StatusLine());
    }

    [Fact]
    public async Task Skips_count_as_resolved_and_are_not_reasked()
    {
        await Mark(1, "skipped");
        Assert.Contains("next is Q2", _tools.StatusLine());
    }

    [Fact]
    public async Task Out_of_order_answers_resolve_to_the_lowest_open_question()
    {
        await Mark(1); await Mark(8);   // user volunteered Q8 territory early
        Assert.Contains("next is Q2", _tools.StatusLine());
    }

    [Fact]
    public async Task Completing_all_twenty_ends_the_intake()
    {
        for (var q = 1; q < 20; q++) await Mark(q);
        Assert.False(_tools.IsComplete());

        var last = await Mark(20);
        Assert.Contains("COMPLETE", last);
        Assert.True(_tools.IsComplete());
        Assert.Null(_tools.StatusLine());   // finished intake costs zero context
    }

    [Fact]
    public async Task Abort_ends_the_intake_without_question_number()
    {
        await Mark(1); await Mark(2);
        var r = await _tools.ExecuteAsync("record_intake_progress", "{\"status\":\"aborted\"}");
        Assert.Contains("2/20", r);
        Assert.True(_tools.IsComplete());
        Assert.Null(_tools.StatusLine());
    }

    [Fact]
    public async Task State_persists_across_tool_instances()
    {
        await Mark(1); await Mark(2);
        var fresh = new IntakeTools(_db);   // simulates app restart (same DB)
        Assert.Contains("2/20", fresh.StatusLine());
        Assert.Contains("next is Q3", fresh.StatusLine());
    }

    [Fact]
    public async Task HandoffDeduper_senses_retells_and_offers_the_match_and_ledger()
    {
        var dedup = new HandoffDeduper(new FakeEmbedder());
        Assert.Contains("first hand-off", dedup.Ledger());

        Assert.Null(await dedup.MatchAsync("SAFETY CHECK-IN: user said im okay no plan no intent im safe, passive SI clarified"));
        var match = await dedup.MatchAsync("SAFETY CHECK-IN: user said im okay no plan no intent im safe — passive SI, clarified");
        Assert.NotNull(match);                       // retell sensed…
        Assert.Contains("SAFETY CHECK-IN", match);   // …and the model is told WHAT it matched

        Assert.Null(await dedup.MatchAsync("Arc 2 life snapshot: 38, divorced, rents with a roommate, days are work"));
        Assert.NotNull(await dedup.MatchAsync("Arc 2 life snapshot: 38, divorced, rents with a roommate, days are work and games"));

        Assert.Contains("2 so far", dedup.Ledger()); // the ledger sense reflects accepted sends only
    }

    [Fact]
    public async Task Invalid_input_errors_cleanly()
    {
        Assert.StartsWith("error", await Mark(0));
        Assert.StartsWith("error", await Mark(21));
        Assert.StartsWith("error", await _tools.ExecuteAsync("record_intake_progress", "{\"status\":\"maybe\"}"));
        Assert.StartsWith("error", await _tools.ExecuteAsync("record_intake_progress", "{\"status\":\"answered\"}"));
    }
}
