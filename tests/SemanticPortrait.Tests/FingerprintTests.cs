using SemanticPortrait.Core;
using SemanticPortrait.Core.Constellation;

namespace SemanticPortrait.Tests;

/// <summary>
/// The generative fingerprint (design §M5): metric → visual-channel mappings that are continuous
/// (never thresholds), per-map normalized (proportions, not data volume), deterministic, and
/// explainable — every channel names its metric and its visual. Your data, not a type.
/// </summary>
public class FingerprintTests
{
    private static NodeMetric N(long id, string cat = "mind", double salience = 0.5,
        double centrality = 0.5, double valence = 0, bool inferred = false,
        double? volatility = null, bool quiet = false) =>
        new(id, $"node-{id}", cat, inferred, 0.8, Degree: 2, Contradiction: 0, Complexity: 1,
            Sides: 4, IsCircle: false, Emotion.Neutral, valence, Intensity: 0.5, Energy: 0.5,
            volatility, salience, centrality, ReferencingEntries: quiet ? 0 : 3, quiet);

    private static EdgeMetric E(long id, long src, long dst, bool inferred = false) =>
        new(id, src, dst, Valence: 1, Uncertainty: 0.2, Confidence: 0.8, inferred,
            Activity: 0.5, ParallelIndex: 0);

    private static ConstellationModel M(NodeMetric[] nodes, EdgeMetric[] edges) =>
        new(nodes, edges, new JoinReport(nodes.Length, nodes.Length, Array.Empty<string>()));

    [Fact]
    public void Seven_channels_all_bounded_and_explainable()
    {
        var m = M(new[] { N(1, "fire", inferred: true, volatility: 0.4), N(2, "distortion"), N(3) },
                  new[] { E(1, 1, 2), E(2, 2, 3, inferred: true) });
        var fp = FingerprintMetrics.Compute(m);

        Assert.Equal(7, fp.Channels.Count);
        foreach (var ch in fp.Channels)
        {
            Assert.InRange(ch.Value, 0, 1);
            Assert.False(string.IsNullOrWhiteSpace(ch.Name));
            Assert.False(string.IsNullOrWhiteSpace(ch.Metric));   // the honesty contract
            Assert.False(string.IsNullOrWhiteSpace(ch.Visual));
        }
    }

    [Fact]
    public void Deterministic_same_model_same_sky()
    {
        var m = M(new[] { N(1, "fire"), N(2, "distortion", volatility: 0.6), N(3, valence: 0.4) },
                  new[] { E(1, 1, 2), E(2, 2, 3) });
        var a = FingerprintMetrics.Compute(m);
        var b = FingerprintMetrics.Compute(m);
        foreach (var (x, y) in a.Channels.Zip(b.Channels))
            Assert.Equal(x.Value, y.Value);
    }

    [Fact]
    public void Fire_raises_warmth_distortion_raises_motion()
    {
        var calm = M(new[] { N(1), N(2) }, new[] { E(1, 1, 2) });
        var fiery = M(new[] { N(1, "fire", salience: 0.9), N(2) }, new[] { E(1, 1, 2) });
        var distorted = M(new[] { N(1, "distortion", salience: 0.9), N(2) }, new[] { E(1, 1, 2) });

        Assert.True(FingerprintMetrics.Compute(fiery)["warmth"] > FingerprintMetrics.Compute(calm)["warmth"]);
        Assert.True(FingerprintMetrics.Compute(distorted)["motion"] > FingerprintMetrics.Compute(calm)["motion"]);
    }

    [Fact]
    public void Proportions_not_volume_a_duplicated_life_wears_the_same_sky()
    {
        var one = M(
            new[] { N(1, "fire", salience: 0.8, valence: 0.5), N(2, "distortion", inferred: true), N(3) },
            new[] { E(1, 1, 2), E(2, 2, 3), E(3, 1, 3) });
        // the same shape twice over (disjoint copy) — twice the data, the same person
        var two = M(
            new[] { N(1, "fire", salience: 0.8, valence: 0.5), N(2, "distortion", inferred: true), N(3),
                    N(11, "fire", salience: 0.8, valence: 0.5), N(12, "distortion", inferred: true), N(13) },
            new[] { E(1, 1, 2), E(2, 2, 3), E(3, 1, 3), E(11, 11, 12), E(12, 12, 13), E(13, 11, 13) });

        var a = FingerprintMetrics.Compute(one);
        var b = FingerprintMetrics.Compute(two);
        foreach (var key in new[] { "breadth", "motion", "warmth", "tint", "weave", "texture" })
            Assert.True(Math.Abs(a[key] - b[key]) < 0.01, $"{key}: {a[key]} vs {b[key]}");
        // keystone is EXPECTED to drop: two equal pillars genuinely means less single-anchor dependence
    }

    [Fact]
    public void Keystone_reads_concentration_not_size()
    {
        var pillar = M(new[] { N(1, centrality: 1.0), N(2, centrality: 0.05), N(3, centrality: 0.05) },
                       new[] { E(1, 1, 2) });
        var even = M(new[] { N(1, centrality: 0.5), N(2, centrality: 0.5), N(3, centrality: 0.5) },
                     new[] { E(1, 1, 2) });
        Assert.True(FingerprintMetrics.Compute(pillar)["keystone"] > FingerprintMetrics.Compute(even)["keystone"] + 0.2);
    }

    [Fact]
    public void Weave_needs_real_communities_not_stray_pairs()
    {
        var pairs = M(new[] { N(1), N(2), N(3), N(4) }, new[] { E(1, 1, 2), E(2, 3, 4) });
        var woven = M(new[] { N(1), N(2), N(3), N(4) }, new[] { E(1, 1, 2), E(2, 2, 3), E(3, 3, 4) });
        Assert.Equal(0, FingerprintMetrics.Compute(pairs)["weave"]);
        Assert.Equal(1, FingerprintMetrics.Compute(woven)["weave"]);
    }

    [Fact]
    public void Empty_model_is_a_quiet_zero_sky_not_a_crash()
    {
        var fp = FingerprintMetrics.Compute(M(Array.Empty<NodeMetric>(), Array.Empty<EdgeMetric>()));
        foreach (var ch in fp.Channels)
            if (ch.Key != "tint") Assert.Equal(0, ch.Value);
        Assert.Equal(0.5, fp["tint"]);   // no data = neutral valence, not "miserable"
    }

    [Fact]
    public void Encoding_build_carries_the_fingerprint()
    {
        var nodes = new[] { new GraphNode(1, "core", "self", false, 0.9), new GraphNode(2, "fire", "creating", false, 0.9) };
        var edges = new[] { new GraphEdge(1, 1, 2, "lifts", "lifts", false, 0.8) };
        var model = ConstellationMetrics.Compute(nodes, edges, Array.Empty<SemanticPortrait.Core.EntryMeta>(), DateTime.UtcNow);
        var vm = Encoding.Build(model);
        Assert.NotNull(vm.Fp);
        Assert.Equal(7, vm.Fp!.Channels.Count);
    }

    /// <summary>The backlog's "comparison study", honestly scoped as a SYNTHETIC check: two
    /// differently-shaped personas must wear visibly different skies. (Whether real humans
    /// cluster this way still needs real humans — this proves the channels CAN separate.)</summary>
    [Fact]
    public void Synthetic_personas_diverge_visibly()
    {
        // Persona A — inward-shaped: sparse ties, one tight cluster, low fire, negative lean.
        var a = M(
            new[]
            {
                N(1, "wound", salience: 0.8, centrality: 0.9, valence: -0.6, volatility: 0.5),
                N(2, "distortion", salience: 0.7, centrality: 0.2, valence: -0.5),
                N(3, "mind", salience: 0.4, centrality: 0.2, valence: -0.2),
                N(4, "connection", salience: 0.2, centrality: 0.1, valence: -0.3),
                N(5, "mind", salience: 0.2, centrality: 0.1, valence: -0.1),
            },
            new[] { E(1, 1, 2), E(2, 1, 3) });

        // Persona B — outward-shaped: dense weave, several communities, high fire, positive lean.
        var b = M(
            new[]
            {
                N(1, "fire", salience: 0.9, centrality: 0.5, valence: 0.7),
                N(2, "joy", salience: 0.8, centrality: 0.5, valence: 0.6),
                N(3, "connection", salience: 0.7, centrality: 0.5, valence: 0.5),
                N(4, "connection", salience: 0.7, centrality: 0.5, valence: 0.6),
                N(5, "fire", salience: 0.6, centrality: 0.5, valence: 0.4),
                N(6, "connection", salience: 0.5, centrality: 0.5, valence: 0.5),
            },
            new[] { E(1, 1, 2), E(2, 2, 3), E(3, 3, 4), E(4, 4, 5), E(5, 5, 6), E(6, 6, 1), E(7, 1, 3), E(8, 2, 4), E(9, 3, 5), E(10, 1, 4), E(11, 2, 5), E(12, 4, 6) });

        var fa = FingerprintMetrics.Compute(a);
        var fb = FingerprintMetrics.Compute(b);

        Assert.True(fb["breadth"] - fa["breadth"] > 0.25, $"breadth: {fa["breadth"]} vs {fb["breadth"]}");
        Assert.True(fb["warmth"] - fa["warmth"] > 0.4, $"warmth: {fa["warmth"]} vs {fb["warmth"]}");
        Assert.True(fb["tint"] - fa["tint"] > 0.3, $"tint: {fa["tint"]} vs {fb["tint"]}");
        Assert.True(fa["motion"] - fb["motion"] > 0.15, $"motion: {fa["motion"]} vs {fb["motion"]}");

        // aggregate separation across all channels — the two skies are unmistakably different
        double l1 = fa.Channels.Zip(fb.Channels).Sum(p => Math.Abs(p.First.Value - p.Second.Value));
        Assert.True(l1 > 1.2, $"aggregate channel separation too small: {l1}");
    }
}
