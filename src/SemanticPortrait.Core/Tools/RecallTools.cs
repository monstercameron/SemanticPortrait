using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Tool surface over <see cref="RecallEngine"/>. Two lanes, enforced by which spec list a caller
/// wires up:
///  - MainSpecs (live chat agent): recall + portrait. recall returns RAW chat excerpts, so it
///    must never reach the clean-room analyst.
///  - AnalystSpecs (clean-room): portrait + list_node_labels only — everything they return is
///    analyst-authored (graph/notes/events/entry summaries), never the raw thread.
/// </summary>
public sealed class RecallTools
{
    private readonly RecallEngine _engine;
    public RecallTools(RecallEngine engine) => _engine = engine;

    private static readonly HashSet<string> _names = new() { "recall", "portrait", "list_node_labels" };
    public bool Handles(string name) => _names.Contains(name);

    private static readonly object RecallSpec = new
    {
        type = "function",
        name = "recall",
        description = "ONE dense lookup across everything stored: semantic matches over the whole " +
                      "thread + your saved notes, each match enriched with the user's recorded mood " +
                      "at that moment and (for top matches) the surrounding exchange. Add person= to " +
                      "pull that person's connections + notes; add from/to ISO dates to pull the " +
                      "event timeline + weekly mood trend for that window. Prefer one well-aimed " +
                      "call with the right arguments over multiple calls.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "What to look for (theme, feeling, event, person, phrase)." },
                person = new { type = "string", description = "Optional: a person/entity to expand (aliases resolve automatically)." },
                from = new { type = "string", description = "Optional ISO date: start of a timeline window." },
                to = new { type = "string", description = "Optional ISO date: end of a timeline window." },
                k = new { type = "integer", description = "How many matches (default 5, max 12)." },
            },
            required = new[] { "query" },
            additionalProperties = false,
        },
    };

    private static readonly object PortraitSpec = new
    {
        type = "function",
        name = "portrait",
        description = "The interconnected picture of one person/theme/pattern from the self-model " +
                      "graph: their connections (typed threads), the notes about them, their event " +
                      "timeline, and the user's recorded states when they came up. Pass " +
                      "focus='overview' for a map of the whole self-model. Use for 'big picture on X' " +
                      "— use recall for specific memories.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                focus = new { type = "string", description = "Person, theme, pattern, node label — or 'overview'." },
            },
            required = new[] { "focus" },
            additionalProperties = false,
        },
    };

    private static readonly object ListLabelsSpec = new
    {
        type = "function",
        name = "list_node_labels",
        description = "All existing node labels in the self-model graph, grouped by category. Check " +
                      "this BEFORE upsert_node/link_nodes so you reuse existing labels (and " +
                      "register_alias against the real canonical name) instead of creating near-" +
                      "duplicate nodes.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                category = new { type = "string", description = "Optional: one category only." },
            },
            additionalProperties = false,
        },
    };

    /// <summary>Live chat agent lane (read-only; includes raw-chat excerpts via recall).</summary>
    public IReadOnlyList<object> MainSpecs => new[] { RecallSpec, PortraitSpec };

    /// <summary>Clean-room analyst lane: analyst-authored stores only — NO raw chat, ever.</summary>
    public IReadOnlyList<object> AnalystSpecs => new[] { PortraitSpec, ListLabelsSpec };

    public async Task<string> ExecuteAsync(string name, string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var r = doc.RootElement;
            switch (name)
            {
                case "recall":
                {
                    var query = Str(r, "query");
                    if (string.IsNullOrWhiteSpace(query)) return "error: 'query' is required.";
                    var k = r.TryGetProperty("k", out var kk) && kk.TryGetInt32(out var kv) ? kv : 5;
                    return await _engine.RecallAsync(query!, Str(r, "person"), Str(r, "from"), Str(r, "to"), k);
                }
                case "portrait":
                {
                    var focus = Str(r, "focus");
                    if (string.IsNullOrWhiteSpace(focus)) return "error: 'focus' is required.";
                    return await _engine.PortraitAsync(focus!);
                }
                case "list_node_labels":
                    return _engine.ListNodeLabels(Str(r, "category"));
                default:
                    return $"error: unknown tool '{name}'";
            }
        }
        catch (Exception ex) { return $"error: {ex.Message}"; }
    }

    private static string? Str(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}
