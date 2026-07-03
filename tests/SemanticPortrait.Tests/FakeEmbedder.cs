using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>
/// Deterministic, offline embedder for tests: hashes word tokens into a fixed-size
/// bag-of-words vector. Identical text → identical vector; overlapping words → higher cosine.
/// Lets us test ranking / re-embedding without any network.
/// </summary>
public sealed class FakeEmbedder : IEmbedder
{
    private const int Dim = 64;

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        var v = new float[Dim];
        foreach (var tok in (text ?? "").ToLowerInvariant()
                     .Split(new[] { ' ', '\t', '\n', '.', ',', '!', '?', ';', ':', '/', '-', '(', ')' },
                            StringSplitOptions.RemoveEmptyEntries))
        {
            var bucket = (int)(Hash(tok) % Dim);
            v[bucket] += 1f;
        }
        return Task.FromResult<float[]?>(v);
    }

    private static uint Hash(string s)
    {
        uint h = 2166136261;
        foreach (var c in s) { h ^= c; h *= 16777619; }
        return h;
    }
}
