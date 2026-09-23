using Microsoft.Extensions.Configuration;
using Pointer.Infrastructure.Auth;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-11c §3.4a — the purpose-bound scoped token variant added to <see cref="ResetTokenService"/>.
/// A scoped token never validates through the plain <c>TryValidate</c> (4 parts) and a plain reset
/// token never validates through <c>TryValidateScoped</c> (6 parts + purpose match), so an e-mailed
/// link can only do the one thing it was minted for.
/// </summary>
public class ResetTokenServiceTests
{
    private static ResetTokenService BuildService() =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?> { ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789" }
                )
                .Build()
        );

    [Fact]
    public void Scoped_RoundTrip_WithPayload()
    {
        var svc = BuildService();
        var id = Guid.NewGuid();
        var stamp = Guid.NewGuid();

        var token = svc.CreateScoped(id, stamp, "change-email", "new@x.com");

        Assert.True(svc.TryValidateScoped(token, "change-email", out var outId, out var outStamp, out var payload));
        Assert.Equal(id, outId);
        Assert.Equal(stamp, outStamp);
        Assert.Equal("new@x.com", payload);
    }

    [Fact]
    public void Scoped_WrongPurpose_Fails()
    {
        var svc = BuildService();
        var id = Guid.NewGuid();
        var stamp = Guid.NewGuid();

        var token = svc.CreateScoped(id, stamp, "erase");

        Assert.False(svc.TryValidateScoped(token, "change-email", out _, out _, out _));
    }

    [Fact]
    public void Scoped_RejectedByPlainTryValidate()
    {
        var svc = BuildService();
        var token = svc.CreateScoped(Guid.NewGuid(), Guid.NewGuid(), "erase");

        Assert.False(svc.TryValidate(token, out _, out _));
    }

    [Fact]
    public void Plain_RejectedByTryValidateScoped()
    {
        var svc = BuildService();
        var token = svc.Create(Guid.NewGuid(), Guid.NewGuid());

        Assert.False(svc.TryValidateScoped(token, "erase", out _, out _, out _));
    }

    [Fact]
    public void Scoped_TamperedPayload_Fails()
    {
        var svc = BuildService();
        var token = svc.CreateScoped(Guid.NewGuid(), Guid.NewGuid(), "change-email", "a@b.com");
        var parts = token.Split('.');
        parts[4] = parts[4].Length > 0 ? "x" + parts[4] : "x";
        var tampered = string.Join('.', parts);

        Assert.False(svc.TryValidateScoped(tampered, "change-email", out _, out _, out _));
    }

    [Fact]
    public void Scoped_PurposeWithDot_Throws()
    {
        var svc = BuildService();

        Assert.Throws<ArgumentException>(() => svc.CreateScoped(Guid.NewGuid(), Guid.NewGuid(), "bad.purpose"));
    }
}
