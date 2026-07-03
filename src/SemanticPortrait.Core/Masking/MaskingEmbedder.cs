namespace SemanticPortrait.Core;

/// <summary>
/// Wraps an <see cref="IEmbedder"/> with egress masking, closing the gap where chat text was
/// masked but the SAME text left the machine raw through the embedding endpoint. Alias tokens are
/// stable, so masked entries and masked queries still land near each other in vector space.
/// (Text embedded while masking was toggled the other way matches slightly worse on the aliased
/// names themselves — the surrounding content still carries the search.) Pass-through when the
/// masker is disabled.
/// </summary>
public sealed class MaskingEmbedder : IEmbedder
{
    private readonly IEmbedder _inner;
    private readonly IMasker _masker;

    public MaskingEmbedder(IEmbedder inner, IMasker masker) { _inner = inner; _masker = masker; }

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
        => _inner.EmbedAsync(_masker.Mask(text), ct);
}
