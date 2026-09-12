using System.Reflection;
using System.Threading.RateLimiting;
using Microsoft.AspNetCore.RateLimiting;
using Pointer.API.Controllers;
using Pointer.API.Extensions;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// BINDING: comment and reply creation are throttled per authenticated user.
///
/// This existed only on paper for a while — the public documentation described a 30-per-minute
/// sliding window while no such policy was configured and neither write action carried the
/// attribute, so the product promised spam protection it did not have. These assertions are what
/// make the claim true, and they fail loudly if either half goes missing again.
///
/// Partitioning is per USER, not per IP, on purpose: an office behind one NAT address is normal,
/// and an IP partition would let one tester throttle their colleagues.
/// </summary>
public class CommentRateLimitingTests
{
    [Fact]
    public void CreateComment_IsRateLimited()
    {
        var method = typeof(CommentsController).GetMethod("Create");
        Assert.NotNull(method);

        var attr = Assert.Single(method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true));
        Assert.Equal("comments", attr.PolicyName);
    }

    [Fact]
    public void AddReply_SharesTheCommentsBudget()
    {
        // A reply is the same write. Throttling comments alone would just move a burst one
        // endpoint to the left.
        var method = typeof(RepliesController).GetMethod("AddReply");
        Assert.NotNull(method);

        var attr = Assert.Single(method!.GetCustomAttributes<EnableRateLimitingAttribute>(inherit: true));
        Assert.Equal("comments", attr.PolicyName);
    }

    [Fact]
    public void CommentsPolicy_IsASlidingWindowOf30PerMinute()
    {
        // Resolve the partition the way the middleware does, for a signed-in caller.
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.User = new System.Security.Claims.ClaimsPrincipal(
            new System.Security.Claims.ClaimsIdentity(
                new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, "user-1") },
                "test"));

        var partition = RateLimitingExtensions.CommentsPartition(ctx);
        var limiter = partition.Factory(partition.PartitionKey);

        var sliding = Assert.IsType<SlidingWindowRateLimiter>(limiter);
        var stats = sliding.GetStatistics();
        Assert.NotNull(stats);
        // 30 permits available on a fresh limiter is the documented budget.
        Assert.Equal(30, stats!.CurrentAvailablePermits);
    }

    /// <summary>
    /// The test that should have existed first. A real Pointer token carries `sub` and NOT
    /// ClaimTypes.NameIdentifier — authentication sets MapInboundClaims = false, so nothing
    /// rewrites it. A partition key that looks only at NameIdentifier therefore finds no user and
    /// falls back to the IP bucket, which silently turns a per-user limit into a per-address one.
    /// The original unit test hand-built its principal with NameIdentifier and so proved nothing
    /// about real traffic; the e2e suite caught it by throttling four different users at once.
    /// </summary>
    [Fact]
    public void CommentsPolicy_RecognisesTheSubClaim_AsRealTokensCarryIt()
    {
        static Microsoft.AspNetCore.Http.HttpContext CtxFor(string userId)
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
            ctx.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim("sub", userId) },
                    "test"));
            return ctx;
        }

        var a = RateLimitingExtensions.PartitionKeyFor(CtxFor("11111111-1111-1111-1111-111111111111"));
        var b = RateLimitingExtensions.PartitionKeyFor(CtxFor("22222222-2222-2222-2222-222222222222"));

        Assert.StartsWith("user:", a);
        Assert.StartsWith("user:", b);
        Assert.NotEqual(a, b);
    }

    [Fact]
    public void CommentsPolicy_FallsBackToIp_ForAnAnonymousCaller()
    {
        var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
        ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("198.51.100.4");

        Assert.Equal("ip:198.51.100.4", RateLimitingExtensions.PartitionKeyFor(ctx));
    }

    [Fact]
    public void CommentsPolicy_PartitionsByUser_NotByIp()
    {
        // Two different users arriving from the SAME address must land in different partitions —
        // otherwise a shared office IP means one person's burst locks out everyone else.
        static Microsoft.AspNetCore.Http.HttpContext CtxFor(string userId)
        {
            var ctx = new Microsoft.AspNetCore.Http.DefaultHttpContext();
            ctx.Connection.RemoteIpAddress = System.Net.IPAddress.Parse("203.0.113.7");
            ctx.User = new System.Security.Claims.ClaimsPrincipal(
                new System.Security.Claims.ClaimsIdentity(
                    new[] { new System.Security.Claims.Claim(System.Security.Claims.ClaimTypes.NameIdentifier, userId) },
                    "test"));
            return ctx;
        }

        var a = RateLimitingExtensions.CommentsPartition(CtxFor("user-a")).PartitionKey;
        var b = RateLimitingExtensions.CommentsPartition(CtxFor("user-b")).PartitionKey;

        Assert.NotEqual(a, b);
    }
}
