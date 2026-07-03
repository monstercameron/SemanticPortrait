using SemanticPortrait.Core;
using SemanticPortrait.Core.Constellation;

namespace SemanticPortrait.Tests;

/// <summary>
/// Pins the rank-spread layout (2026-07-02 rework): real graphs cluster at mild-negative valence /
/// mid energy, and the raw circumplex piled everything into one blob. The spread must fill the
/// canvas WITHOUT breaking the semantics — exact quadrants, exact rank order — and the belt must
/// wrap instead of smearing. Plus the camera clamp that crashed live, and NaN insurance.
/// </summary>
public class ConstellationLayoutTests
{
    private static readonly DateTime Now = new(2026, 6, 21, 0, 0, 0, DateTimeKind.Utc);
    private static string Recent => new DateTime(2026, 6, 20, 0, 0, 0, DateTimeKind.Utc).ToString("o");

    private static GraphNode Node(long id, string label) => new(id, "self", label, false, 1.0);
    private static EntryMeta Entry(long id, double val, string topics, double energy = 0.5, string mood = "anxious")
        => new(id, Recent, mood, val, 0.5, energy, topics, "[]", "s");

    private static VisualModel Build(IReadOnlyList<GraphNode> n, IReadOnlyList<EntryMeta> m)
        => Encoding.Build(ConstellationMetrics.Compute(n, Array.Empty<GraphEdge>(), m, Now));

    [Fact]
    public void Dense_same_sign_cluster_spreads_across_its_half_without_leaving_it()
    {
        // Ten nodes with nearly identical mildly-negative valence — the exact live failure shape.
        var nodes = new List<GraphNode>(); var entries = new List<EntryMeta>();
        for (var i = 0; i < 10; i++)
        {
            nodes.Add(Node(i + 1, $"n{i}"));
            entries.Add(Entry(100 + i, -0.40 + i * 0.01, $"[\"n{i}\"]"));
        }
        var vm = Build(nodes, entries);
        var real = vm.Nodes.Where(n => n.Id > 0).ToList();   // exclude the synthesized core star

        // A radial fan spends its spread across the ARC, not one axis — the anti-pile guarantee
        // is pairwise separation (as rendered, dx aspect-weighted), not gross x-span.
        var pts = real.Select(n => (n.X, n.Y)).ToArray();
        for (var i = 0; i < pts.Length; i++)
            for (var j = i + 1; j < pts.Length; j++)
            {
                var dx = (pts[i].X - pts[j].X) * Encoding.WorldAspect; var dy = pts[i].Y - pts[j].Y;
                Assert.True(Math.Sqrt(dx * dx + dy * dy) > 0.025,
                    $"nodes {i} and {j} are stacked ({Math.Sqrt(dx * dx + dy * dy):0.###} apart)");
            }
        Assert.All(real, n => Assert.True(n.X < 0.5, "negative valence must NEVER cross the midline"));
    }

    [Fact]
    public void Half_planes_stay_exact_at_scale_and_nothing_stacks()
    {
        var nodes = new List<GraphNode>(); var entries = new List<EntryMeta>();
        double[] vals = { -0.9, -0.6, -0.55, -0.3, -0.1, -0.05, 0.05, 0.1, 0.35, 0.6, 0.62, 0.9 };
        double[] energies = { 0.9, 0.2, 0.7, 0.3, 0.8, 0.1, 0.9, 0.2, 0.6, 0.4, 0.75, 0.15 };
        for (var i = 0; i < vals.Length; i++)
        {
            nodes.Add(Node(i + 1, $"n{i}"));
            entries.Add(Entry(100 + i, vals[i], $"[\"n{i}\"]", energy: energies[i]));
        }
        var vm = Build(nodes, entries);
        var real = vm.Nodes.Where(n => n.Id > 0).ToList();   // exclude the synthesized core star

        // The radial invariant: half-plane membership is exact on BOTH axes, always.
        foreach (var n in real)
        {
            Assert.True(n.Valence >= 0 == n.X > 0.5, $"{n.Label}: wrong valence side");
            Assert.True(n.Energy >= 0.5 == n.Y < 0.5, $"{n.Label}: wrong energy side");
        }
        Assert.Equal(vals.Length, real.Select(n => (n.X, n.Y)).Distinct().Count());
    }

    [Fact]
    public void Energy_halves_stay_exact_through_the_vertical_spread()
    {
        var nodes = new[] { Node(1, "hi1"), Node(2, "hi2"), Node(3, "lo1"), Node(4, "lo2") };
        var entries = new[]
        {
            Entry(10, 0.2, "[\"hi1\"]", energy: 0.8), Entry(11, 0.2, "[\"hi2\"]", energy: 0.9),
            Entry(12, 0.2, "[\"lo1\"]", energy: 0.1), Entry(13, 0.2, "[\"lo2\"]", energy: 0.2),
        };
        var vm = Build(nodes, entries);

        // y is inverted: high energy renders in the TOP half (y < 0.5).
        Assert.All(vm.Nodes.Where(n => n.Label.StartsWith("hi")), n => Assert.True(n.Y < 0.5));
        Assert.All(vm.Nodes.Where(n => n.Label.StartsWith("lo")), n => Assert.True(n.Y > 0.5));
    }

    [Fact]
    public void Identical_affect_nodes_are_pushed_apart_not_stacked()
    {
        var nodes = new[] { Node(1, "a"), Node(2, "b"), Node(3, "c") };
        var entries = new[]
        {
            Entry(10, -0.3, "[\"a\"]"), Entry(11, -0.3, "[\"b\"]"), Entry(12, -0.3, "[\"c\"]"),
        };
        var vm = Build(nodes, entries);
        var pos = vm.Nodes.Where(n => n.Id > 0).Select(n => (n.X, n.Y)).Distinct().Count();
        Assert.Equal(3, pos);   // relaxation separated them
    }

    [Fact]
    public void Belt_wraps_into_rows_stays_in_bounds_and_caps_radius()
    {
        // 25 quiet nodes (no entries) — the old single evenly-divided row smeared past ~15.
        var nodes = Enumerable.Range(1, 25).Select(i => Node(i, $"q{i}")).ToList();
        var vm = Build(nodes, Array.Empty<EntryMeta>());

        var belt = vm.Nodes.Where(n => n.Quiet).ToList();
        Assert.Equal(25, belt.Count);
        Assert.True(belt.Select(n => n.Y).Distinct().Count() >= 2, "25 quiet nodes must wrap to multiple rows");
        Assert.All(belt, n => { Assert.InRange(n.X, 0, 1); Assert.InRange(n.Y, 0, 1); });
        Assert.All(belt, n => Assert.True(n.Radius <= 0.55, "belt slots are tight — radius must be capped"));
        Assert.Equal(25, belt.Select(n => (n.X, n.Y)).Distinct().Count());   // no two share a slot
    }

    [Fact]
    public void NaN_metrics_cannot_blank_the_svg()
    {
        var nodes = new[] { Node(1, "bad"), Node(2, "good") };
        var entries = new[] { Entry(10, double.NaN, "[\"bad\"]", energy: double.NaN), Entry(11, 0.4, "[\"good\"]") };
        var vm = Build(nodes, entries);

        foreach (var n in vm.Nodes)
        {
            Assert.False(double.IsNaN(n.X) || double.IsNaN(n.Y), "position must never be NaN");
            Assert.False(double.IsNaN(n.Valence) || double.IsNaN(n.Energy), "raw metrics must never be NaN");
            // A NaN entry also poisons SALIENCE (Math.Max(NaN,0) is NaN), which feeds every
            // node's pulse/glow through the shared normalizer — those channels must stay clean.
            Assert.False(double.IsNaN(n.PulseAmp) || double.IsNaN(n.GlowWarmth) || double.IsNaN(n.Opacity),
                "animation channels must never be NaN");
        }
    }

    [Fact]
    public void Inspector_reads_raw_metrics_not_the_spread_position()
    {
        var nodes = new[] { Node(1, "a"), Node(2, "b"), Node(3, "c") };
        var entries = new[]
        {
            Entry(10, -0.35, "[\"a\"]", energy: 0.7), Entry(11, -0.30, "[\"b\"]"), Entry(12, -0.25, "[\"c\"]"),
        };
        var vm = Build(nodes, entries);
        var a = vm.Nodes.Single(n => n.Label == "a");
        Assert.Equal(-0.35, a.Valence, 6);   // the true value, not X*2-1
        Assert.Equal(0.7, a.Energy, 6);
    }

    // ---- the camera clamp that crashed live (ArgumentException: '-200' > max) ----

    [Fact]
    public void Camera_clamp_never_throws_and_centers_when_zoomed_out_past_the_world()
    {
        const double world = 1500;
        for (double span = 100; span <= 5000; span += 50)
        {
            var v = Geometry.ClampCameraAxis(-9999, span, world);   // must never throw
            if (span >= world + 400)
                Assert.Equal((world - span) / 2, v, 6);             // wider than world+margins → centered
            else
                Assert.Equal(-200, v, 6);                           // clamped to the low margin
        }
        Assert.Equal(300, Geometry.ClampCameraAxis(300, 500, 1500), 6);        // in-range untouched
        Assert.Equal(1200, Geometry.ClampCameraAxis(9999, 500, 1500), 6);      // high clamp = world+200-span
    }

    // ---- sigil: extreme signatures must paint in bounds, never throw ----

    [Fact]
    public void Sigil_paints_extreme_signatures_in_bounds()
    {
        var extremes = new[]
        {
            new SigilSignature(4, 1, 1, 1, 1, 1, 1, 1, new[] { 1.0, 0, 0, 0, 0, 0, 0, 0 }, int.MaxValue),
            new SigilSignature(0, 0, -1, 0, 0, 0, 0, 0, new double[8], 0),
            new SigilSignature(2, 0.5, 0, 0.5, 0.5, 0.5, 0.5, 0.5,
                new[] { .125, .125, .125, .125, .125, .125, .125, .125 }, int.MinValue),
        };
        foreach (var sig in extremes)
        {
            var p = SigilForge.Paint(sig);
            Assert.NotEmpty(p.Cells);   // even an all-zero mix paints the quiet nebula
            Assert.All(p.Cells, c => { Assert.InRange(c.X, 0, 1); Assert.InRange(c.Y, 0, 1); Assert.True(c.R > 0); });
            Assert.All(p.Ripples, r => { Assert.InRange(r.X, 0, 1); Assert.InRange(r.Y, 0, 1); });
            Assert.All(p.Filaments, f =>
            {
                Assert.InRange(f.CX, 0, 1); Assert.InRange(f.CY, 0, 1);
                Assert.True(f.Width > 0); Assert.InRange(f.Opacity, 0, 1);
            });
            Assert.True(p.TurbulenceFrequency > 0);
        }
    }
}
