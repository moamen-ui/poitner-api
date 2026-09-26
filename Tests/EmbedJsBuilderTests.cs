using Pointer.API.Extensions;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// The <c>/embed.js</c> loader used to always write an `environment` attribute (defaulting to
/// "staging"), which — per pointer-init.md — overrides the widget's own origin-based environment
/// resolution for every install. These pin the fix: no explicit, valid `?environment=` means no
/// attribute at all.
/// </summary>
public class EmbedJsBuilderTests
{
    [Fact]
    public void Build_WithoutEnvironment_OmitsEnvironmentAttribute()
    {
        var js = EmbedJsBuilder.Build("https://api.example.com", "proj-key", environment: null);

        Assert.DoesNotContain("setAttribute('environment'", js);
        Assert.Contains("setAttribute('project', 'proj-key')", js);
        Assert.Contains("var server = 'https://api.example.com';", js);
    }

    [Fact]
    public void Build_WithExplicitEnvironment_EmitsEnvironmentAttribute()
    {
        var js = EmbedJsBuilder.Build(
            "https://api.example.com",
            "proj-key",
            environment: "production"
        );

        Assert.Contains("setAttribute('environment', 'production')", js);
        Assert.Contains("setAttribute('project', 'proj-key')", js);
    }

    [Theory]
    [InlineData("")]
    [InlineData("staging")]
    [InlineData("local")]
    [InlineData("a.b-c_d1")]
    public void Safe_AcceptsExpected(string value)
    {
        // Empty is the one case Safe rejects — everything else made of letters/digits/./_/- passes.
        Assert.Equal(value.Length > 0, EmbedJsBuilder.Safe(value));
    }

    [Theory]
    [InlineData("staging; alert(1)")]
    [InlineData("<script>")]
    [InlineData("a b")]
    [InlineData("a'b")]
    public void Safe_RejectsUnsafeCharacters(string value)
    {
        Assert.False(EmbedJsBuilder.Safe(value));
    }
}
