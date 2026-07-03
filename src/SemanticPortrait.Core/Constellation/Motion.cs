namespace SemanticPortrait.Core.Constellation;

/// <summary>
/// Pure, deterministic motion offsets evaluated at a time t (seconds). The renderer feeds the current
/// time; these never hold state. Three distinct motions (design §10): Pulse = salience, Tremor =
/// volatility, (flow particles live in the renderer). Reduced-motion is enforced at the render layer.
/// </summary>
public static class Motion
{
    /// <summary>Salience pulse: a scalar in [−amp, amp] oscillating at freqHz. Add to node scale/opacity.</summary>
    public static double Pulse(double t, double amp, double freqHz)
        => amp * Math.Sin(2 * Math.PI * freqHz * t);

    /// <summary>
    /// Volatility tremor: a small deterministic (x,y) jitter, bounded by amp. Seeded per-node so
    /// different nodes shimmer out of phase. Deterministic given (t, seed).
    /// </summary>
    public static Pt Tremor(double t, int seed, double amp)
    {
        double p = seed * 0.6180339887;   // golden-ratio phase offset per node
        double x = amp * Math.Sin(2 * Math.PI * 0.7 * t + p);
        double y = amp * Math.Cos(2 * Math.PI * 0.9 * t + p * 1.3);
        return new Pt(x, y);
    }
}
