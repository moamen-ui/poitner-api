using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.API.Middleware;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-12 §6 test 8 (adapted to R5-58's middleware, which already existed): the resolved request
/// id is exposed on <c>HttpContext.Items[RequestIdMiddleware.ItemKey]</c> so the audit writer can
/// stamp it on every row, and an invalid client-supplied id (empty, whitespace, oversized, too
/// short) is replaced with a fresh one rather than echoed.
/// </summary>
public class RequestIdMiddlewareTests
{
    private static async Task<(string Item, string Header)> ResolveAsync(string? headerValue)
    {
        var ctx = new DefaultHttpContext();
        if (headerValue is not null)
            ctx.Request.Headers[RequestIdMiddleware.HeaderName] = headerValue;

        var middleware = new RequestIdMiddleware(_ => Task.CompletedTask);
        await middleware.InvokeAsync(ctx, NullLogger<RequestIdMiddleware>.Instance);

        var item = Assert.IsType<string>(ctx.Items[RequestIdMiddleware.ItemKey]);
        var echoed = ctx.Response.Headers[RequestIdMiddleware.HeaderName].ToString();
        return (item, echoed);
    }

    [Fact]
    public async Task ValidHeader_IsEchoed_OnItemsAndResponse()
    {
        var (item, header) = await ResolveAsync("test-1234-abcd");

        Assert.Equal("test-1234-abcd", item);
        Assert.Equal("test-1234-abcd", header);
    }

    [Fact]
    public async Task OversizedHeader_IsReplaced()
    {
        var (item, header) = await ResolveAsync(new string('a', 65));

        AssertGenerated(item);
        Assert.Equal(item, header);
    }

    [Fact]
    public async Task WhitespaceInHeader_IsReplaced()
    {
        var (item, header) = await ResolveAsync("a b");

        AssertGenerated(item);
        Assert.Equal(item, header);
    }

    [Fact]
    public async Task NoHeader_IsGenerated()
    {
        var (item, header) = await ResolveAsync(null);

        AssertGenerated(item);
        Assert.Equal(item, header);
    }

    [Fact]
    public async Task TooShortHeader_IsReplaced()
    {
        // R5-58 tightened the minimum to 8 chars; "abc-123" is 7.
        var (item, header) = await ResolveAsync("abc-123");

        AssertGenerated(item);
        Assert.Equal(item, header);
    }

    private static void AssertGenerated(string value) =>
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), value); // Guid N — never the rejected client value
}
