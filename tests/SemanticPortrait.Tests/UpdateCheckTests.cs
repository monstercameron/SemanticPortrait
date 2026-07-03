using SemanticPortrait.Core;

namespace SemanticPortrait.Tests;

/// <summary>Version comparison for the launch-time update check: prerelease suffixes strip,
/// malformed input never reports "newer" (silent no-op is the failure mode everywhere).</summary>
public class UpdateCheckTests
{
    [Theory]
    [InlineData("v1.0.0", "0.9.0", true)]
    [InlineData("v0.9.1", "v0.9.0", true)]
    [InlineData("v0.9.0", "0.9.0", false)]           // equal is not newer
    [InlineData("v0.9.0", "0.10.0", false)]          // numeric, not lexicographic
    [InlineData("v0.9.0-beta", "0.8.0", true)]       // prerelease suffix strips
    [InlineData("v1.0", "0.9.0", true)]              // short form tolerated
    [InlineData("garbage", "0.9.0", false)]          // malformed → never "newer"
    [InlineData("v1.0.0", "not-a-version", false)]
    public void IsNewer_compares_numerically_and_fails_closed(string candidate, string current, bool expected)
        => Assert.Equal(expected, UpdateCheck.IsNewer(candidate, current));
}
