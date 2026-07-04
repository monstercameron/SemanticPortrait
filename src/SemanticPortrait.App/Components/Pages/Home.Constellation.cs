using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;
using SemanticPortrait.App.Services;
using SemanticPortrait.Core;

namespace SemanticPortrait.App.Components.Pages;

// Constellation hosting: builds the VisualModel from the DB and runs the cancellable
// full-graph rebuild with live bloom.
public partial class Home
{
    // --- Constellation rendered from the DB graph -----------------------------
    /// <summary>Rebuild the visual model from the DB (called after any graph mutation).</summary>
    private void LoadGraph() => BuildConstellation();

    /// <summary>Append one metrics snapshot after each reflection (trend data over time).</summary>
    private void SaveMetricsSnapshot()
    {
        try
        {
            if (!Database.IsOpen) return;
            var nodes = Database.GetNodes();
            var edges = Database.GetEdges();
            var payload = System.Text.Json.JsonSerializer.Serialize(new
            {
                nodes = nodes.Count,
                edges = edges.Count,
                inferred = nodes.Count(n => n.Inferred),
                avg_confidence = nodes.Count > 0 ? Math.Round(nodes.Average(n => n.Confidence), 3) : 0,
                connection_density = nodes.Count > 1
                    ? Math.Round(edges.Count / (double)nodes.Count, 3) : 0,
                by_category = nodes.GroupBy(n => n.Category)
                    .ToDictionary(g => g.Key, g => g.Count()),
            });
            Database.SaveMetricsSnapshot(payload);
        }
        catch { /* trend data is best-effort */ }
    }

    // --- Constellation v2 (decoupled bundle → Observatory/Sigil views) --------
    private SemanticPortrait.Core.Constellation.VisualModel? _constModel;
    private SemanticPortrait.Core.Constellation.SigilPainting? _sigil;
    private bool _constSample;    // prototyping toggle: sample fixture (design) vs live graph
    private bool _sigilMode;      // Observatory (private, labeled) ↔ Sigil (public, masked)

    /// <summary>Rebuild both renderings from the source (async: the live source runs semantic
    /// join + mood classification through the embedder). Fire-and-forget callers use LoadGraph().</summary>
    private string? _memorySpan;   // "14 months" — HUD vital sign

    private async Task BuildConstellationAsync()
    {
        SemanticPortrait.Core.Constellation.IConstellationSource src = _constSample
            ? new SemanticPortrait.Core.Constellation.SampleConstellationSource()
            : ConstSource;
        var bundle = await src.BuildAsync();
        _constModel = bundle.Visual;
        _sigil = bundle.Sigil;
        try
        {
            if (Database.IsOpen)
            {
                var meta = Database.GetAllEntryMeta();
                var dated = meta.Select(m => Compactor.ParseUtc(m.EntryUtc))
                    .Where(d => d != DateTime.MinValue).ToList();
                if (dated.Count >= 2)
                {
                    var months = (dated.Max() - dated.Min()).TotalDays / 30.44;
                    _memorySpan = months < 1.5 ? "weeks"
                        : months < 24 ? $"{months:0} months"
                        : $"{months / 12:0} years";   // an imported life reads in years, not "336 months"
                }
                else _memorySpan = null;
            }
        }
        catch { /* HUD garnish — never let it block the map */ }
        await InvokeAsync(StateHasChanged);
    }

    private void BuildConstellation() => _ = BuildConstellationAsync().Guard("constellation-build");

    private void ToggleConstSample() { _constSample = !_constSample; BuildConstellation(); }
    private void ToggleSigilMode() => _sigilMode = !_sigilMode;

    // --- ambient mode: the sigil, fullscreen, living -----------------------------
    private bool _ambient;
    private void ToggleAmbient() => _ambient = !_ambient;

    // --- time scrub: the portrait as it was ---------------------------------------
    private bool _scrubActive;
    private double _scrubPos = 100;          // 0 = first entry, 100 = now
    private DateTime _scrubFromUtc;
    private DateTime? _scrubAsOf;            // null = live
    private bool _scrubBuilding;
    private double? _scrubQueued;            // latest requested position while a build runs

    private string ScrubLabel => _scrubAsOf is { } t ? t.ToLocalTime().ToString("MMM d, yyyy") : "now";

    private void ToggleScrub()
    {
        if (!Database.IsOpen) return;   // a lock mid-session must not turn a click into a crash
        _scrubActive = !_scrubActive;
        if (_scrubActive)
        {
            var first = Database.GetAllEntryMeta().Select(m => Compactor.ParseUtc(m.EntryUtc))
                .Where(d => d != DateTime.MinValue).DefaultIfEmpty(DateTime.UtcNow.AddDays(-30)).Min();
            _scrubFromUtc = first;
            _scrubPos = 100;
        }
        else { _scrubAsOf = null; BuildConstellation(); }   // leaving the scrub returns to live
    }

    private void OnScrub(ChangeEventArgs e)
    {
        if (!double.TryParse(e.Value?.ToString(), out var pos)) return;
        _scrubPos = pos;
        // Coalesce the slider's event stream: one build at a time, latest position wins.
        if (_scrubBuilding) { _scrubQueued = pos; return; }
        _ = ScrubToAsync(pos).Guard("constellation-scrub");
    }

    private async Task ScrubToAsync(double pos)
    {
        _scrubBuilding = true;
        try
        {
            while (true)
            {
                var span = DateTime.UtcNow - _scrubFromUtc;
                _scrubAsOf = pos >= 99.5 ? null : _scrubFromUtc + span * (pos / 100.0);
                var bundle = await ConstSource.BuildAsync(_scrubAsOf);
                _constModel = bundle.Visual;
                _sigil = bundle.Sigil;
                await InvokeAsync(StateHasChanged);
                if (_scrubQueued is not { } next) break;
                _scrubQueued = null;
                pos = next;
            }
        }
        finally { _scrubBuilding = false; }
    }

    /// <summary>Inspector's "ask the agent" — jump to the chat with the node pre-quoted, so the
    /// main agent (which has recall/portrait tools) picks the thread up with full context.</summary>
    private void AskAboutNode(SemanticPortrait.Core.Constellation.VisualNode n)
    {
        _draft = $"About “{n.Label}” on my map: ";
        _showConstellation = false;
        _restoreChatScroll = true;
        _focusNext = true;
        StateHasChanged();
    }

    private async Task RejectFromConstellation(long id)
    {
        if (!Database.IsOpen) return;
        Database.DeleteNode(id);
        LoadGraph();                 // rebuilds the model + clears selection
        await InvokeAsync(StateHasChanged);
    }

    private enum RState { Idle, Running, Cancelled, Empty }
    private RState _rebuildState = RState.Idle;
    private bool _showRebuildConfirm;
    private System.Threading.CancellationTokenSource? _rebuildCts;
    private string _rebuildStatus = "";
    private int _rebuildCount;
    private double _rebuildStartCost;
    private long _rebuildStartTokens;

    private double RebuildCost => Math.Max(0, Usage.CostUsd - _rebuildStartCost);
    private long RebuildTokens => Math.Max(0, Usage.Total - _rebuildStartTokens);

    private void CancelRebuild() => _rebuildCts?.Cancel();

    /// <summary>
    /// Re-run the clean-room analyst over ALL known facts to regenerate the graph, re-rendering after
    /// every tool call so nodes/threads bloom live. Cancellable; cost-tracked; honest empty-state.
    /// </summary>
    private async Task RunRebuild(bool restart)
    {
        _showRebuildConfirm = false;
        if (_rebuildState == RState.Running || !Database.IsOpen) return;

        var facts = GatherFacts();
        if (facts is null)
        {
            _rebuildState = RState.Empty;     // dev sandbox / brand-new: nothing known yet
            await InvokeAsync(StateHasChanged);
            return;
        }

        if (restart) ClearGraph();
        if (_constSample) ToggleConstSample();   // watch the LIVE graph populate, not the sample

        _rebuildCts = new System.Threading.CancellationTokenSource();
        _rebuildState = RState.Running;
        _rebuildCount = 0;
        _rebuildStatus = "reading everything you've shared…";
        _rebuildStartCost = Usage.CostUsd;
        _rebuildStartTokens = Usage.Total;
        await InvokeAsync(StateHasChanged);

        try
        {
            await Analyst.ImportAsync(facts, onToolCall: (name, detail) =>
            {
                _rebuildCount++;
                _rebuildStatus = ToolBloomLine(name);
                _ = InvokeAsync(() => { LoadGraph(); StateHasChanged(); }).Guard("rebuild-bloom");   // live bloom
            }, ct: _rebuildCts.Token);
            _rebuildState = RState.Idle;        // finished cleanly → overlay closes
        }
        catch (OperationCanceledException) { _rebuildState = RState.Cancelled; _rebuildStatus = "paused — pick up where you left off or restart"; }
        catch (Exception ex) { _rebuildState = RState.Cancelled; _rebuildStatus = "hit a snag: " + (ex.Message.Length > 80 ? ex.Message[..80] + "…" : ex.Message); }
        finally { LoadGraph(); await InvokeAsync(StateHasChanged); }
    }

    /// <summary>Collect everything known about the user; null if there's genuinely nothing yet.</summary>
    private string? GatherFacts()
    {
        var notes = Database.GetNotes();
        var events = Database.GetEvents();
        var msgs = Database.GetMessages().Where(m => m.Role == "user").TakeLast(40).ToList();
        if (notes.Count == 0 && events.Count == 0 && msgs.Count == 0) return null;

        var sb = new System.Text.StringBuilder();
        sb.AppendLine("Rebuild the self-model graph from EVERYTHING known about the user below. " +
                      "Create/refine nodes and link them with typed edges.\n");
        if (notes.Count > 0) { sb.AppendLine("## Distilled notes:"); foreach (var n in notes) sb.AppendLine("- " + n.Text); }
        if (events.Count > 0) { sb.AppendLine("\n## Events:"); foreach (var e in events) sb.AppendLine($"- {e.Summary}"); }
        if (msgs.Count > 0) { sb.AppendLine("\n## Recent entries:"); foreach (var m in msgs) sb.AppendLine("- " + m.Text); }
        return sb.ToString();
    }

    private void ClearGraph()
    {
        foreach (var n in Database.GetNodes()) Database.DeleteNode(n.Id);   // also drops touching edges
        LoadGraph();
    }

    private static string ToolBloomLine(string tool) => tool switch
    {
        "upsert_node" => "✦ a new star appears…",
        "link_nodes" => "✦ threads connecting…",
        "save_note" or "refine_note" => "📝 distilling an insight…",
        "log_event" => "📅 placing a moment in time…",
        _ => "✦ mapping you…",
    };
}
