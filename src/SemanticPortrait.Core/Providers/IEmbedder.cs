namespace SemanticPortrait.Core;

/// <summary>Produces an embedding vector for text. Abstracted so tests can inject a fake.</summary>
public interface IEmbedder
{
    Task<float[]?> EmbedAsync(string text, CancellationToken ct = default);
}

/// <summary>Optional capability for an embedder that can report whether it is currently running
/// fully on-device (no cloud egress). Consumers probe this via <c>is ILocalityProbe</c> instead of
/// downcasting to a concrete implementation — so wrapping the embedder in another decorator can
/// never throw an InvalidCastException, and an unknown embedder safely reads as "not local".</summary>
public interface ILocalityProbe
{
    bool LocalActive { get; }
}
