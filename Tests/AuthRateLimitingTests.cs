using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Controllers;
using Pointer.API.Extensions;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// BINDING (R5-59 §12): password login limits failed attempts per normalised e-mail address
/// (via ILoginAttemptLimiter), while post-authentication and raw network flood abuse is throttled
/// by a per-IP floor ("login-ip", 60/min). The per-IP "signup" limiter remains separate and
/// covers account creation/password email surfaces. Magic-link redemption ("login-with-invite"
/// and "login-with-key") keeps its own per-IP "login" policy — a 256-bit token is already the
/// credential, so IP partitioning is enough there.
/// Rejections must read as throttling (429 + Retry-After), not an outage (503).
/// </summary>
public class AuthRateLimitingTests
{
    [Fact]
    public void Login_HasIpFloorRateLimit()
    {
        var method = typeof(AuthController).GetMethod("Login");
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true).ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "login-ip");
    }

    /// <summary>
    /// GLM review M1/F2 — a body-size cap on top of the e-mail-length validator: the login body
    /// is two short strings, so 64 KiB is generous headroom while still ruling out the
    /// multi-megabyte bodies the review used to model the cache-memory attack.
    /// </summary>
    [Fact]
    public void Login_HasRequestSizeLimit()
    {
        var method = typeof(AuthController).GetMethod("Login");
        Assert.NotNull(method);

        var limit = method!.GetCustomAttribute<RequestSizeLimitAttribute>(inherit: true);
        Assert.NotNull(limit);
        Assert.Equal(64L * 1024, ((IRequestSizeLimitMetadata)limit!).MaxRequestBodySize);
    }

    [Fact]
    public void LoginWithKey_HasLoginRateLimit()
    {
        var method = typeof(AuthController).GetMethod("LoginWithKey");
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true).ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "login");
    }

    /// <summary>DB-11b / GLM A5: switch-workspace is the second consumer of the "login" policy (the
    /// first is login-with-invite). Login itself must stay untouched.</summary>
    [Fact]
    public void SwitchWorkspace_HasLoginRateLimit()
    {
        var method = typeof(AuthController).GetMethod("SwitchWorkspace");
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true).ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "login");

        var loginRateLimits = typeof(AuthController)
            .GetMethod("Login")!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
        Assert.DoesNotContain(loginRateLimits, a => a.PolicyName == "login");
    }

    [Theory]
    [InlineData("Register")]
    [InlineData("RegisterAdmin")]
    [InlineData("RegisterInvite")]
    [InlineData("ForgotPassword")]
    [InlineData("ResetPassword")]
    [InlineData("ConfirmErase")]
    public void SignupSurface_KeepsSignupRateLimit(string action)
    {
        var method = typeof(AuthController).GetMethod(action);
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true).ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "signup");
    }

    /// <summary>DB-11c GLM A3: request-erase e-mails a link, same 5/h-per-IP budget as forgot-password.</summary>
    [Fact]
    public void RequestErase_HasSignupRateLimit()
    {
        var method = typeof(MeController).GetMethod("RequestErase");
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
