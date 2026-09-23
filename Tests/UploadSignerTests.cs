using Microsoft.Extensions.Configuration;
using Pointer.Infrastructure.Storage;

public class UploadSignerTests
{
    private static UploadSigner MakeSigner(string key = "test-signing-key-that-is-long-enough-32chars")
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { ["JWT:SigningKey"] = key })
            .Build();
        return new UploadSigner(config);
    }

    [Fact]
    public void SignedUrl_then_Validate_round_trips()
    {
        var signer = MakeSigner();
        var relPath = "uploads/global/myproject/abc123.png";

        var url = signer.SignedUrl(relPath);

        // Parse out the query parameters from the generated URL
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + url).Query);
        var p = query["p"]!;
        var exp = long.Parse(query["exp"]!);
        var sig = query["sig"]!;

        Assert.True(signer.Validate(p, exp, sig), "A freshly signed URL should validate as true.");
    }

    [Fact]
    public void Validate_returns_false_for_expired_url()
    {
        var signer = MakeSigner();
        var relPath = "uploads/global/myproject/abc123.png";

        var url = signer.SignedUrl(relPath);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + url).Query);
        var p = query["p"]!;
        var sig = query["sig"]!;

        // Use an expiry in the past
        var expiredExp = DateTimeOffset.UtcNow.AddSeconds(-1).ToUnixTimeSeconds();

        Assert.False(signer.Validate(p, expiredExp, sig), "An expired URL must not validate.");
    }

    [Fact]
    public void Validate_returns_false_for_tampered_sig()
    {
        var signer = MakeSigner();
        var relPath = "uploads/global/myproject/abc123.png";

        var url = signer.SignedUrl(relPath);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + url).Query);
        var p = query["p"]!;
        var exp = long.Parse(query["exp"]!);

        Assert.False(signer.Validate(p, exp, "tampered_signature_value"), "A tampered sig must not validate.");
    }

    [Fact]
    public void Validate_returns_false_for_tampered_relpath()
    {
        var signer = MakeSigner();
        var relPath = "uploads/global/myproject/abc123.png";

        var url = signer.SignedUrl(relPath);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + url).Query);
        var exp = long.Parse(query["exp"]!);
        var sig = query["sig"]!;

        Assert.False(signer.Validate("uploads/global/myproject/different.png", exp, sig), "A tampered relpath must not validate.");
    }

    [Fact]
    public void ExtractRelPath_handles_signed_url()
    {
        var signer = MakeSigner();
        var relPath = "uploads/global/myproject/abc123.png";
        var url = signer.SignedUrl(relPath);

        var extracted = signer.ExtractRelPath(url);
        Assert.Equal(relPath, extracted);
    }

    [Fact]
    public void ExtractRelPath_handles_absolute_public_url()
    {
        var signer = MakeSigner();
        var absoluteUrl = "https://myserver.example.com/uploads/global/proj/file.png";

        var extracted = signer.ExtractRelPath(absoluteUrl);
        Assert.Equal("uploads/global/proj/file.png", extracted);
    }

    [Fact]
    public void ExtractRelPath_handles_raw_relpath()
    {
        var signer = MakeSigner();
        var relPath = "uploads/global/proj/file.png";

        var extracted = signer.ExtractRelPath(relPath);
        Assert.Equal(relPath, extracted);
    }

    [Fact]
    public void ExtractRelPath_handles_null_or_empty()
    {
        var signer = MakeSigner();
        Assert.Equal(string.Empty, signer.ExtractRelPath(string.Empty));
    }

    // -----------------------------------------------------------------------
    // DB-13 review fix #2 — the notAfter clamp for an impersonating operator's screenshot URLs.
    // -----------------------------------------------------------------------

    [Fact]
    public void SignedUrl_WithNotAfterSoonerThanTtl_ClampsExpToNotAfter()
    {
        var signer = MakeSigner();
        var relPath = "uploads/global/myproject/abc123.png";
        var notAfter = DateTime.UtcNow.AddMinutes(5);

        var url = signer.SignedUrl(relPath, notAfter);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + url).Query);
        var exp = long.Parse(query["exp"]!);

        // Clamped to notAfter, not the normal 3600s TTL.
        Assert.True(exp <= new DateTimeOffset(notAfter).ToUnixTimeSeconds());
        Assert.True(exp < DateTimeOffset.UtcNow.AddSeconds(3600).ToUnixTimeSeconds());

        // And the URL still validates as a normal signed URL against that clamped expiry.
        var p = query["p"]!;
        var sig = query["sig"]!;
        Assert.True(signer.Validate(p, exp, sig));
    }

    [Fact]
    public void SignedUrl_WithNotAfterLaterThanTtl_UsesNormalTtl()
    {
        var signer = MakeSigner();
        var relPath = "uploads/global/myproject/abc123.png";
        // A notAfter far in the future must never LENGTHEN the URL's life past the normal TTL.
        var notAfter = DateTime.UtcNow.AddDays(1);

        var url = signer.SignedUrl(relPath, notAfter);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + url).Query);
        var exp = long.Parse(query["exp"]!);

        Assert.True(exp <= DateTimeOffset.UtcNow.AddSeconds(3601).ToUnixTimeSeconds());
    }

    [Fact]
    public void SignedUrl_WithNullNotAfter_SameAsSingleArgOverload()
    {
        var signer = MakeSigner();
        var relPath = "uploads/global/myproject/abc123.png";

        var url = signer.SignedUrl(relPath, null);
        var query = System.Web.HttpUtility.ParseQueryString(new Uri("http://localhost" + url).Query);
        var p = query["p"]!;
        var exp = long.Parse(query["exp"]!);
        var sig = query["sig"]!;

        Assert.True(signer.Validate(p, exp, sig));
        Assert.True(exp > DateTimeOffset.UtcNow.AddSeconds(3500).ToUnixTimeSeconds());
    }
}
