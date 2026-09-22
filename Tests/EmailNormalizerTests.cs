using Pointer.Application.Common;
using Xunit;

namespace Pointer.Tests;

/// <summary>DB-11a, GLM A1 — the one e-mail normaliser in the codebase.</summary>
public class EmailNormalizerTests
{
    [Fact]
    public void Normalize_TrimsAndLowers()
    {
        Assert.Equal("user@x.com", EmailNormalizer.Normalize("  User@X.COM "));
    }

    [Fact]
    public void Normalize_NullOrWhitespace_ReturnsNull()
    {
        Assert.Null(EmailNormalizer.Normalize(null));
        Assert.Null(EmailNormalizer.Normalize(""));
        Assert.Null(EmailNormalizer.Normalize("   "));
    }

    [Fact]
    public void NormalizeRequired_Whitespace_ReturnsEmpty()
    {
        Assert.Equal(string.Empty, EmailNormalizer.NormalizeRequired("   "));
        Assert.Equal(string.Empty, EmailNormalizer.NormalizeRequired(null));
    }
}
