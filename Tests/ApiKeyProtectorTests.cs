using System.Security.Cryptography;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.Infrastructure.Security;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// The real AES-GCM protector. These matter because the whole hardening rests on two properties:
/// the hash is deterministic (so login can match on it) and the blob is authenticated (so a tampered
/// or wrongly-keyed value fails closed instead of returning garbage).
/// </summary>
public class ApiKeyProtectorTests
{
    private static ApiKeyProtector Build(string? dedicatedKey = null, string? jwtKey = "a-signing-key-of-at-least-32-bytes!!")
    {
        var values = new Dictionary<string, string?>();
        if (dedicatedKey is not null)
            values["Auth:ApiKeyEncryptionKey"] = dedicatedKey;
        if (jwtKey is not null)
            values["JWT:SigningKey"] = jwtKey;

        var config = new ConfigurationBuilder().AddInMemoryCollection(values).Build();
        return new ApiKeyProtector(config, NullLogger<ApiKeyProtector>.Instance);
    }

    private static string NewKeyMaterial() => Convert.ToBase64String(RandomNumberGenerator.GetBytes(32));

    [Fact]
    public void Hash_Is_Deterministic_And_Differs_Per_Key()
    {
        var p = Build(NewKeyMaterial());

        Assert.Equal(p.Hash("ptr_abc"), p.Hash("ptr_abc"));
        Assert.NotEqual(p.Hash("ptr_abc"), p.Hash("ptr_abd"));
        Assert.Equal(64, p.Hash("ptr_abc").Length); // hex SHA-256
    }

    [Fact]
    public void Encrypt_Then_Decrypt_Round_Trips()
    {
        var p = Build(NewKeyMaterial());
        const string raw = "ptr_0123456789abcdef0123456789abcdef01234567";

        Assert.Equal(raw, p.Decrypt(p.Encrypt(raw)));
    }

    [Fact]
    public void Encrypt_Is_Non_Deterministic()
    {
        // A fresh nonce per call: two encryptions of the same key must not produce the same blob,
        // or the ciphertext would leak which users share a key value.
        var p = Build(NewKeyMaterial());

        Assert.NotEqual(p.Encrypt("ptr_same"), p.Encrypt("ptr_same"));
    }

    [Fact]
    public void Decrypt_With_A_Different_Key_Returns_Null()
    {
        // The rotation case: login still works (hash), display does not. Must fail closed, not throw.
        var blob = Build(NewKeyMaterial()).Encrypt("ptr_secret");

        Assert.Null(Build(NewKeyMaterial()).Decrypt(blob));
    }

    [Fact]
    public void Decrypt_Rejects_A_Tampered_Blob()
    {
        var p = Build(NewKeyMaterial());
        var blob = Convert.FromBase64String(p.Encrypt("ptr_secret"));
        blob[^1] ^= 0xFF; // flip a bit in the auth tag

        Assert.Null(p.Decrypt(Convert.ToBase64String(blob)));
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")] // valid base64, far too short to contain nonce+tag
    public void Decrypt_Rejects_Malformed_Input(string blob) => Assert.Null(Build(NewKeyMaterial()).Decrypt(blob));

    [Fact]
    public void Derives_A_Stable_Key_From_The_Jwt_Secret_When_None_Is_Configured()
    {
        // Zero-config boot: two instances must agree, or a restart would orphan every key.
        var a = Build(dedicatedKey: null);
        var b = Build(dedicatedKey: null);

        Assert.Equal("ptr_derived", b.Decrypt(a.Encrypt("ptr_derived")));
    }

    [Fact]
    public void Throws_When_Neither_Key_Is_Configured()
    {
        var ex = Assert.Throws<InvalidOperationException>(() => Build(dedicatedKey: null, jwtKey: null));
        Assert.Contains("cannot protect API keys", ex.Message);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("c2hvcnQ=")] // decodes to 5 bytes, not 32
    public void Rejects_A_Malformed_Configured_Key(string configured) =>
        Assert.Throws<InvalidOperationException>(() => Build(configured));
}
