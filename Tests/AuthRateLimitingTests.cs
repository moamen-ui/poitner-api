using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Controllers;
using Pointer.API.Extensions;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// BINDING: the per-IP "signup" limiter exists to throttle account-creation and
/// password-email abuse — NOT login. Login is called by the widget from arbitrary
/// host origins; throttling it locks legitimate users out for the whole window
/// (and everyone behind one NAT shares the budget). These assertions fail loudly
/// if the limiter ever creeps back onto login or falls off the signup surface.
/// Rejections must read as throttling (429 + Retry-After), not an outage (503).
/// </summary>
public class AuthRateLimitingTests
{
    [Fact]
    public void Login_IsNotRateLimited()
    {
        var method = typeof(AuthController).GetMethod("Login");
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true);
        Assert.Empty(rateLimits);
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
