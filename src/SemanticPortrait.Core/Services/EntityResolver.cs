namespace SemanticPortrait.Core;

/// <summary>How a name was resolved — callers surface anything fuzzier than 'exact' to the model,
/// same stated-vs-inferred discipline as the rest of the system.</summary>
public enum ResolutionVia { Exact, Alias, Semantic, Unresolved }

/// <summary>A name resolved to the self-model. <see cref="Nodes"/> may hold several nodes (the same
/// label can legitimately live in multiple categories); empty when unresolved.</summary>
public sealed record ResolvedEntity(string Query, string Canonical, IReadOnlyList<GraphNode> Nodes, ResolutionVia Via)
{
    public bool IsResolved => Via != ResolutionVia.Unresolved;
    /// <summary>Human-readable provenance suffix for bundles, e.g. "\"P\" → Priya (alias)".</summary>
    public string Provenance => Via switch
    {
        ResolutionVia.Alias => $"\"{Query}\" → {Canonical} (alias)",
        ResolutionVia.Semantic => $"\"{Query}\" → {Canonical} (closest match — inferred)",
        _ => Canonical,
    };
}

/// <summary>
/// The name-resolution ladder for model-written free text (labels, people, mentions). Everything
/// name-shaped in the DB was written by a model across many independent sessions, so exact-match
/// SQL silently misses — resolution goes: (1) alias registry, (2) NOCASE label match,
/// (3) embedding similarity over node labels. Each tier is cheaper/more trustworthy than the next;
/// the Via on the result says which one fired so callers can mark fuzzy joins as inferred.
/// </summary>
public sealed class EntityResolver
{
    private readonly Db _db;
    private readonly IEmbedder _embedder;

    /// <summary>Below this cosine similarity a semantic match is noise, not a resolution.
    /// Conservative on purpose: a wrong entity join poisons everything built on top of it.</summary>
    public const double MinSemanticSimilarity = 0.60;

    public EntityResolver(Db db, IEmbedder embedder) { _db = db; _embedder = embedder; }

    public async Task<ResolvedEntity> ResolveAsync(string mention, CancellationToken ct = default)
    {
        mention = mention.Trim();
        if (mention.Length == 0) return new(mention, mention, Array.Empty<GraphNode>(), ResolutionVia.Unresolved);

        // Tier 1+2: alias registry, then NOCASE node lookup on the canonical name.
        var canonical = _db.ResolveCanonical(mention);
        var viaAlias = !string.Equals(canonical, mention, StringComparison.OrdinalIgnoreCase);
        var nodes = _db.FindNodesByLabel(canonical);
        if (nodes.Count > 0)
            return new(mention, nodes[0].Label, nodes, viaAlias ? ResolutionVia.Alias : ResolutionVia.Exact);
        if (viaAlias)   // registry knows the name even though no graph node exists yet
            return new(mention, canonical, Array.Empty<GraphNode>(), ResolutionVia.Alias);

        // Tier 3: embedding similarity over node labels ("the radar thing" → rejection-radar).
        var vec = await _embedder.EmbedAsync(mention);
        if (vec is not null)
        {
            var hits = _db.SearchNodes(vec, 3);
            var best = hits.FirstOrDefault();
            if (best.Node is not null && best.Score >= MinSemanticSimilarity)
            {
                // Same label may exist in several categories — return all of them, not just the top hit.
                var all = _db.FindNodesByLabel(best.Node.Label);
                return new(mention, best.Node.Label, all.Count > 0 ? all : new[] { best.Node }, ResolutionVia.Semantic);
            }
        }
        return new(mention, mention, Array.Empty<GraphNode>(), ResolutionVia.Unresolved);
    }

    /// <summary>All name variants that should match this entity in free text (canonical + aliases).
    /// Used to expand substring searches (notes, entry_meta people/topics).</summary>
    public List<string> Variants(string canonical)
    {
        var variants = new List<string> { canonical };
        var entity = _db.GetEntities().FirstOrDefault(e =>
            string.Equals(e.Canonical, canonical, StringComparison.OrdinalIgnoreCase));
        if (entity.Aliases is { Length: > 0 })
            variants.AddRange(entity.Aliases.Split(", ", StringSplitOptions.RemoveEmptyEntries));
        return variants.Distinct(StringComparer.OrdinalIgnoreCase).ToList();
    }
}

/// <summary>
/// One-time (idempotent) backfill: nodes and events written before embed-on-write existed get
/// embeddings so the resolver's semantic tier and timeline search see the whole graph, not just
/// new rows. Cheap to re-run — rows that already have embeddings are skipped in SQL.
/// </summary>
public sealed class EmbeddingBackfill
{
    private readonly Db _db;
    private readonly IEmbedder _embedder;
    public EmbeddingBackfill(Db db, IEmbedder embedder) { _db = db; _embedder = embedder; }

    /// <summary>Returns how many rows were embedded (0 = nothing was missing).</summary>
    public async Task<int> RunAsync(CancellationToken ct = default)
    {
        var done = 0;
        foreach (var n in _db.GetNodesWithoutEmbedding())
        {
            ct.ThrowIfCancellationRequested();
            var vec = await _embedder.EmbedAsync($"{n.Category} {n.Label}");
            if (vec is not null) { _db.UpsertEmbedding("node", n.Id, vec); done++; }
        }
        foreach (var e in _db.GetEventsWithoutEmbedding())
        {
            ct.ThrowIfCancellationRequested();
            var vec = await _embedder.EmbedAsync(e.Summary);
            if (vec is not null) { _db.UpsertEmbedding("event", e.Id, vec); done++; }
        }
        return done;
    }
}
