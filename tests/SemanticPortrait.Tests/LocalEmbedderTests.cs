using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

public class LocalEmbedderTests
{
    // The repo-root models/minilm copy (gitignored, ~90MB) — tests skip gracefully without it.
    private static string RepoModelDir()
    {
        var dir = AppContext.BaseDirectory;
        for (int i = 0; i < 8 && dir is not null; i++, dir = Path.GetDirectoryName(dir)!)
        {
            var p = Path.Combine(dir, "models", "minilm");
            if (File.Exists(Path.Combine(p, "model.onnx"))) return p;
        }
        return Path.Combine(Path.GetTempPath(), "no-model-here");
    }

    [Fact]
    public async Task Embeds_locally_with_real_semantics_when_model_present()
    {
        var local = new LocalEmbedder(RepoModelDir());
        if (!local.IsAvailable) return;   // model not downloaded — nothing to assert offline

        var a = await local.EmbedAsync("I went to the gym and lifted weights this morning.");
        var b = await local.EmbedAsync("This morning's workout: strength training at the fitness studio.");
        var c = await local.EmbedAsync("The quarterly tax filing deadline is next Tuesday.");
        Assert.NotNull(a); Assert.NotNull(b); Assert.NotNull(c);
        Assert.Equal(384, a!.Length);

        static double Cos(float[] x, float[] y) =>
            x.Zip(y, (p, q) => (double)p * q).Sum();   // vectors are L2-normalized
        Assert.True(Cos(a, b!) > Cos(a, c!) + 0.1,
            $"related {Cos(a, b!):0.000} should beat unrelated {Cos(a, c!):0.000}");
        local.Dispose();
    }

    [Fact]
    public async Task Missing_model_returns_null_and_hybrid_falls_back_to_cloud()
    {
        var local = new LocalEmbedder(Path.Combine(Path.GetTempPath(), $"none_{Guid.NewGuid():N}"));
        Assert.False(local.IsAvailable);
        Assert.Null(await local.EmbedAsync("anything"));

        var hybrid = new PreferLocalEmbedder(local, new FakeEmbedder());
        Assert.False(hybrid.LocalActive);
        Assert.NotNull(await hybrid.EmbedAsync("anything"));   // FakeEmbedder answered
    }

    // Privacy invariant: once local embeddings are installed, the hybrid must NEVER reach the
    // cloud — a user installed them so entry text stays on-device. A throwing cloud proves it's
    // never touched. (Skips when the ~90MB model isn't downloaded, like the semantics test.)
    [Fact]
    public async Task Local_installed_never_touches_cloud()
    {
        var local = new LocalEmbedder(RepoModelDir());
        if (!local.IsAvailable) return;   // model not present — nothing to prove

        var hybrid = new PreferLocalEmbedder(local, new ThrowingEmbedder());
        Assert.True(hybrid.LocalActive);
        var v = await hybrid.EmbedAsync("the cloud embedder must not be called");
        Assert.NotNull(v);                // answered locally, ThrowingEmbedder never ran
        local.Dispose();
    }

    private sealed class ThrowingEmbedder : IEmbedder
    {
        public Task<float[]?> EmbedAsync(string text, CancellationToken ct = default) =>
            throw new InvalidOperationException("cloud embedder must not be reached when local is active");
    }
}
