namespace SemanticPortrait.Core.Constellation;

/// <summary>
/// Render-agnostic visual attributes for one node. Coordinates are normalized 0..1 (the renderer
/// scales to the viewport). Every field traces to a metric (see <see cref="Why"/> for the inspector).
/// </summary>
public readonly record struct VisualNode(
    long Id, string Label,
    string Category,                    // domain word — subtitles, chips
    double X, double Y,                 // affect-circumplex position (rank-spread + relaxed — NOT the raw values)
    double Valence, double Energy,      // the RAW metrics, for honest inspector readouts
    int Sides, bool IsCircle,
    double FillH, double FillS, double FillL,
    string StrokeHex,                   // domain
    double Radius,                      // 0..1 of a base unit
    double Opacity, bool Dashed,        // confidence / inferred
    double PulseAmp, double PulseFreq,  // salience (amp is a fraction of radius)
    double TremorAmp,                   // volatility (0 = steady)
    double GlowWarmth,                  // fire/flow
    bool Quiet, bool IsUnmapped,
    bool Ambivalent,                    // both-sides truth: holds opposing valences → split fill
    int Rings,                          // temporal depth: 0..3 from how often entries revisit it
    string Why);

/// <summary>An asterism: the soft outline around one connected community of the graph — the
/// gestalt grouping visible when zoomed out, named for its dominant domain.</summary>
public sealed record VisualHull(IReadOnlyList<Pt> Points, string TintHex, string Name);

public readonly record struct VisualEdge(
    long Id,   // the graph edge's own id — (Src,Dst) is NOT unique (multiple typed relations per pair)
    long Src, long Dst, string ColorHex,
    double Frequency, double Amplitude,   // uncertainty → wiggle (signed amplitude picks the bow side)
    int Valence,
    double Width,                         // confidence → thickness
    bool Dashed,                          // inferred vs stated
    double PulseAmp, double PulseFreq);   // liveness (endpoint salience) → opacity throb

/// <summary>The complete render payload: nodes + edges + asterisms + gestalt + join coverage.</summary>
public sealed record VisualModel(
    IReadOnlyList<VisualNode> Nodes,
    IReadOnlyList<VisualEdge> Edges,
    IReadOnlyList<VisualHull> Hulls,
    double MeanValence,
    double LinkedFraction,
    JoinReport Join);
