namespace SemanticPortrait.Core.Constellation;

/// <summary>
/// The MASKED public signature of a self-model — the only thing the Sigil renderer ever sees.
/// Masking is architectural: this type carries ONLY numbers (no labels, no per-node identities,
/// no strings — enforced by test via reflection), so the public artwork cannot leak content by
/// construction. Everything here is an AGGREGATE: proportions, means, densities.
/// </summary>
public sealed record SigilSignature(
    int NodeBand,               // banded size class 0..4 (never the exact node count)
    double Density,             // connectedness: edges per node, normalized 0..1
    double MeanValence,         // −1..1 across entry-linked nodes
    double MeanEnergy,          // 0..1
    double Volatility,          // 0..1 mean of node volatilities
    double ContradictionLoad,   // 0..1 how much of the graph holds opposing forces
    double FireGlow,            // 0..1 salience share of fire/joy — the creative heat
    double InferredShare,       // 0..1 how much of the model is inference vs stated
    IReadOnlyList<double> EmotionMix,   // proportions over the 8 real emotions (Anger..Love order)
    int Seed);                  // deterministic, from QUANTIZED values → stable across small changes

// Painting parameters — all numeric, all derived deterministically from the signature.
public readonly record struct SigilCell(double X, double Y, double R, double Hue, double Sat, double Light, double Opacity, double BreatheDur, double BreatheDelay);
public readonly record struct SigilFilament(double X1, double Y1, double CX, double CY, double X2, double Y2, double Hue, double Opacity, double Width);
public readonly record struct SigilRipple(double X, double Y, double R, double HueA, double HueB);
public readonly record struct SigilStar(double X, double Y, double R, double Opacity, double TwinkleDur);

public sealed record SigilPainting(
    SigilSignature Sig,
    IReadOnlyList<SigilCell> Cells,
    IReadOnlyList<SigilFilament> Filaments,
    IReadOnlyList<SigilRipple> Ripples,
    IReadOnlyList<SigilStar> Stars,
    double BgHue, double BgLight,
    double TurbulenceFrequency,   // organic distortion — rises with volatility
    double GlowWarmth);

/// <summary>
/// Signature extraction + deterministic painting. Pure, seeded, testable: the same person's model
/// paints the same image; small model changes shift it gently (quantized seed); different people
/// diverge structurally. The aesthetic: an aurora field — emotion hues as light cells, density as
/// filaments, contradiction as interference ripples, fire as warm glow, volatility as turbulence.
/// </summary>
public static class SigilForge
{
    private static readonly Emotion[] RealEmotions =
        { Emotion.Anger, Emotion.Disgust, Emotion.Creativity, Emotion.Calm,
          Emotion.Sadness, Emotion.Fear, Emotion.Love, Emotion.Joy };

    // ------------------------------------------------------------------ signature
    public static SigilSignature From(ConstellationModel m)
    {
        var nodes = m.Nodes;
        var linked = nodes.Where(n => !n.Quiet).ToList();

        int band = nodes.Count switch { 0 => 0, <= 4 => 1, <= 10 => 2, <= 25 => 3, _ => 4 };
        double density = nodes.Count == 0 ? 0 : Math.Min(1, m.Edges.Count / (double)nodes.Count / 2.5);
        double meanVal = linked.Count > 0 ? linked.Average(n => n.Valence) : 0;
        double meanEnergy = linked.Count > 0 ? linked.Average(n => Math.Clamp(n.Energy, 0, 1)) : 0;
        var vols = nodes.Where(n => n.Volatility is not null).Select(n => n.Volatility!.Value).ToList();
        double volatility = vols.Count > 0 ? Math.Min(1, vols.Average() * 2) : 0;
        double contradiction = nodes.Count == 0 ? 0 : Math.Min(1, nodes.Average(n => n.Contradiction) / 1.5);
        double totalSal = nodes.Sum(n => n.Salience);
        double fire = totalSal <= 0 ? 0
            : nodes.Where(n => n.Category is "fire" or "joy").Sum(n => n.Salience) / totalSal;
        double inferred = nodes.Count == 0 ? 0 : nodes.Count(n => n.Inferred) / (double)nodes.Count;

        var mix = new double[RealEmotions.Length];
        double mixTotal = 0;
        foreach (var n in linked)
        {
            var i = Array.IndexOf(RealEmotions, n.Emotion);
            if (i < 0) continue;
            var w = 0.25 + n.Salience;          // every read emotion counts; salient ones count more
            mix[i] += w; mixTotal += w;
        }
        if (mixTotal > 0) for (var i = 0; i < mix.Length; i++) mix[i] /= mixTotal;

        return new SigilSignature(band, density, meanVal, meanEnergy, volatility, contradiction,
            fire, inferred, mix, QuantizedSeed(band, density, meanVal, meanEnergy, volatility, contradiction, fire, inferred, mix));
    }

    /// <summary>FNV-1a over values rounded to coarse steps: nearby models → the SAME seed (the
    /// image evolves smoothly via the continuous params), while different people diverge.</summary>
    private static int QuantizedSeed(int band, double density, double val, double energy,
        double vol, double contra, double fire, double inferred, double[] mix)
    {
        unchecked
        {
            uint h = 2166136261;
            void Mix(int v) { h ^= (uint)v; h *= 16777619; }
            Mix(band);
            Mix((int)Math.Round(density * 5));
            Mix((int)Math.Round((val + 1) * 4));
            Mix((int)Math.Round(energy * 4));
            Mix((int)Math.Round(vol * 4));
            Mix((int)Math.Round(contra * 3));
            Mix((int)Math.Round(fire * 4));
            Mix((int)Math.Round(inferred * 3));
            foreach (var m in mix) Mix((int)Math.Round(m * 6));
            return (int)h;
        }
    }

    // ------------------------------------------------------------------ painting
    public static SigilPainting Paint(SigilSignature s)
    {
        var rng = new XorShift(s.Seed == 0 ? 777 : s.Seed);

        // Atmosphere: deep space stays deep — indigo base, only TINTED toward the dominant
        // emotion (a fully emotion-hued background reads as mud, not sky). Brightness tracks
        // valence (dark ↔ luminous nights).
        var domIdx = DominantIndex(s.EmotionMix);
        double domHue = domIdx >= 0 ? EmotionColor.Hue(RealEmotions[domIdx]) : 232;
        double bgHue = 232 + AngleDelta(232, domHue) * 0.18;
        double bgLight = 0.045 + 0.05 * (s.MeanValence + 1) / 2;

        // Emotion cells on a golden-angle spiral — one cluster per present emotion, sized by
        // share, placed LARGEST-FIRST so the dominant weather holds the center and small dark
        // shares (a trace of shame, a flicker of anger) sit honestly at the rim.
        var cells = new List<SigilCell>();
        int k = 0;
        var order = Enumerable.Range(0, s.EmotionMix.Count)
            .OrderByDescending(i => s.EmotionMix[i]).ThenBy(i => i).ToArray();
        foreach (var i in order)
        {
            var share = s.EmotionMix[i];
            if (share < 0.03) continue;
            int count = 1 + (int)Math.Round(share * 3);
            for (var c = 0; c < count; c++, k++)
            {
                double angle = k * 2.39996 + rng.Next() * 0.55;                  // golden angle + shimmer
                double dist = 0.04 + 0.28 * Math.Sqrt((k + 1) / 9.0) + rng.Next() * 0.08;
                double cx = 0.5 + Math.Cos(angle) * dist;
                double cy = 0.5 + Math.Sin(angle) * dist * (0.75 + 0.25 * s.MeanEnergy);   // high energy → taller field
                var hsl = EmotionColor.ToHsl(RealEmotions[i], s.MeanValence, 0.55 + 0.45 * share);
                // Deep + saturated: screen-blending brightens where cells meet, so each cell
                // starts darker and richer than it will read — luminosity is EARNED at overlaps.
                cells.Add(new SigilCell(
                    Clamp01(cx), Clamp01(cy),
                    0.07 + 0.15 * share + rng.Next() * 0.03,
                    hsl.H, Math.Max(hsl.S, 0.68), Math.Clamp(hsl.L, 0.34, 0.58),
                    0.34 + 0.28 * share,
                    9 + rng.Next() * 9, rng.Next() * -12));
            }
        }
        if (cells.Count == 0)   // a brand-new, unread model still deserves a quiet nebula
            cells.Add(new SigilCell(0.5, 0.5, 0.22, 232, 0.45, 0.30, 0.28, 14, 0));

        // Filaments: connectedness as luminous threads between cells.
        var filaments = new List<SigilFilament>();
        int fCount = (int)Math.Round(4 + s.Density * 18);
        for (var f = 0; f < fCount && cells.Count > 1; f++)
        {
            var a = cells[(int)(rng.Next() * cells.Count) % cells.Count];
            var b = cells[(int)(rng.Next() * cells.Count) % cells.Count];
            if (Math.Abs(a.X - b.X) + Math.Abs(a.Y - b.Y) < 0.05) continue;
            double mx = (a.X + b.X) / 2 + (rng.Next() - 0.5) * 0.35;
            double my = (a.Y + b.Y) / 2 + (rng.Next() - 0.5) * 0.35;
            filaments.Add(new SigilFilament(a.X, a.Y, Clamp01(mx), Clamp01(my), b.X, b.Y,
                (a.Hue + b.Hue) / 2, 0.22 + rng.Next() * 0.26, 0.5 + rng.Next() * 1.1));
        }

        // Starfield: the cosmic texture. Count scales with the model's size band — a fuller life,
        // a fuller sky. Positions avoid the exact center so the nebula keeps its stage.
        var stars = new List<SigilStar>();
        int starCount = 30 + s.NodeBand * 18;
        for (var st = 0; st < starCount; st++)
        {
            double sx = rng.Next(); double sy = rng.Next();
            if (Math.Abs(sx - 0.5) < 0.07 && Math.Abs(sy - 0.5) < 0.07) continue;
            stars.Add(new SigilStar(sx, sy, 0.6 + rng.Next() * 1.6,
                0.25 + rng.Next() * 0.55, 2.5 + rng.Next() * 6));
        }

        // Contradiction: interference ripples — two hues sharing one center.
        var ripples = new List<SigilRipple>();
        int rCount = (int)Math.Round(s.ContradictionLoad * 4);
        for (var r = 0; r < rCount; r++)
        {
            var host = cells[(int)(rng.Next() * cells.Count) % cells.Count];
            ripples.Add(new SigilRipple(host.X, host.Y, 0.10 + rng.Next() * 0.12,
                host.Hue, (host.Hue + 180) % 360));
        }

        return new SigilPainting(s, cells, filaments, ripples, stars, bgHue, bgLight,
            TurbulenceFrequency: 0.004 + 0.018 * s.Volatility,
            GlowWarmth: s.FireGlow);
    }

    private static int DominantIndex(IReadOnlyList<double> mix)
    {
        int best = -1; double bestV = 0.03;
        for (var i = 0; i < mix.Count; i++) if (mix[i] > bestV) { best = i; bestV = mix[i]; }
        return best;
    }

    private static double Clamp01(double v) => Math.Clamp(v, 0.04, 0.96);

    /// <summary>Signed shortest angular distance a→b on the hue wheel, in degrees.</summary>
    private static double AngleDelta(double a, double b)
    {
        var d = (b - a) % 360;
        if (d > 180) d -= 360;
        if (d < -180) d += 360;
        return d;
    }

    /// <summary>Tiny deterministic PRNG (xorshift32) → doubles in [0,1). Seeded by the signature,
    /// so the painting is a pure function of the person's aggregates.</summary>
    private struct XorShift
    {
        private uint _s;
        public XorShift(int seed) => _s = (uint)(seed == 0 ? 1 : seed);
        public double Next()
        {
            _s ^= _s << 13; _s ^= _s >> 17; _s ^= _s << 5;
            return _s / (double)uint.MaxValue;
        }
    }
}
