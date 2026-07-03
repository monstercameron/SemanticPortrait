namespace SemanticPortrait.Core.Constellation;

/// <summary>Emotions on the constellation hue wheel. Neutral = read, no strong emotion; Unmapped = we
/// could not read the mood text (rendered distinctly so "unreadable" never poses as "calm").</summary>
public enum Emotion { Anger, Disgust, Creativity, Calm, Sadness, Fear, Love, Joy, Neutral, Unmapped }

/// <summary>HSL color: H in [0,360), S/L in [0,1].</summary>
public readonly record struct Hsl(double H, double S, double L);

/// <summary>
/// Maps free-text mood → <see cref="Emotion"/> (keyword lexicon) and Emotion+valence+intensity → HSL.
/// Pure + deterministic + unit-tested. Hue = which emotion; lightness = valence (good↔bad);
/// saturation = intensity. Lexicon/wheel are intentionally simple now; a local NER/embedding upgrade
/// can replace Classify later without touching callers.
/// </summary>
public static class EmotionColor
{
    // Wheel angles (deg). See design/constellation.md §5.
    // Neutral/Unmapped return NaN (no meaningful hue) so any misuse on a polar layout is loud, not Anger.
    public static double Hue(Emotion e) => e switch
    {
        Emotion.Anger => 0,
        Emotion.Joy => 48,
        Emotion.Disgust => 80,
        Emotion.Creativity => 140,
        Emotion.Calm => 200,
        Emotion.Sadness => 230,
        Emotion.Fear => 275,
        Emotion.Love => 320,
        _ => double.NaN,
    };

    private static readonly HashSet<string> _negators = new()
        { "not", "no", "never", "without", "barely", "hardly", "isnt", "arent", "wasnt", "dont", "didnt" };

    // Keyword lexicon. ORDER IS LOAD-BEARING: it is the tie-break precedence when two emotions score
    // equally (the first listed wins). Do not reorder casually — it changes classification. Tested.
    private static readonly (Emotion E, string[] Words)[] _lexicon =
    {
        (Emotion.Anger,      new[] { "angry", "anger", "furious", "rage", "irritated", "frustrated", "resentful", "mad" }),
        (Emotion.Fear,       new[] { "anxious", "anxiety", "afraid", "scared", "nervous", "worried", "dread", "panic", "terror", "fear" }),
        (Emotion.Sadness,    new[] { "sad", "empty", "down", "depressed", "grief", "hopeless", "lonely", "hurt", "numb", "low" }),
        (Emotion.Joy,        new[] { "happy", "joy", "joyful", "glad", "excited", "elated", "grateful", "content", "hopeful" }),
        (Emotion.Calm,       new[] { "calm", "peaceful", "relaxed", "settled", "steady", "grounded", "serene" }),
        (Emotion.Creativity, new[] { "creative", "inspired", "productive", "flow", "building", "energized", "motivated", "driven" }),
        (Emotion.Love,       new[] { "love", "loving", "longing", "affection", "tender", "warm", "attached", "yearning" }),
        (Emotion.Disgust,    new[] { "disgust", "gross", "repulsed", "ashamed", "shame", "guilty", "guilt" }),
        (Emotion.Neutral,    new[] { "neutral", "fine", "okay", "ok", "meh", "indifferent", "blah" }),
    };

    /// <summary>
    /// Classify a free-text mood. Token-based whole-word matching with light negation handling
    /// ("not anxious" does NOT score Fear). Highest-scoring emotion wins; ties break by lexicon order.
    /// Known limitation (lexicon v1): only adjacent single-word negators are caught; contractions
    /// split on the apostrophe. A local NER/embedding classifier replaces this later without API change.
    /// </summary>
    public static Emotion Classify(string? mood)
    {
        if (string.IsNullOrWhiteSpace(mood)) return Emotion.Unmapped;
        var tokens = Tokenize(mood.ToLowerInvariant());

        var scores = new Dictionary<Emotion, int>();
        for (int i = 0; i < tokens.Count; i++)
        {
            if (i > 0 && _negators.Contains(tokens[i - 1])) continue;   // skip negated keyword
            foreach (var (e, words) in _lexicon)
                if (Array.IndexOf(words, tokens[i]) >= 0)
                    scores[e] = scores.GetValueOrDefault(e) + 1;
        }

        Emotion best = Emotion.Unmapped;
        int bestScore = 0;
        foreach (var (e, _) in _lexicon)   // iterate in precedence order → first with max score wins
        {
            var s = scores.GetValueOrDefault(e);
            if (s > bestScore) { bestScore = s; best = e; }
        }
        return best;
    }

    private static List<string> Tokenize(string text)
    {
        var tokens = new List<string>();
        int i = 0;
        while (i < text.Length)
        {
            if (!char.IsLetter(text[i])) { i++; continue; }
            int start = i;
            while (i < text.Length && char.IsLetter(text[i])) i++;
            tokens.Add(text[start..i]);
        }
        return tokens;
    }

    /// <param name="valence">−1 (bad) .. +1 (good) → lightness.</param>
    /// <param name="intensity">0 .. 1 → saturation.</param>
    public static Hsl ToHsl(Emotion e, double valence, double intensity)
    {
        valence = Math.Clamp(valence, -1, 1);
        intensity = Math.Clamp(intensity, 0, 1);

        // Both are desaturated greys, but DISTINCT values so a caller that only sees the Hsl can still
        // tell "couldn't read this" (Unmapped) from a real "calm/neutral" — Unmapped never poses as calm.
        if (e is Emotion.Neutral) return new Hsl(0, 0.05, 0.50);
        if (e is Emotion.Unmapped) return new Hsl(0, 0.00, 0.45);

        double hue = Hue(e);
        double sat = Math.Clamp(0.35 + 0.50 * intensity + 0.15 * Math.Abs(valence), 0, 1);
        double light = Math.Clamp(0.5 + 0.20 * valence, 0.2, 0.8);   // monotonic in valence
        return new Hsl(hue, sat, light);
    }
}
