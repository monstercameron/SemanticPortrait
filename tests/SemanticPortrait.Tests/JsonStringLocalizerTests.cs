using SemanticPortrait.Core.Localization;

namespace SemanticPortrait.Tests;

// Proves the i18n seam actually resolves the embedded en.json catalog at runtime — not just
// that a missing key degrades to the key name. Loads the SAME embedded resource the app's
// JsonStringLocalizerFactory loads (SemanticPortrait.Core.Localization.Strings.en.json).
public class JsonStringLocalizerTests
{
    [Fact]
    public void KnownKey_ResolvesToEnglishText_FromEmbeddedCatalog()
    {
        var localizer = JsonStringLocalizer.FromEmbeddedResource(JsonStringLocalizerFactory.DefaultLogicalName);

        var result = localizer["Lock.EnableLock"];

        Assert.Equal("Enable lock", result.Value);
        Assert.False(result.ResourceNotFound);
    }

    [Fact]
    public void ParameterizedKey_FormatsArguments()
    {
        var localizer = JsonStringLocalizer.FromEmbeddedResource(JsonStringLocalizerFactory.DefaultLogicalName);

        var result = localizer["Lock.LockedOut", 30];

        Assert.Equal("Too many wrong attempts — locked for 30 seconds.", result.Value);
    }

    [Fact]
    public void MissingKey_DegradesToTheKeyItself_NeverThrows()
    {
        var localizer = JsonStringLocalizer.FromEmbeddedResource(JsonStringLocalizerFactory.DefaultLogicalName);

        var result = localizer["Some.Key.That.Does.Not.Exist"];

        Assert.Equal("Some.Key.That.Does.Not.Exist", result.Value);
        Assert.True(result.ResourceNotFound);
    }

    [Fact]
    public void Factory_CreateIgnoresResourceSource_ReturnsSharedCatalog()
    {
        var factory = new JsonStringLocalizerFactory();

        var localizer = factory.Create(typeof(JsonStringLocalizerTests));

        Assert.Equal("Wrong PIN.", localizer["Lock.WrongPin"].Value);
    }
}
