using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Controllers;
using Pointer.API.Extensions;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// BINDING (R5-59): password login IS rate-limited, per normalised e-mail address — NOT per IP,
/// because the widget is called from arbitrary host origins and a whole office/agency behind one
/// NAT address must not share a single budget. This reverses the pre-R5-59 decision (login was
/// deliberately unlimited); the per-IP "signup" limiter remains separate and continues to cover
/// only the account-creation/password-email surface. Magic-link redemption ("login-with-invite"
/// and, since R5-59, "login-with-key") keeps its own per-IP "login" policy — a 256-bit token is
/// already the credential, so IP partitioning is enough there.
/// Rejections must read as throttling (429 + Retry-After), not an outage (503).
/// </summary>
public class AuthRateLimitingTests
{
    [Fact]
    public void Login_HasPasswordLoginRateLimit()
    {
        var method = typeof(AuthController).GetMethod("Login");
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true).ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "password-login");
    }

    [Fact]
    public void LoginWithKey_HasLoginRateLimit()
    {
        var method = typeof(AuthController).GetMethod("LoginWithKey");
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true).ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "login");
    }

    [Theory]
    [InlineData("Register")]
    [InlineData("RegisterAdmin")]
    [InlineData("RegisterInvite")]
    [InlineData("ForgotPassword")]
    [InlineData("ResetPassword")]
    public void SignupSurface_KeepsSignupRateLimit(string action)
    {
        var method = typeof(AuthController).GetMethod(action);
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true).ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "signup");
    }

    /// <summary>
    /// The device-code sign-in endpoints must NOT share the 5-per-hour "signup" budget: the CLI polls
    /// `device/poll` every ~3s for up to 10 minutes, so that budget was gone 15 seconds into the first
    /// sign-in and every later `start` from the same IP was a 429 (prod, 2026-09-17). Each has its own
    /// policy sized for its traffic.
    /// </summary>
    [Theory]
    [InlineData("DeviceStart", "device-start")]
    [InlineData("DevicePoll", "device-poll")]
    public void DeviceCodeSurface_HasItsOwnRateLimit(string action, string policy)
    {
        var method = typeof(AuthController).GetMethod(action);
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true).ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == policy);
        Assert.DoesNotContain(rateLimits, a => a.PolicyName == "signup");
    }

    [Fact]
    public void RateLimiter_RejectsWith429_NotDefault503()
    {
        var o = new RateLimiterOptions();
        RateLimitingExtensions.Configure(o);

        Assert.Equal(StatusCodes.Status429TooManyRequests, o.RejectionStatusCode);
    }

    [Fact]
    public async Task RateLimiter_OnRejected_SetsRetryAfterSeconds()
    {
        var o = new RateLimiterOptions();
        RateLimitingExtensions.Configure(o);
        Assert.NotNull(o.OnRejected);

        var http = new DefaultHttpContext();
        var ctx = new OnRejectedContext
        {
            HttpContext = http,
            Lease = new RetryAfterLease(TimeSpan.FromMinutes(7)),
        };
        await o.OnRejected!(ctx, CancellationToken.None);

        Assert.Equal("420", http.Response.Headers.RetryAfter.ToString());
    }

    private sealed class RetryAfterLease(TimeSpan retryAfter) : RateLimitLease
    {
        public override bool IsAcquired => false;

        public override IEnumerable<string> MetadataNames => [MetadataName.RetryAfter.Name];

        public override bool TryGetMetadata(string metadataName, out object? metadata)
        {
            if (metadataName == MetadataName.RetryAfter.Name)
            {
                metadata = retryAfter;
                return true;
            }
            metadata = null;
            return false;
        }
    }
}
