using Pointer.Application.Common;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Wildcard app-URL patterns (R1-05). These are an authorisation boundary — a pattern decides who may
/// post comments into a project — so the interesting cases are the rejections, not the matches.
/// </summary>
public class OriginPatternTests
{
    // ── Matching ────────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://myapp-*.vercel.app", "https://myapp-pr-12.vercel.app")]
    [InlineData("https://myapp-*.vercel.app", "https://myapp-.vercel.app")] // empty glob run
    [InlineData("https://pr-*.preview.acme.com", "https://pr-7.preview.acme.com")]
    [InlineData("https://*.staging.acme.com", "https://anything.staging.acme.com")]
    [InlineData("https://*.staging.acme.com", "HTTPS://Anything.Staging.Acme.Com/")] // case + trailing slash
    public void Matches_Accepts_What_It_Should(string pattern, string origin) =>
        Assert.True(OriginNormalizer.Matches(pattern, origin));

    [Theory]
    // A different tenant on the same shared host — the whole reason the literal prefix is required.
    [InlineData("https://myapp-*.vercel.app", "https://someoneelse.vercel.app")]
    // Label count must match, or a deeper host slips through the wildcard.
    [InlineData("https://*.staging.acme.com", "https://evil.attacker.staging.acme.com")]
    [InlineData("https://*.staging.acme.com", "https://staging.acme.com")]
    // The wildcard never crosses a dot.
    [InlineData("https://myapp-*.vercel.app", "https://myapp-x.y.vercel.app")]
    // Scheme and port are part of the origin.
    [InlineData("https://*.staging.acme.com", "http://a.staging.acme.com")]
    [InlineData("https://*.staging.acme.com:8443", "https://a.staging.acme.com")]
    // Suffix must match literally.
    [InlineData("https://myapp-*.vercel.app", "https://myapp-1.vercel.app.evil.com")]
    public void Matches_Rejects_What_It_Should(string pattern, string origin) =>
        Assert.False(OriginNormalizer.Matches(pattern, origin));

    [Fact]
    public void Matches_Falls_Back_To_Exact_Comparison_Without_A_Wildcard()
    {
        Assert.True(OriginNormalizer.Matches("https://app.acme.com", "https://app.acme.com/"));
        Assert.False(OriginNormalizer.Matches("https://app.acme.com", "https://other.acme.com"));
    }

    [Fact]
    public void Matches_Refuses_A_Pattern_It_Would_Not_Have_Saved()
    {
        // Defence in depth: even if an over-broad pattern reached the database (seeded by hand, or
        // written before this rule existed), matching must not honour it.
        Assert.False(OriginNormalizer.Matches("https://*.vercel.app", "https://anyone.vercel.app"));
        Assert.False(OriginNormalizer.Matches("https://*.acme.com", "https://anyone.acme.com"));
    }

    [Theory]
    [InlineData("")]
    [InlineData(null)]
    public void Matches_Handles_Empty_Input(string? origin) =>
        Assert.False(OriginNormalizer.Matches("https://*.staging.acme.com", origin!));

    // ── Validation ──────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("https://app.acme.com")] // no wildcard at all
    [InlineData("https://myapp-*.vercel.app")] // literal prefix on a shared host
    [InlineData("https://pr-*.preview.acme.com")]
    [InlineData("https://*.staging.acme.com")] // bare wildcard, three labels, own domain
    [InlineData("https://*.staging.acme.com:8443")]
    public void ValidatePattern_Accepts(string pattern) => Assert.Null(OriginNormalizer.ValidatePattern(pattern));

    [Fact]
    public void ValidatePattern_Rejects_A_Bare_Wildcard_On_Shared_Hosting()
    {
        // Would authorise every other customer of that platform.
        var error = OriginNormalizer.ValidatePattern("https://*.vercel.app");

        Assert.NotNull(error);
        Assert.Contains("other people", error);
    }

    [Fact]
    public void ValidatePattern_Rejects_A_Bare_Wildcard_Under_A_Shared_Suffix()
    {
        // The ends-with rule: *.foo.github.io is still every GitHub Pages site under foo.
        Assert.NotNull(OriginNormalizer.ValidatePattern("https://*.foo.github.io"));
    }

    [Fact]
    public void ValidatePattern_Rejects_Too_Few_Labels()
    {
        var error = OriginNormalizer.ValidatePattern("https://*.acme.com");

        Assert.NotNull(error);
        Assert.Contains("too broad", error);
    }

    [Theory]
    [InlineData("https://*-*.acme.com", "at most one")]
    [InlineData("https://app.*.acme.com", "left-most")]
    [InlineData("https://*.staging.acme.com/path", "no path")]
    [InlineData("not-a-url-*", "absolute URL")]
    [InlineData("", "required")]
    public void ValidatePattern_Rejects_With_A_Useful_Message(string pattern, string expected)
    {
        var error = OriginNormalizer.ValidatePattern(pattern);

        Assert.NotNull(error);
        Assert.Contains(expected, error);
    }

    [Fact]
    public void A_Literal_Prefix_Rescues_A_Shared_Host()
    {
        // The documented escape hatch: scoped to one account's naming convention.
        Assert.Null(OriginNormalizer.ValidatePattern("https://acme-*.vercel.app"));
        Assert.True(OriginNormalizer.Matches("https://acme-*.vercel.app", "https://acme-pr-3.vercel.app"));
        Assert.False(OriginNormalizer.Matches("https://acme-*.vercel.app", "https://beta-pr-3.vercel.app"));
    }
}
