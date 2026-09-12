using Pointer.Application.Common;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// The magic-link credential itself: how it is minted, hashed and turned into a URL.
///
/// The link is a bearer credential for a low-privilege account. Three properties carry the weight —
/// it is unguessable, the raw value is never recoverable from storage, and building the URL cannot
/// destroy whatever query or fragment the customer's app already relies on.
/// </summary>
public class QuickAccessLinkTests
{
    [Fact]
    public void NewToken_IsUnguessable_AndUrlSafe()
    {
        var tokens = Enumerable.Range(0, 200).Select(_ => QuickAccessTokenGenerator.NewToken()).ToList();

        // 32 bytes base64url-encoded, padding stripped.
        Assert.All(tokens, t => Assert.Equal(43, t.Length));
        // No +, / or = — the token travels in a query string and must survive it untouched.
        Assert.All(tokens, t => Assert.Matches("^[A-Za-z0-9_-]+$", t));
        // 200 draws from a CSPRNG must not repeat.
        Assert.Equal(tokens.Count, tokens.Distinct().Count());
    }

    [Fact]
    public void Hash_IsStable_AndDoesNotContainTheToken()
    {
        var token = QuickAccessTokenGenerator.NewToken();

        var first = QuickAccessTokenGenerator.Hash(token);
        var second = QuickAccessTokenGenerator.Hash(token);

        Assert.Equal(first, second);              // lookup by hash depends on this
        Assert.Equal(64, first.Length);           // SHA-256 hex
        Assert.Matches("^[0-9a-f]+$", first);
        // The stored value must not be reversible into a working link by simple inspection.
        Assert.DoesNotContain(token, first);
    }

    [Fact]
    public void Hash_DiffersForDifferentTokens()
    {
        Assert.NotEqual(
            QuickAccessTokenGenerator.Hash(QuickAccessTokenGenerator.NewToken()),
            QuickAccessTokenGenerator.Hash(QuickAccessTokenGenerator.NewToken()));
    }

    [Theory]
    [InlineData("https://app.example.com", "https://app.example.com/?pointer_invite=TOK")]
    [InlineData("https://app.example.com/", "https://app.example.com/?pointer_invite=TOK")]
    [InlineData("https://app.example.com/board", "https://app.example.com/board?pointer_invite=TOK")]
    public void BuildMagicLink_AppendsTheToken(string appUrl, string expected)
    {
        Assert.Equal(expected, QuickAccessTokenGenerator.BuildMagicLink(appUrl, "TOK"));
    }

    [Fact]
    public void BuildMagicLink_PreservesAnExistingQueryString()
    {
        // The customer's app URL may already carry params it needs; clobbering them would break the
        // page the client is being invited to look at.
        var link = QuickAccessTokenGenerator.BuildMagicLink("https://app.example.com/b?tab=2&x=1", "TOK");

        Assert.Contains("tab=2", link);
        Assert.Contains("x=1", link);
        Assert.Contains("pointer_invite=TOK", link);
    }

    [Fact]
    public void BuildMagicLink_EscapesTheToken()
    {
        // base64url never produces these, but the encoder must not be the only thing standing
        // between a token and a broken URL.
        var link = QuickAccessTokenGenerator.BuildMagicLink("https://app.example.com", "a+b/c=d&e");

        Assert.DoesNotContain("&e", link.Split("pointer_invite=")[1]);
        Assert.Contains("pointer_invite=a%2Bb%2Fc%3Dd%26e", link);
    }
}
