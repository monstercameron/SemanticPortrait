namespace SemanticPortrait.Core;

/// <summary>Produces an embedding vector for text. Abstracted so tests can inject a fake.</summary>
public interface IEmbedder
{
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
}
