using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Metadata;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
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

        var rateLimits = method!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
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

        var rateLimits = method!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "login");
    }

    /// <summary>DB-11b / GLM A5: switch-workspace is the second consumer of the "login" policy (the
    /// first is login-with-invite). Login itself must stay untouched.</summary>
    [Fact]
    public void SwitchWorkspace_HasLoginRateLimit()
    {
        var method = typeof(AuthController).GetMethod("SwitchWorkspace");
        Assert.NotNull(method);

        var rateLimits = method!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
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
    [InlineData("ConfirmEmailChange")]
    [InlineData("VerifyEmail")]
    public void SignupSurface_KeepsSignupRateLimit(string action)
    {
        var method = typeof(AuthController).GetMethod(action);
        Assert.NotNull(method);

        var rateLimits = method!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "signup");
    }

    /// <summary>DB-11c GLM A3: request-erase e-mails a link, same 5/h-per-IP budget as forgot-password.</summary>
    [Fact]
    public void RequestErase_HasSignupRateLimit()
    {
        var method = typeof(MeController).GetMethod("RequestErase");
        Assert.NotNull(method);

        var rateLimits = method!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "signup");
    }

    /// <summary>DB-11d: change-email sends two e-mails, same 5/h-per-IP budget as forgot-password/request-erase.</summary>
    [Fact]
    public void ChangeEmail_HasSignupRateLimit()
    {
        var method = typeof(MeController).GetMethod("ChangeEmail");
        Assert.NotNull(method);

        var rateLimits = method!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "signup");
    }

    /// <summary>DB-14: resend shares the 5/h-per-IP "signup" budget on top of its own 1-per-5-min
    /// per-identity cache check inside EmailVerificationService.ResendAsync.</summary>
    [Fact]
    public void ResendVerification_HasSignupRateLimit()
    {
        var method = typeof(MeController).GetMethod("ResendVerification");
        Assert.NotNull(method);

        var rateLimits = method!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "signup");
    }

    /// <summary>Review finding #1: DB-19 §3.5 — POST /api/me/workspaces has its own budget
    /// ("workspace-create", D19.8), never shared with "signup"/"danger"/"login". A rename or
    /// misspelling of the policy name would silently disable the limiter without failing any
    /// other test.</summary>
    [Fact]
    public void CreateWorkspace_HasWorkspaceCreateRateLimit()
    {
        var method = typeof(MeController).GetMethod("CreateWorkspace");
        Assert.NotNull(method);

        var rateLimits = method!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
        Assert.Contains(rateLimits, a => a.PolicyName == "workspace-create");
    }

    /// <summary>Review finding #1: the read-only allowance endpoint is unthrottled by design (it
    /// backs the dashboard's workspace switcher and is polled far more often than creation).</summary>
    [Fact]
    public void GetWorkspaceAllowance_HasNoRateLimit()
    {
        var method = typeof(MeController).GetMethod("GetWorkspaceAllowance");
        Assert.NotNull(method);

        var rateLimits = method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true);
        Assert.Empty(rateLimits);
    }

    /// <summary>Review finding #1: the "workspace-create" policy itself — 1-hour fixed window,
    /// 5-permit default (Security:RateLimits:WorkspaceCreatePerHour, appsettings.json), overridable
    /// upward exactly like "signup"/"danger", and partitioned by identity+IP via
    /// RateLimitingExtensions.DangerPartitionKey (not a bare per-IP floor — one identity behind a
    /// shared NAT must not share its budget with everyone else on it).</summary>
    [Fact]
    public void WorkspaceCreatePolicy_DefaultsTo5PerHour_PartitionedByIdentity()
    {
        var o = new RateLimiterOptions();
        RateLimitingExtensions.Configure(o);

        var policy = GetPolicy(o, "workspace-create");
        var ip = System.Net.IPAddress.Parse("10.0.0.2");

        var ctxUserA = new DefaultHttpContext();
        ctxUserA.Connection.RemoteIpAddress = ip;
        ctxUserA.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("sub", "user-a") },
                "Test"
            )
        );

        var partition = GetPartition(policy, ctxUserA);
        Assert.Equal(
            RateLimitingExtensions.DangerPartitionKey(ctxUserA),
            GetPartitionKey(partition)
        );

        var limiter = CreateLimiter(partition);
        for (var i = 0; i < 5; i++)
            Assert.True(limiter.AttemptAcquire(1).IsAcquired);
        Assert.False(limiter.AttemptAcquire(1).IsAcquired);
    }

    /// <summary>Review finding #5 / D19.8: the operator-facing override reaches the limiter (the
    /// documented-but-missing appsettings.json knob was the finding — this proves the knob works).</summary>
    [Fact]
    public void WorkspaceCreatePolicy_HonoursConfiguredOverride()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Security:RateLimits:WorkspaceCreatePerHour"] = "2",
                }
            )
            .Build();

        var o = new RateLimiterOptions();
        RateLimitingExtensions.Configure(o, config);

        var policy = GetPolicy(o, "workspace-create");
        var ctx = new DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("10.0.0.3");

        var limiter = CreateLimiter(GetPartition(policy, ctx));
        Assert.True(limiter.AttemptAcquire(1).IsAcquired);
        Assert.True(limiter.AttemptAcquire(1).IsAcquired);
        Assert.False(limiter.AttemptAcquire(1).IsAcquired);
    }

    // ── Reflection helpers for the two tests above: RateLimiterOptions.PolicyMap and
    // DefaultRateLimiterPolicy.GetPartition are internal, so a registered policy's actual limiter
    // (PermitLimit/Window/partition key) can only be reached through the same public entry point
    // ASP.NET Core's middleware uses — Configure(...) — and a small amount of reflection. This
    // exercises the real, wired-up policy rather than re-deriving expected values from the
    // implementation, which is what the review finding asked for. ──

    private static object GetPolicy(RateLimiterOptions o, string policyName)
    {
        var map = typeof(RateLimiterOptions)
            .GetProperty("PolicyMap", BindingFlags.NonPublic | BindingFlags.Instance)!
            .GetValue(o)!;
        var indexer = map.GetType().GetProperty("Item")!;
        return indexer.GetValue(map, new object[] { policyName })!;
    }

    private static object GetPartition(object policy, HttpContext ctx) =>
        policy.GetType().GetMethod("GetPartition")!.Invoke(policy, new object[] { ctx })!;

    private static string GetPartitionKey(object partition)
    {
        var defaultKey = partition.GetType().GetProperty("PartitionKey")!.GetValue(partition)!;
        return (string)defaultKey.GetType().GetProperty("Key")!.GetValue(defaultKey)!;
    }

    private static RateLimiter CreateLimiter(object partition)
    {
        var factory = partition.GetType().GetProperty("Factory")!.GetValue(partition)!;
        var key = partition.GetType().GetProperty("PartitionKey")!.GetValue(partition)!;
        return (RateLimiter)factory.GetType().GetMethod("Invoke")!.Invoke(factory, new[] { key })!;
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

        var rateLimits = method!
            .GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true)
            .ToList();
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

    /// <summary>DB-18 R16 amendment: "danger" (workspace pause/delete confirm/pause-instead)
    /// partitions by identity (sub claim) + IP when authenticated, so one identity behind a shared
    /// NAT does not share its 10/10min budget with everyone else on it — and never with a different
    /// identity on the SAME connection either. Falls back to IP alone when anonymous (the token-
    /// redemption endpoints have no identity yet).</summary>
    [Fact]
    public void DangerLimiter_PartitionsByIdentity()
    {
        var ip = System.Net.IPAddress.Parse("10.0.0.1");

        var ctxUserA = new DefaultHttpContext();
        ctxUserA.Connection.RemoteIpAddress = ip;
        ctxUserA.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("sub", "user-a") },
                "Test"
            )
        );

        var ctxUserB = new DefaultHttpContext();
        ctxUserB.Connection.RemoteIpAddress = ip; // same IP/NAT as user A
        ctxUserB.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim("sub", "user-b") },
                "Test"
            )
        );

        var keyA = RateLimitingExtensions.DangerPartitionKey(ctxUserA);
        var keyB = RateLimitingExtensions.DangerPartitionKey(ctxUserB);

        // Two identities sharing an IP get independent budgets.
        Assert.NotEqual(keyA, keyB);

        // The SAME identity always resolves to the SAME key (its own budget persists across calls).
        Assert.Equal(keyA, RateLimitingExtensions.DangerPartitionKey(ctxUserA));

        // Anonymous (no sub claim at all) falls back to IP alone.
        var anon = new DefaultHttpContext();
        anon.Connection.RemoteIpAddress = ip;
        Assert.Equal("ip:10.0.0.1", RateLimitingExtensions.DangerPartitionKey(anon));
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
