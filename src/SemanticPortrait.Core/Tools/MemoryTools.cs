using System.Text;
using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Memory tools exposed to the model:
///  - search_memory: semantic recall over entries + notes (embeddings + cosine).
///  - save_note: persist a durable distilled insight (embedded for future recall).
///  - refine_note: update an existing note and re-embed it (refining older notes).
/// </summary>
public sealed class MemoryTools
{
    private readonly Db _db;
    private readonly IEmbedder _embedder;

    public MemoryTools(Db db, IEmbedder embedder) { _db = db; _embedder = embedder; }

    private static readonly HashSet<string> _names = new()
        { "search_memory", "search_past_analysis", "save_note", "refine_note" };

    public bool Handles(string name) => _names.Contains(name);

    private static readonly object SearchNotesSpec = new
    {
        type = "function",
        name = "search_past_analysis",
        description = "Search ONLY your prior analysis (your saved notes) — never the raw chat. " +
                      "Use this to research what you already concluded before recording or refining, " +
                      "so you build on past reads instead of duplicating or contradicting them. " +
                      "Returns note ids you can refine.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "Theme/person/pattern to look up in your notes." },
                k = new { type = "integer", description = "How many (default 6)." },
            },
            required = new[] { "query" },
            additionalProperties = false,
        },
    };

    /// <summary>Notes-only recall specs for the clean analyst (no raw-chat access).</summary>
    public IReadOnlyList<object> AnalystSpecs => new[] { SearchNotesSpec, SaveSpec, RefineSpec };

    private static readonly object SearchSpec = new
    {
        type = "function",
        name = "search_memory",
        description = "Semantically search everything the user has written and every note you've " +
                      "saved (the whole thread). Recall relevant past context before answering — " +
                      "don't rely on the recent window alone. Results include note ids you can refine.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                query = new { type = "string", description = "What to look for (a theme, person, feeling, event)." },
                k = new { type = "integer", description = "How many results (default 5)." },
            },
            required = new[] { "query" },
            additionalProperties = false,
        },
    };

    private static readonly object SaveSpec = new
    {
        type = "function",
        name = "save_note",
        description = "Save a durable, distilled insight about the user (a pattern, a stable fact, a " +
                      "working hypothesis). Stored and embedded for future recall. Returns the note id.",
        parameters = new
        {
            type = "object",
            properties = new { text = new { type = "string", description = "The insight to remember." } },
            required = new[] { "text" },
            additionalProperties = false,
        },
    };

    private static readonly object RefineSpec = new
    {
        type = "function",
        name = "refine_note",
        description = "Refine/replace an existing note (found via search_memory) with an updated " +
                      "version, and re-embed it. Use when your understanding sharpens or corrects.",
        parameters = new
        {
            type = "object",
            properties = new
            {
                id = new { type = "integer", description = "The note id to refine." },
                text = new { type = "string", description = "The new, refined note text." },
            },
            required = new[] { "id", "text" },
            additionalProperties = false,
        },
    };

    /// <summary>Full specs (read + write) — for the clean analyst subagent.</summary>
    public IReadOnlyList<object> Specs => new[] { SearchSpec, SaveSpec, RefineSpec };

    /// <summary>Read-only specs — for the main chat agent (writes go via the subagent).</summary>
    public IReadOnlyList<object> ReadSpecs => new[] { SearchSpec };

    public async Task<string> ExecuteAsync(string name, string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var root = doc.RootElement;

            switch (name)
            {
                case "search_memory":
                case "search_past_analysis":
                {
                    var query = root.TryGetProperty("query", out var q) ? q.GetString() : null;
                    var k = root.TryGetProperty("k", out var kk) && kk.TryGetInt32(out var kv) ? Math.Clamp(kv, 1, 20) : (name == "search_past_analysis" ? 6 : 5);
                    if (string.IsNullOrWhiteSpace(query)) return "error: 'query' is required.";
                    var vec = await _embedder.EmbedAsync(query);
                    if (vec is null) return "error: could not embed the query.";
                    var hits = name == "search_past_analysis" ? _db.SearchNotes(vec, k) : _db.Search(vec, k);
                    if (hits.Count == 0) return name == "search_past_analysis" ? "(no prior analysis yet)" : "(nothing stored yet)";
                    var sb = new StringBuilder();
                    foreach (var h in hits)
                    {
                        var label = h.RefType == "note" ? $"note #{h.RefId}" : "entry";
                        // Notes come back UNTRIMMED: refine_note replaces the whole note, so letting
                        // the model rewrite one it only saw a truncated view of silently loses the tail.
                        // Raw entries stay trimmed (they can be long; recall only needs the gist).
                        var text = h.RefType == "note" ? h.Text : Trim(h.Text, 280);
                        sb.AppendLine($"- [{label} · {h.CreatedUtc} · sim {h.Score:0.00}] {text}");
                    }
                    return sb.ToString().TrimEnd();
                }

                case "save_note":
                {
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text)) return "error: 'text' is required.";
                    var id = _db.AddNote(text, DateTime.UtcNow.ToString("o"));
                    var vec = await _embedder.EmbedAsync(text);
                    if (vec is not null) _db.UpsertEmbedding("note", id, vec);
                    return $"saved note #{id}";
                }

                case "refine_note":
                {
                    if (!root.TryGetProperty("id", out var idEl) || !idEl.TryGetInt64(out var id))
                        return "error: 'id' is required.";
                    var text = root.TryGetProperty("text", out var t) ? t.GetString() : null;
                    if (string.IsNullOrWhiteSpace(text)) return "error: 'text' is required.";
                    if (!_db.UpdateNote(id, text, DateTime.UtcNow.ToString("o")))
                        return $"error: note #{id} not found.";
                    var vec = await _embedder.EmbedAsync(text);
                    if (vec is not null) _db.UpsertEmbedding("note", id, vec);  // re-embed
                    return $"refined note #{id}";
                }

                default:
                    return $"error: unknown tool '{name}'";
            }
        }
        catch (Exception ex)
        {
            return $"error: {ex.Message}";
        }
    }

    private static string Trim(string s, int n) => s.Length <= n ? s : s[..n] + "…";
}
