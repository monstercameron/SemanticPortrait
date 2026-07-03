namespace SemanticPortrait.Core.Constellation;

/// <summary>
/// A fixed fixture that mirrors the design mockup (Core Self + the six domain clusters). Lets the
/// renderer be prototyped pixel-for-pixel against the design with zero DB. Same <see cref="VisualModel"/>
/// contract as the live source — flip between them freely.
/// </summary>
public sealed class SampleConstellationSource : IConstellationSource
{
    // domain → (hue, sat, light) fill + stroke hex (matches the mockup's cluster colors)
    private static readonly (double H, double S, double L) Core = (212, .75, .62), Drive = (330, .75, .62),
        Challenge = (28, .85, .58), Solitude = (262, .55, .66), Growth = (155, .70, .55),
        Connection = (190, .70, .60), Stability = (212, .75, .60), Inferred = (0, 0, .55);

    public Task<ConstellationBundle> BuildAsync() =>
        Task.FromResult(new ConstellationBundle(Build(), SigilForge.Paint(SampleSignature)));

    /// <summary>A hand-tuned signature so the Sigil can be designed with zero data — a warm,
    /// fairly connected, mildly contradictory inner world.</summary>
    private static readonly SigilSignature SampleSignature = new(
        NodeBand: 3, Density: 0.55, MeanValence: 0.15, MeanEnergy: 0.55, Volatility: 0.35,
        ContradictionLoad: 0.4, FireGlow: 0.5, InferredShare: 0.2,
        EmotionMix: new[] { 0.05, 0.03, 0.22, 0.15, 0.13, 0.10, 0.17, 0.15 },
        Seed: 1_337_042);

    public VisualModel Build()
    {
        var n = new List<VisualNode>();
        void Add(long id, string label, double x, double y, (double H, double S, double L) c, string stroke,
            bool circle = true, int sides = 6, bool dashed = false, double radius = 0.38, double glow = 0, double pulse = 0,
            bool ambivalent = false, int rings = 0)
            => n.Add(new VisualNode(id, label, "sample", x, y, x * 2 - 1, 1 - y, circle ? Geometry.CircleSides : sides, circle,
                c.H, c.S, c.L, stroke, radius, dashed ? 0.5 : 1.0, dashed, pulse, 0.8, 0, glow, false, false,
                ambivalent, rings, label));

        const string mag = "#ff2e6e", org = "#ff7a00", pur = "#7c6bff", grn = "#00e88f",
                     tea = "#19d3ff", blu = "#2e9bff", gry = "#9a9aa3";

        Add(1, "Core Self", 0.50, 0.50, Core, blu, circle: false, sides: 6, radius: 1.0, glow: .6, pulse: .10);
        // Drive & Achievement
        Add(2, "Ambition", 0.34, 0.22, Drive, mag, circle: false, sides: 6, radius: .8, glow: .5, pulse: .07);
        Add(3, "Leadership", 0.40, 0.15, Drive, mag);
        Add(4, "Need for Validation", 0.24, 0.27, Drive, mag);
        Add(5, "Impatience", 0.31, 0.38, Drive, mag);
        // Challenges
        Add(6, "Perfectionism", 0.25, 0.45, Challenge, org, circle: false, sides: 6, radius: .7);
        Add(7, "Self-Doubt", 0.31, 0.55, Challenge, org, circle: false, sides: 6, radius: .65);
        Add(8, "Burnout History", 0.19, 0.62, Challenge, org);
        Add(9, "Overthinking", 0.29, 0.69, Challenge, org, circle: false, sides: 4, radius: .34);
        // Solitude & Renewal
        Add(10, "Introversion", 0.37, 0.72, Solitude, pur, circle: false, sides: 6, radius: .7);
        Add(11, "Reflection", 0.32, 0.82, Solitude, pur);
        // inferred (dashed)
        Add(12, "Risk Tolerance", 0.47, 0.27, Inferred, gry, dashed: true);
        // Growth & Exploration
        Add(13, "Creativity", 0.62, 0.24, Growth, grn, circle: false, sides: 8, radius: .8, glow: .5, pulse: .06);
        Add(14, "Curiosity", 0.66, 0.13, Growth, grn);
        Add(15, "Playfulness", 0.73, 0.22, Growth, grn);
        Add(16, "Love of Learning", 0.69, 0.33, Growth, grn, circle: false, sides: 4, radius: .34);
        // Connection & Empathy
        Add(17, "Empathy", 0.69, 0.45, Connection, tea, circle: false, sides: 6, radius: .75, glow: .45);
        Add(18, "People Pleaser", 0.81, 0.50, Connection, tea);
        Add(24, "Resilience", 0.45, 0.61, Connection, tea, glow: .2);
        // Stability & Grounding
        Add(19, "Calm", 0.64, 0.63, Stability, blu, circle: false, sides: 6, radius: .7, glow: .4);
        Add(20, "Routine & Discipline", 0.55, 0.71, Stability, blu);
        Add(21, "Gratitude", 0.80, 0.64, Stability, blu);
        Add(22, "Perspective", 0.74, 0.73, Stability, blu, circle: false, sides: 4, radius: .34);
        Add(23, "Need for Control", 0.49, 0.75, Inferred, gry, dashed: true);

        // edges: Core→majors, majors→satellites. valence drives color (green/red/grey).
        var e = new List<VisualEdge>();
        // Amplitude is a FRACTION of edge length (matches Encoding's 0.04–0.14 live range).
        void E(long s, long d, int v) => e.Add(new VisualEdge(e.Count + 1, s, d,
            v > 0 ? "#28e070" : v < 0 ? "#ff2e6e" : "#5a5a64", 1.4, 0.08, v,
            Width: 1.6, Dashed: false, PulseAmp: 0.12, PulseFreq: 0.4));

        E(1, 2, +1); E(1, 6, -1); E(1, 7, -1); E(1, 10, 0); E(1, 13, +1); E(1, 17, +1); E(1, 19, +1); E(1, 24, +1);
        E(1, 12, 0); E(1, 23, 0);
        E(2, 3, +1); E(2, 4, 0); E(2, 5, -1);
        E(6, 7, -1); E(7, 8, -1); E(7, 9, -1);
        E(10, 11, +1);
        E(13, 14, +1); E(13, 15, +1); E(13, 16, +1);
        E(17, 18, 0); E(19, 20, +1); E(19, 21, +1); E(19, 22, +1);

        return new VisualModel(n, e, Array.Empty<VisualHull>(), MeanValence: 0.08, LinkedFraction: 1.0,
            new JoinReport(n.Count, n.Count, Array.Empty<string>()));
    }
}
