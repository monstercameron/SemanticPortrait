using SemanticPortrait.Core.Constellation;

namespace SemanticPortrait.Tests;

public class ConstellationPrimitivesTests
{
    // ---- Geometry: polygons ----
    [Theory]
    [InlineData(3)] [InlineData(4)] [InlineData(6)] [InlineData(8)]
    public void Polygon_has_requested_vertex_count_on_the_radius(int sides)
    {
        var pts = Geometry.RegularPolygon(sides, radius: 10, rotation: 0, center: new Pt(5, 5));
        Assert.Equal(sides, pts.Length);
        foreach (var p in pts)
        {
            var r = Math.Sqrt(Math.Pow(p.X - 5, 2) + Math.Pow(p.Y - 5, 2));
            Assert.Equal(10, r, 6);                 // every vertex sits on the radius
        }
    }

    [Fact]
    public void Polygon_clamps_sides_to_min_three()
    {
        Assert.Equal(3, Geometry.RegularPolygon(1, 10).Length);
        Assert.Equal(3, Geometry.RegularPolygon(-5, 10).Length);
    }

    [Fact]
    public void Rotation_offsets_first_vertex()
    {
        var a = Geometry.RegularPolygon(4, 10, rotation: 0)[0];
        var b = Geometry.RegularPolygon(4, 10, rotation: Math.PI / 2)[0];
        Assert.False(Math.Abs(a.X - b.X) < 1e-6 && Math.Abs(a.Y - b.Y) < 1e-6);
    }

    [Fact]
    public void IsCircle_at_threshold()
    {
        Assert.False(Geometry.IsCircle(8));
        Assert.True(Geometry.IsCircle(Geometry.CircleSides));
        Assert.True(Geometry.IsCircle(40));
    }

    // ---- Geometry: wave edges ----
    [Theory]
    [InlineData(1.0)] [InlineData(2.5)] [InlineData(5.0)]   // endpoints anchored regardless of frequency
    public void WaveEdge_endpoints_are_anchored_to_a_and_b(double freq)
    {
        var a = new Pt(0, 0); var b = new Pt(100, 0);
        var pts = Geometry.WaveEdge(a, b, frequency: freq, amplitude: 12, samples: 30);
        Assert.Equal(a.X, pts[0].X, 6); Assert.Equal(a.Y, pts[0].Y, 6);
        Assert.Equal(b.X, pts[^1].X, 6); Assert.Equal(b.Y, pts[^1].Y, 6);
    }

    [Fact]
    public void WaveEdge_displacement_within_amplitude_and_perpendicular()
    {
        var a = new Pt(0, 0); var b = new Pt(100, 0);   // horizontal → displacement is vertical
        var pts = Geometry.WaveEdge(a, b, frequency: 3, amplitude: 10, samples: 50);
        foreach (var p in pts) Assert.True(Math.Abs(p.Y) <= 10 + 1e-6);
        Assert.Contains(pts, p => Math.Abs(p.Y) > 1);   // it actually waves
    }

    [Fact]
    public void WaveEdge_degenerate_zero_length_does_not_throw_or_nan()
    {
        var pts = Geometry.WaveEdge(new Pt(5, 5), new Pt(5, 5), 3, 10, 10);
        Assert.All(pts, p => Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y)));
    }

    // ---- Emotion lexicon ----
    [Theory]
    [InlineData("anxious but hopeful", Emotion.Fear)]      // fear keyword present; tie-break by score
    [InlineData("completely empty", Emotion.Sadness)]
    [InlineData("weird productive energy", Emotion.Creativity)]
    [InlineData("furious at myself", Emotion.Anger)]
    [InlineData("calm and steady", Emotion.Calm)]
    [InlineData("so grateful today", Emotion.Joy)]
    [InlineData("ashamed of it", Emotion.Disgust)]
    [InlineData("lonely and numb", Emotion.Sadness)]
    [InlineData("in love", Emotion.Love)]
    [InlineData("fine", Emotion.Neutral)]
    [InlineData("qwerty zxcv", Emotion.Unmapped)]
    [InlineData("", Emotion.Unmapped)]
    public void Lexicon_classifies_mood(string mood, Emotion expected)
        => Assert.Equal(expected, EmotionColor.Classify(mood));

    [Fact]
    public void Classify_respects_word_boundaries()
    {
        Assert.Equal(Emotion.Unmapped, EmotionColor.Classify("broke"));   // "ok" not matched inside "broke"
        Assert.Equal(Emotion.Neutral, EmotionColor.Classify("it's ok"));  // token "ok" → Neutral
    }

    [Fact]
    public void Classify_handles_negation()
    {
        Assert.Equal(Emotion.Unmapped, EmotionColor.Classify("not anxious"));     // negated → not Fear
        Assert.Equal(Emotion.Calm, EmotionColor.Classify("calm, not anxious"));   // calm still counts
    }

    [Fact]
    public void Classify_tiebreak_is_lexicon_order()
    {
        // "low" (Sadness) and "flow" (Creativity) each score 1; Sadness precedes Creativity → Sadness.
        Assert.Equal(Emotion.Sadness, EmotionColor.Classify("low flow"));
    }

    [Fact]
    public void Unmapped_color_is_distinct_from_neutral()
    {
        Assert.NotEqual(EmotionColor.ToHsl(Emotion.Neutral, 0, 0),
                        EmotionColor.ToHsl(Emotion.Unmapped, 0, 0));
    }

    [Fact]
    public void Hue_is_nan_for_neutral_and_unmapped()
    {
        Assert.True(double.IsNaN(EmotionColor.Hue(Emotion.Neutral)));
        Assert.True(double.IsNaN(EmotionColor.Hue(Emotion.Unmapped)));
    }

    // ---- guards / edge cases from critique ----
    [Fact]
    public void Negative_radius_does_not_invert_polygon()
    {
        var pos = Geometry.RegularPolygon(5, 10);
        var neg = Geometry.RegularPolygon(5, -10);
        for (int i = 0; i < pos.Length; i++) Assert.Equal(pos[i], neg[i]);   // |radius| used
    }

    [Fact]
    public void WaveEdge_zero_amplitude_is_a_straight_line()
    {
        var pts = Geometry.WaveEdge(new Pt(0, 0), new Pt(100, 0), frequency: 5, amplitude: 0, samples: 20);
        Assert.All(pts, p => Assert.Equal(0, p.Y, 9));
    }

    [Fact]
    public void WaveEdge_positive_amplitude_displaces_left_of_direction()
    {
        // a→b points +y (upward). Left of +y is −x. Midpoint should have negative X.
        var pts = Geometry.WaveEdge(new Pt(0, 0), new Pt(0, 100), frequency: 0.5, amplitude: 10, samples: 21);
        Assert.True(pts[10].X < 0);
    }

    // ---- property / fuzz sweep (design §12) ----
    [Fact]
    public void Primitives_never_produce_nan_or_out_of_range()
    {
        var rnd = new Random(12345);   // fixed seed → deterministic
        for (int k = 0; k < 2000; k++)
        {
            int sides = rnd.Next(-2, 30);
            double radius = (rnd.NextDouble() - 0.5) * 200;
            foreach (var p in Geometry.RegularPolygon(sides, radius, rnd.NextDouble() * 10))
                Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y));

            var a = new Pt(rnd.NextDouble() * 100, rnd.NextDouble() * 100);
            var b = new Pt(rnd.NextDouble() * 100, rnd.NextDouble() * 100);
            foreach (var p in Geometry.WaveEdge(a, b, rnd.NextDouble() * 20, rnd.NextDouble() * 30, 16))
                Assert.False(double.IsNaN(p.X) || double.IsNaN(p.Y));

            foreach (Emotion e in Enum.GetValues<Emotion>())
            {
                var c = EmotionColor.ToHsl(e, rnd.NextDouble() * 4 - 2, rnd.NextDouble() * 4 - 2);
                Assert.InRange(c.H, 0, 360);
                Assert.InRange(c.S, 0, 1);
                Assert.InRange(c.L, 0, 1);
            }

            double t = rnd.NextDouble() * 100;
            Assert.True(Math.Abs(Motion.Pulse(t, 0.3, rnd.NextDouble() * 3)) <= 0.3 + 1e-9);
            var tr = Motion.Tremor(t, rnd.Next(0, 100000), 2);
            Assert.True(Math.Abs(tr.X) <= 2 + 1e-9 && Math.Abs(tr.Y) <= 2 + 1e-9);
        }
    }

    // ---- Emotion → HSL ----
    [Fact]
    public void Hsl_hue_matches_wheel_and_is_in_range()
    {
        foreach (Emotion e in Enum.GetValues<Emotion>())
        {
            var c = EmotionColor.ToHsl(e, 0.5, 0.5);
            Assert.InRange(c.H, 0, 360);
            Assert.InRange(c.S, 0, 1);
            Assert.InRange(c.L, 0, 1);
        }
        Assert.Equal(0, EmotionColor.ToHsl(Emotion.Anger, 0, 0.5).H);
        Assert.Equal(140, EmotionColor.ToHsl(Emotion.Creativity, 0, 0.5).H);
    }

    [Fact]
    public void Lightness_is_monotonic_in_valence()
    {
        var neg = EmotionColor.ToHsl(Emotion.Joy, -0.8, 0.5).L;
        var mid = EmotionColor.ToHsl(Emotion.Joy, 0.0, 0.5).L;
        var pos = EmotionColor.ToHsl(Emotion.Joy, 0.8, 0.5).L;
        Assert.True(neg < mid && mid < pos);
    }

    [Fact]
    public void Saturation_rises_with_intensity()
    {
        Assert.True(EmotionColor.ToHsl(Emotion.Anger, 0, 0.1).S
                  < EmotionColor.ToHsl(Emotion.Anger, 0, 0.9).S);
    }

    [Fact]
    public void Neutral_and_unmapped_are_desaturated()
    {
        Assert.True(EmotionColor.ToHsl(Emotion.Neutral, 0.5, 0.9).S < 0.1);
        Assert.True(EmotionColor.ToHsl(Emotion.Unmapped, 0.5, 0.9).S < 0.1);
    }

    // ---- Motion ----
    [Fact]
    public void Pulse_is_bounded_and_deterministic()
    {
        for (double t = 0; t < 3; t += 0.13)
            Assert.True(Math.Abs(Motion.Pulse(t, 0.2, 1.5)) <= 0.2 + 1e-9);
        Assert.Equal(Motion.Pulse(1.0, 0.2, 1.5), Motion.Pulse(1.0, 0.2, 1.5));   // deterministic
    }

    [Fact]
    public void Tremor_is_bounded_deterministic_and_phase_varies_by_seed()
    {
        var a = Motion.Tremor(1.0, seed: 1, amp: 2);
        var b = Motion.Tremor(1.0, seed: 2, amp: 2);
        Assert.True(Math.Abs(a.X) <= 2 + 1e-9 && Math.Abs(a.Y) <= 2 + 1e-9);
        Assert.Equal(a, Motion.Tremor(1.0, 1, 2));            // deterministic
        Assert.NotEqual(a, b);                                 // different nodes shimmer out of phase
    }
}
