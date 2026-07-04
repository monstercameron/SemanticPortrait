using System.Text.Json;

namespace SemanticPortrait.Core;

/// <summary>
/// Tools for the clean analyst subagent to build the self-model graph (the Constellation):
///  - upsert_node: create/update a node in a domain category.
///  - link_nodes: connect two nodes with a typed edge (auto-creating nodes if needed).
/// Categories should come from the ontology: keystone, distortion, fire, self, heart, wound,
/// mind, body, joy, connection, work, money.
/// </summary>
public sealed class GraphTools : IToolModule
{
    private readonly Db _db;
    private readonly IEmbedder _embedder;
    public GraphTools(Db db, IEmbedder embedder) { _db = db; _embedder = embedder; }

    // Node labels are model-written free text; embedding "category label" makes them semantically
    // findable ("the radar thing" → distortion/rejection-radar) — the third tier of the resolver
    // ladder. Best-effort: a failed embed just means this node resolves by alias/NOCASE only.
    private async Task EmbedNodeAsync(long nodeId)
    {
        var node = _db.GetNode(nodeId);   // fetch back: upsert may have merged onto existing casing
        if (node is null) return;
        var vec = await _embedder.EmbedAsync($"{node.Category} {node.Label}");
        if (vec is not null) _db.UpsertEmbedding("node", nodeId, vec);
    }

    /// <summary>Same-category cosine floor above which a NEW label is treated as a duplicate of an
    /// existing node ("hopium/fantasy re-inflation" vs "fantasy re-inflation" — observed in real
    /// transcripts even after the model read the label list; prompts alone don't hold).</summary>
    public const double NodeDupThreshold = 0.82;
    /// <summary>Longer than this and an edge "type" is a sentence, not a relation
    /// (observed: "amplifies-romantic-meaning-of-warmth").</summary>
    public const int MaxEdgeTypeLength = 24;

    /// <summary>
    /// Deterministic near-duplicate gate for new node labels. Exact/NOCASE/alias matches merge
    /// safely inside Db.UpsertNode — this catches the semantic near-misses those layers can't.
    /// Returns an actionable error string (reuse / alias / force) or null when the label is clean.
    /// Same-category only: the same label in two categories is a legitimate modeling choice.
    /// </summary>
    private async Task<string?> NearDuplicateGateAsync(string category, string label)
    {
        var canonical = _db.ResolveCanonical(label.Trim());
        if (_db.FindNodesByLabel(canonical)
                .Any(n => n.Category.Equals(category, StringComparison.OrdinalIgnoreCase)))
            return null;   // exact reuse — UpsertNode merges it
        var vec = await _embedder.EmbedAsync($"{category} {canonical}");
        if (vec is null) return null;   // no embedder → best-effort, don't block writes
        var near = _db.SearchNodes(vec, 3)
            .Where(h => h.Node.Category.Equals(category, StringComparison.OrdinalIgnoreCase)
                        && !h.Node.Label.Equals(canonical, StringComparison.OrdinalIgnoreCase)
                        && h.Score >= NodeDupThreshold)
            .OrderByDescending(h => h.Score)
            .Select(h => (GraphNode?)h.Node)
            .FirstOrDefault();
        return near is null ? null
            : $"error: '{category}/{canonical}' is a near-duplicate of existing '{near.Category}/{near.Label}'. " +
              $"Reuse that EXACT label, or register_alias(canonical: \"{near.Label}\", mention: \"{canonical}\") " +
              "if it's the same thing under a new name, or resend with force=true ONLY if they are genuinely distinct concepts.";
    }

    /// <summary>Normalize an edge type to a short hyphenated relation; error text if it's prose.</summary>
    private static (string? Type, string? Error) NormalizeEdgeType(string type)
    {
        var t = type.Trim().ToLowerInvariant().Replace(' ', '-');
        return t.Length is 0 or > MaxEdgeTypeLength
            ? (null, $"error: edge type '{type}' is a sentence, not a relation — use a short typed " +
                     $"relation (≤{MaxEdgeTypeLength} chars), e.g. amplifies, counteracts, manufactures.")
            : (t, null);
    }

    private static readonly HashSet<string> _names = new() { "upsert_node", "link_nodes", "register_alias" };
    public bool Handles(string name) => _names.Contains(name);

    private const string CategoryDesc =
        "Domain category. One of: keystone, distortion, fire, self, heart, wound, mind, body, " +
        "joy, connection, work, money.";

    public IReadOnlyList<object> Specs => new object[]
    {
        new
        {
            type = "function",
            name = "upsert_node",
            description = "Add or update a node in the self-model graph. Nodes are DURABLE entities " +
                          "and patterns only: people, recurring themes, distortions, values, fires, " +
                          "wounds — never one-off metaphors, never analysis concepts. Idempotent on " +
                          "(category,label); semantic near-duplicates are rejected with the existing " +
                          "label to reuse (force=true overrides, only for genuinely distinct concepts).",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    category = new { type = "string", description = CategoryDesc },
                    label = new { type = "string", description = "Short node label (e.g. 'rejection-radar', 'Alice')." },
                    inferred = new { type = "boolean", description = "True if this is your inference, not stated." },
                    confidence = new { type = "number", description = "0..1 confidence." },
                    force = new { type = "boolean", description = "Override the near-duplicate rejection — ONLY after judging the suggested existing node genuinely distinct." },
                },
                required = new[] { "category", "label" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "link_nodes",
            description = "Create a typed edge between two nodes (auto-creates the nodes if missing; " +
                          "near-duplicate endpoint labels are rejected like upsert_node). Prefer this " +
                          "relation vocabulary: steals-the-fuel, manufactures, amplifies, counteracts, " +
                          "lifts, walls-out, disproves, evidence-for, prototype-of, triggers, " +
                          "stabilizes, keeps-alive, context-for. Coin new ones sparingly — a relation " +
                          "is ≤24 chars and hyphenated, never a sentence.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    from_category = new { type = "string", description = CategoryDesc },
                    from_label = new { type = "string" },
                    to_category = new { type = "string", description = CategoryDesc },
                    to_label = new { type = "string" },
                    type = new { type = "string", description = "Short typed relation (≤24 chars, hyphenated)." },
                    inferred = new { type = "boolean" },
                    confidence = new { type = "number", description = "0..1." },
                    force = new { type = "boolean", description = "Override near-duplicate rejection of endpoint labels." },
                },
                required = new[] { "from_category", "from_label", "to_category", "to_label", "type" },
                additionalProperties = false,
            },
        },
        new
        {
            type = "function",
            name = "register_alias",
            description = "Record that a mention (nickname, initial, spelling variant) refers to a " +
                          "known entity, so future mentions merge into ONE graph node instead of " +
                          "duplicating (e.g. canonical 'Alice', mention 'Ali'). Also merges any " +
                          "existing duplicate node into the canonical one.",
            parameters = new
            {
                type = "object",
                properties = new
                {
                    canonical = new { type = "string", description = "The entity's canonical name." },
                    mention = new { type = "string", description = "The variant that should resolve to it." },
                    kind = new { type = "string", description = "person | place | project | concept | org (default person)." },
                },
                required = new[] { "canonical", "mention" },
                additionalProperties = false,
            },
        },
    };

    public async Task<string> ExecuteAsync(string name, string argumentsJson)
    {
        try
        {
            using var doc = JsonDocument.Parse(string.IsNullOrWhiteSpace(argumentsJson) ? "{}" : argumentsJson);
            var r = doc.RootElement;

            switch (name)
            {
                case "upsert_node":
                {
                    var cat = Str(r, "category"); var label = Str(r, "label");
                    if (cat is null || label is null) return "error: 'category' and 'label' required.";
                    if (!Bool(r, "force"))
                    {
                        var gate = await NearDuplicateGateAsync(cat, label);
                        if (gate is not null) return gate;
                    }
                    var id = _db.UpsertNode(cat, label, Bool(r, "inferred"), Conf(r));
                    await EmbedNodeAsync(id);
                    return $"node #{id}: {cat}/{label}";
                }
                case "link_nodes":
                {
                    var fc = Str(r, "from_category"); var fl = Str(r, "from_label");
                    var tc = Str(r, "to_category"); var tl = Str(r, "to_label");
                    var rawType = Str(r, "type");
                    if (fc is null || fl is null || tc is null || tl is null || rawType is null)
                        return "error: from/to category+label and type required.";
                    // Tip-off, not a wall: force carries the model's judgment past the length check
                    // too (a deliberately-chosen long relation beats a silently mangled one).
                    var (type, typeErr) = NormalizeEdgeType(rawType);
                    if (typeErr is not null && !Bool(r, "force")) return typeErr;
                    type ??= rawType.Trim().ToLowerInvariant().Replace(' ', '-');
                    if (!Bool(r, "force"))
                    {
                        // Phantom endpoints sneak in through links as easily as through upserts.
                        var gate = await NearDuplicateGateAsync(fc, fl);
                        gate ??= await NearDuplicateGateAsync(tc, tl);
                        if (gate is not null) return gate;
                    }
                    var inferred = Bool(r, "inferred"); var conf = Conf(r);
                    var src = _db.UpsertNode(fc, fl, inferred, conf);
                    var dst = _db.UpsertNode(tc, tl, inferred, conf);
                    _db.AddEdge(src, dst, type!, type!, inferred, conf);
                    await EmbedNodeAsync(src);   // auto-created endpoints need embeddings too
                    await EmbedNodeAsync(dst);
                    return $"linked {fc}/{fl} -[{type}]-> {tc}/{tl}";
                }
                case "register_alias":
                {
                    var canonical = Str(r, "canonical"); var mention = Str(r, "mention");
                    if (canonical is null || mention is null) return "error: 'canonical' and 'mention' required.";
                    var kind = Str(r, "kind") ?? "person";
                    _db.RegisterEntityAlias(canonical, mention, kind);
                    return $"'{mention}' now resolves to '{canonical}' ({kind})";
                }
                default:
                    return $"error: unknown tool '{name}'";
            }
        }
        catch (Exception ex) { return $"error: {ex.Message}"; }
    }

    private static string? Str(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
    private static bool Bool(JsonElement r, string k) =>
        r.TryGetProperty(k, out var v) && (v.ValueKind == JsonValueKind.True);
    private static double Conf(JsonElement r) =>
        r.TryGetProperty("confidence", out var v) && v.TryGetDouble(out var d) ? Math.Clamp(d, 0, 1) : 0.7;
}
