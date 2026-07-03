using System.Collections;
using System.Reflection;
using SemanticPortrait.Core;
using SemanticPortrait.Core.Constellation;

namespace SemanticPortrait.Tests;

/// <summary>
/// The Sigil contract: masked by construction (numbers only — verified by reflection),
/// deterministic (same model → same painting), stable (small changes don't reshuffle the image),
/// divergent (different inner worlds paint structurally different images).
/// </summary>
public class SigilTests
{
    private static readonly DateTime Now = new(2026, 7, 2, 0, 0, 0, DateTimeKind.Utc);
    private static string Day(int d) => new DateTime(2026, 6, d, 0, 0, 0, DateTimeKind.Utc).ToString("o");

    private static ConstellationModel Model(
        (string Cat, string Label)[] nodes, (int A, int B, string T)[] edges, (string Mood, double Val, string Topic)[] entries)
    {
        var gn = nodes.Select((n, i) => new GraphNode(i + 1, n.Cat, n.Label, false, 0.9)).ToList();
        var ge = edges.Select((e, i) => new GraphEdge(i + 1, e.A, e.B, e.T, e.T, false, 0.8)).ToList();
        var em = entries.Select((e, i) => new EntryMeta(100 + i, Day(10 + i), e.Mood, e.Val, 0.6, 0.5,
            $"[\"{e.Topic}\"]", "[]", "s")).ToList();
        return ConstellationMetrics.Compute(gn, ge, em, Now);
    }

    private static ConstellationModel Rich() => Model(
        new[] { ("fire", "creating"), ("connection", "Priya"), ("wound", "pining"), ("distortion", "radar") },
        new[] { (1, 2, "lifts"), (2, 3, "amplifies"), (4, 3, "amplifies") },
        new[] { ("hopeful", 0.5, "creating"), ("sad", -0.6, "pining"), ("anxious", -0.4, "radar"), ("happy", 0.7, "Priya") });

    // ---------------------------------------------------------------- masking

    [Fact]
    public void Sigil_types_carry_no_strings_by_construction()
    {
        // The masking contract is architectural: walk every public property of the painting
        // object graph and assert nothing string-typed exists anywhere in it.
        var offenders = new List<string>();
        void Walk(Type t, string path, HashSet<Type> seen)
        {
            if (!seen.Add(t)) return;
            foreach (var p in t.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                var pt = p.PropertyType;
                if (pt == typeof(string)) { offenders.Add($"{path}.{p.Name}"); continue; }
                if (pt.IsPrimitive || pt == typeof(double) || pt == typeof(int)) continue;
                var elem = pt.IsArray ? pt.GetElementType()
                    : pt.IsGenericType && typeof(IEnumerable).IsAssignableFrom(pt) ? pt.GetGenericArguments()[0]
                    : pt;
                if (elem == typeof(string)) { offenders.Add($"{path}.{p.Name}[]"); continue; }
                if (elem is not null && !elem.IsPrimitive && elem != typeof(double) && elem != typeof(int))
                    Walk(elem, $"{path}.{p.Name}", seen);
            }
        }
        Walk(typeof(SigilPainting), "SigilPainting", new HashSet<Type>());
        Assert.True(offenders.Count == 0, "string-typed members found: " + string.Join(", ", offenders));
    }

    // ------------------------------------------------------------ determinism

    [Fact]
    public void Same_model_paints_the_same_image()
    {
        var a = SigilForge.Paint(SigilForge.From(Rich()));
        var b = SigilForge.Paint(SigilForge.From(Rich()));
        Assert.Equal(a.Sig, b.Sig, SigSemanticComparer.Instance);
        Assert.Equal(a.Cells.Count, b.Cells.Count);
        for (var i = 0; i < a.Cells.Count; i++) Assert.Equal(a.Cells[i], b.Cells[i]);
        Assert.Equal(a.Filaments.Count, b.Filaments.Count);
        Assert.Equal(a.BgHue, b.BgHue, 9);
    }

    // -------------------------------------------------------------- stability

    [Fact]
    public void A_small_life_change_keeps_the_same_seed()
    {
        var baseline = SigilForge.From(Rich());

        var tweaked = Model(
            new[] { ("fire", "creating"), ("connection", "Priya"), ("wound", "pining"), ("distortion", "radar") },
            new[] { (1, 2, "lifts"), (2, 3, "amplifies"), (4, 3, "amplifies") },
            new[] { ("hopeful", 0.55, "creating"), ("sad", -0.6, "pining"), ("anxious", -0.4, "radar"), ("happy", 0.7, "Priya") });
        Assert.Equal(baseline.Seed, SigilForge.From(tweaked).Seed);   // valence nudge → same image family
    }

    // -------------------------------------------------------------- divergence

    [Fact]
    public void Different_inner_worlds_paint_structurally_different_sigils()
    {
        // A dense, warm, fiery world vs a sparse, low, disconnected one.
        var warm = SigilForge.Paint(SigilForge.From(Rich()));
        var sparse = SigilForge.Paint(SigilForge.From(Model(
            new[] { ("wound", "isolation"), ("mind", "rumination") },
            Array.Empty<(int, int, string)>(),
            new[] { ("numb", -0.7, "isolation"), ("hopeless", -0.8, "rumination") })));

        Assert.NotEqual(warm.Sig.Seed, sparse.Sig.Seed);
        Assert.True(warm.Filaments.Count > sparse.Filaments.Count);      // connectedness shows
        Assert.True(warm.Sig.MeanValence > sparse.Sig.MeanValence);
        Assert.True(warm.BgLight > sparse.BgLight);                      // a darker atmosphere
        Assert.NotEqual(warm.BgHue, sparse.BgHue);                       // different dominant weather
    }

    [Fact]
    public void Empty_model_still_paints_a_quiet_nebula()
    {
        var empty = new ConstellationModel(Array.Empty<NodeMetric>(), Array.Empty<EdgeMetric>(),
            new JoinReport(0, 0, Array.Empty<string>()));
        var p = SigilForge.Paint(SigilForge.From(empty));
        Assert.NotEmpty(p.Cells);
        Assert.InRange(p.BgLight, 0.01, 0.2);
    }

    // Values-based equality for the record with the array property.
    private sealed class SigSemanticComparer : IEqualityComparer<SigilSignature>
    {
        public static readonly SigSemanticComparer Instance = new();
        public bool Equals(SigilSignature? x, SigilSignature? y) =>
            x is not null && y is not null && x.Seed == y.Seed &&
            x.NodeBand == y.NodeBand && x.EmotionMix.SequenceEqual(y.EmotionMix);
        public int GetHashCode(SigilSignature s) => s.Seed;
    }
}
