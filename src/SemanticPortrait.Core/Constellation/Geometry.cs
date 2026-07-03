namespace SemanticPortrait.Core.Constellation;

/// <summary>A 2D point in constellation/layout space (unitless; the renderer scales to the viewport).</summary>
public readonly record struct Pt(double X, double Y);

/// <summary>
/// Pure geometry for the constellation primitives — no rendering, no state. The renderer consumes
/// these vertex/point arrays. Everything here is deterministic and unit-tested.
/// </summary>
public static class Geometry
{
    /// <summary>A node with ≥ this many sides is drawn as a circle ("resolved/whole").</summary>
    public const int CircleSides = 20;

    /// <summary>
    /// Vertices of a regular polygon centered at <paramref name="center"/>. <paramref name="sides"/>
    /// is clamped to ≥3. <paramref name="rotation"/> is in radians (0 = first vertex pointing +x).
    /// </summary>
    public static Pt[] RegularPolygon(int sides, double radius, double rotation = 0, Pt center = default)
    {
        if (sides < 3) sides = 3;
        radius = Math.Abs(radius);              // negative radius would invert winding order

        var pts = new Pt[sides];
        for (int i = 0; i < sides; i++)
        {
            var a = rotation + i * (2 * Math.PI / sides);
            pts[i] = new Pt(center.X + radius * Math.Cos(a), center.Y + radius * Math.Sin(a));
        }
        return pts;
    }

    public static bool IsCircle(int sides) => sides >= CircleSides;

    /// <summary>
    /// Convex hull (monotone chain), counter-clockwise, no repeated last point. Used for asterism
    /// outlines around graph communities. Degenerate inputs (&lt;3 distinct points) return what
    /// exists — callers decide whether that's drawable.
    /// </summary>
    public static Pt[] ConvexHull(IReadOnlyList<Pt> points)
    {
        var pts = points.Distinct().OrderBy(p => p.X).ThenBy(p => p.Y).ToArray();
        if (pts.Length < 3) return pts;

        static double Cross(Pt o, Pt a, Pt b) => (a.X - o.X) * (b.Y - o.Y) - (a.Y - o.Y) * (b.X - o.X);
        var hull = new List<Pt>();
        foreach (var p in pts)                       // lower
        {
            while (hull.Count >= 2 && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        var lower = hull.Count + 1;
        for (var i = pts.Length - 2; i >= 0; i--)    // upper
        {
            var p = pts[i];
            while (hull.Count >= lower && Cross(hull[^2], hull[^1], p) <= 0) hull.RemoveAt(hull.Count - 1);
            hull.Add(p);
        }
        hull.RemoveAt(hull.Count - 1);
        return hull.ToArray();
    }

    /// <summary>Expand a hull outward from its centroid (padding for asterism glow).</summary>
    public static Pt[] Expand(Pt[] hull, double pad)
    {
        if (hull.Length == 0) return hull;
        double cx = hull.Average(p => p.X), cy = hull.Average(p => p.Y);
        return hull.Select(p =>
        {
            var dx = p.X - cx; var dy = p.Y - cy;
            var d = Math.Sqrt(dx * dx + dy * dy);
            var s = d < 1e-9 ? 0 : (d + pad) / d;
            return new Pt(cx + dx * s, cy + dy * s);
        }).ToArray();
    }

    /// <summary>
    /// Clamp one camera axis so the viewport stays within world ± margin. When the viewport is
    /// WIDER than world+margins the naive Math.Clamp range inverts and THROWS (live crash,
    /// 2026-07-02: one zoom-out wheel tick killed the whole view) — that case centers the world.
    /// </summary>
    public static double ClampCameraAxis(double v, double viewportSpan, double worldSpan, double margin = 200)
    {
        var hi = worldSpan + margin - viewportSpan;
        return hi <= -margin ? (worldSpan - viewportSpan) / 2 : Math.Clamp(v, -margin, hi);
    }

    /// <summary>
    /// A wavy edge from <paramref name="a"/> to <paramref name="b"/>: the straight segment with a
    /// perpendicular sinusoidal displacement. A sin(πt) window pins BOTH endpoints to a and b (within
    /// floating-point epsilon) regardless of frequency, so edges always connect their nodes.
    /// Positive amplitude displaces to the LEFT of the a→b direction (consistent for directed edges).
    /// frequency = wave cycles along the edge (relationship complexity) — keep ≲ samples/2 to avoid
    /// aliasing; amplitude = displacement magnitude (uncertainty).
    /// </summary>
    public static Pt[] WaveEdge(Pt a, Pt b, double frequency, double amplitude, int samples = 24)
    {
        if (samples < 2) samples = 2;
        double dx = b.X - a.X, dy = b.Y - a.Y;
        double len = Math.Sqrt(dx * dx + dy * dy);
        // Unit normal (perpendicular to the segment). Degenerate (a==b) → no displacement.
        double nx = len > 1e-9 ? -dy / len : 0;
        double ny = len > 1e-9 ? dx / len : 0;

        var pts = new Pt[samples];
        for (int i = 0; i < samples; i++)
        {
            double t = (double)i / (samples - 1);
            double bx = a.X + dx * t, by = a.Y + dy * t;
            double window = Math.Sin(Math.PI * t);                 // 0 at both ends
            double disp = amplitude * Math.Sin(2 * Math.PI * frequency * t) * window;
            pts[i] = new Pt(bx + nx * disp, by + ny * disp);
        }
        return pts;
    }
}
