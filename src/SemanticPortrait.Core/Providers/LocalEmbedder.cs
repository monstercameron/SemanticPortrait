using Microsoft.ML.OnnxRuntime;
using Microsoft.ML.OnnxRuntime.Tensors;
using Microsoft.ML.Tokenizers;

namespace SemanticPortrait.Core;

/// <summary>
/// 100%-local embeddings via all-MiniLM-L6-v2 (ONNX, CPU): nothing leaves the machine and
/// embedding costs nothing. Loads model.onnx + vocab.txt from <see cref="ModelDir"/> when
/// present (the app offers the ~90MB download); mean-pooled, L2-normalized 384-dim vectors.
/// </summary>
public sealed class LocalEmbedder : IEmbedder, IDisposable
{
    private const int MaxTokens = 256;

    public string ModelDir { get; }
    private readonly object _gate = new();
    private InferenceSession? _session;
    private BertTokenizer? _tokenizer;

    public LocalEmbedder(string modelDir) => ModelDir = modelDir;

    public string ModelPath => Path.Combine(ModelDir, "model.onnx");
    public string VocabPath => Path.Combine(ModelDir, "vocab.txt");
    public bool IsAvailable => File.Exists(ModelPath) && File.Exists(VocabPath);

    public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(text) || !IsAvailable) return Task.FromResult<float[]?>(null);
        return Task.Run(() =>
        {
            try
            {
                InferenceSession session;
                BertTokenizer tokenizer;
                lock (_gate)
                {
                    _tokenizer ??= BertTokenizer.Create(VocabPath);
                    _session ??= new InferenceSession(ModelPath);
                    tokenizer = _tokenizer;
                    session = _session;
                }

                // BertTokenizer adds [CLS]/[SEP] itself.
                var ids = tokenizer.EncodeToIds(text, MaxTokens, out _, out _).Select(i => (long)i).ToArray();
                var n = ids.Length;
                var inputIds = new DenseTensor<long>(ids, new[] { 1, n });
                var mask = new DenseTensor<long>(Enumerable.Repeat(1L, n).ToArray(), new[] { 1, n });
                var types = new DenseTensor<long>(new long[n], new[] { 1, n });

                using var results = session.Run(new[]
                {
                    NamedOnnxValue.CreateFromTensor("input_ids", inputIds),
                    NamedOnnxValue.CreateFromTensor("attention_mask", mask),
                    NamedOnnxValue.CreateFromTensor("token_type_ids", types),
                });
                // last_hidden_state: [1, n, 384] → mean-pool over tokens → L2 normalize
                var hidden = results.First().AsTensor<float>();
                var dim = hidden.Dimensions[2];
                var vec = new float[dim];
                for (int t = 0; t < n; t++)
                    for (int d = 0; d < dim; d++)
                        vec[d] += hidden[0, t, d];
                var norm = 0.0;
                for (int d = 0; d < dim; d++) { vec[d] /= n; norm += vec[d] * vec[d]; }
                norm = Math.Sqrt(norm);
                if (norm > 0) for (int d = 0; d < dim; d++) vec[d] = (float)(vec[d] / norm);
                return (float[]?)vec;
            }
            catch { return null; }   // corrupt model / OOM → callers fall back gracefully
        }, ct);
    }

    public void Dispose() { lock (_gate) { _session?.Dispose(); _session = null; } }
}

/// <summary>
/// Routes embeddings to the local model when it's installed (nothing leaves, costs nothing),
/// else to the cloud embedder (which is masking-wrapped). Availability is re-checked per call
/// so finishing the model download switches over without a restart.
///
/// Privacy invariant: when the local model IS installed, this NEVER egresses to the cloud — not
/// even on a transient local failure. A user who installed local embeddings did so precisely so
/// entry text stays on-device, so a one-off OOM/corrupt-read must degrade recall (skip the embed),
/// never silently leak the entry to OpenAI. Cloud is used ONLY when no local model is present.
/// </summary>
public sealed class PreferLocalEmbedder : IEmbedder, ILocalityProbe
{
    private readonly LocalEmbedder _local;
    private readonly IEmbedder _cloud;

    public PreferLocalEmbedder(LocalEmbedder local, IEmbedder cloud) { _local = local; _cloud = cloud; }

    public bool LocalActive => _local.IsAvailable;

    public async Task<float[]?> EmbedAsync(string text, CancellationToken ct = default)
    {
        // Local installed → stay on-device unconditionally. A null (local failure) skips this
        // embed rather than falling through to the cloud; the "nothing leaves" guarantee holds.
        if (_local.IsAvailable)
            return await _local.EmbedAsync(text, ct);
        return await _cloud.EmbedAsync(text, ct);
    }
}
