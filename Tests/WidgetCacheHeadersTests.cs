using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.StaticFiles;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.API.Extensions;
using Pointer.Application.Common;
using Xunit;

namespace Pointer.Tests;

public class WidgetCacheHeadersTests : IDisposable
{
    private readonly string _tempDir;
    private readonly string _wwwrootDir;
    private readonly string _currentHash = "99183d8f9f37";
    private readonly string _olderHash = "5d0e12345678";
    private readonly byte[] _currentBytes = "console.log('current build');"u8.ToArray();
    private readonly byte[] _olderBytes = "console.log('older build');"u8.ToArray();
    private readonly byte[] _cssBytes = "/* css */"u8.ToArray();
    private readonly WidgetVersionInfo _widgetInfo;

    public WidgetCacheHeadersTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "widget_cache_tests_" + Guid.NewGuid().ToString("N"));
        _wwwrootDir = Path.Combine(_tempDir, "wwwroot");
        Directory.CreateDirectory(_wwwrootDir);

        // Bare files
        File.WriteAllBytes(Path.Combine(_wwwrootDir, "pointer.js"), _currentBytes);
        File.WriteAllBytes(Path.Combine(_wwwrootDir, "pointer.css"), _cssBytes);

        // Current retained dir
        var currentDir = Path.Combine(_wwwrootDir, "widget", _currentHash);
        Directory.CreateDirectory(currentDir);
        File.WriteAllBytes(Path.Combine(currentDir, "pointer.js"), _currentBytes);
        File.WriteAllBytes(Path.Combine(currentDir, "pointer.css"), _cssBytes);

        // Older retained dir
        var olderDir = Path.Combine(_wwwrootDir, "widget", _olderHash);
        Directory.CreateDirectory(olderDir);
        File.WriteAllBytes(Path.Combine(olderDir, "pointer.js"), _olderBytes);
        File.WriteAllBytes(Path.Combine(olderDir, "pointer.css"), _cssBytes);

        // pointer.version.json
        var versionJson = $$"""
        {
          "version": "0.1.0",
          "hash": "{{_currentHash}}",
          "commit": "abc1234",
          "committedAt": "2026-09-12T00:00:00Z",
          "files": {
            "pointer.js": { "bytes": {{_currentBytes.Length}}, "gzipBytes": 50, "integrity": "sha384-current" },
            "pointer.css": { "bytes": {{_cssBytes.Length}}, "gzipBytes": 20, "integrity": "sha384-css" }
          },
          "retained": [
            { "hash": "{{_currentHash}}", "version": "0.1.0", "files": {} },
            { "hash": "{{_olderHash}}", "version": "0.0.9", "files": {} }
          ],
          "budget": { "pointerJsGzipMax": 61440 }
        }
        """;
        File.WriteAllText(Path.Combine(_wwwrootDir, "pointer.version.json"), versionJson);

        _widgetInfo = WidgetVersionInfo.Load(_tempDir, NullLogger.Instance);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
                Directory.Delete(_tempDir, recursive: true);
        }
        catch
        {
            // Ignore cleanup errors
        }
    }

    private async Task<(int statusCode, string? cacheControl, string? mismatchHeader, byte[]? body)> ExecuteAsync(
        string pathAndQuery,
        WidgetVersionInfo? info = null)
    {
        var widgetInfo = info ?? _widgetInfo;
        var ctx = new DefaultHttpContext();
        var uri = new Uri("http://localhost" + pathAndQuery);
        ctx.Request.Path = uri.AbsolutePath;
        ctx.Request.QueryString = new QueryString(uri.Query);

        bool nextCalled = false;
        await WidgetStaticPipeline.HandleWidgetVersioningAsync(ctx, () =>
        {
            nextCalled = true;
            return Task.CompletedTask;
        }, widgetInfo);

        if (!nextCalled)
        {
            // Short-circuited with 404
            return (
                ctx.Response.StatusCode,
                ctx.Response.Headers.CacheControl.ToString(),
                ctx.Response.Headers["X-Pointer-Widget-Version-Mismatch"].ToString(),
                null
            );
        }

        // Simulate static file response
        var relPath = ctx.Request.Path.Value!.TrimStart('/');
        var filePath = Path.Combine(_wwwrootDir, relPath);
        if (!File.Exists(filePath))
        {
            ctx.Response.StatusCode = 404;
            return (404, null, null, null);
        }

        var fileInfo = new PhysicalFileInfo(new FileInfo(filePath));
        var staticCtx = new StaticFileResponseContext(ctx, fileInfo);
        WidgetStaticPipeline.PrepareStaticResponse(staticCtx);

        var body = await File.ReadAllBytesAsync(filePath);
        return (200, ctx.Response.Headers.CacheControl.ToString(), null, body);
    }

    [Fact]
    public async Task Bare_PointerJs_Serves_NoCache()
    {
        var (status, cacheControl, _, body) = await ExecuteAsync("/pointer.js");

        Assert.Equal(200, status);
        Assert.Equal("no-cache", cacheControl);
        Assert.Equal(_currentBytes, body);
    }

    [Fact]
    public async Task Bare_PointerVersionJson_Serves_NoCache()
    {
        var (status, cacheControl, _, body) = await ExecuteAsync("/pointer.version.json");

        Assert.Equal(200, status);
        Assert.Equal("no-cache", cacheControl);
        Assert.NotNull(body);
        Assert.Contains(_currentHash, System.Text.Encoding.UTF8.GetString(body!));
    }

    [Fact]
    public async Task Current_Pinned_Version_Serves_Immutable_And_Matches_Current_Bytes()
    {
        var (status, cacheControl, _, body) = await ExecuteAsync($"/pointer.js?v={_currentHash}");

        Assert.Equal(200, status);
        Assert.Equal("public, max-age=31536000, immutable", cacheControl);
        Assert.Equal(_currentBytes, body);
    }

    [Fact]
    public async Task Older_Retained_Version_Serves_Immutable_And_Older_Bytes()
    {
        var (status, cacheControl, _, body) = await ExecuteAsync($"/pointer.js?v={_olderHash}");

        Assert.Equal(200, status);
        Assert.Equal("public, max-age=31536000, immutable", cacheControl);
        Assert.Equal(_olderBytes, body);
    }

    [Fact]
    public async Task Wrong_Version_Returns_404_With_Mismatch_Header()
    {
        var (status, _, mismatch, body) = await ExecuteAsync("/pointer.js?v=000000000000");

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal(_currentHash, mismatch);
        Assert.Null(body);
    }

    [Fact]
    public async Task Stable_Channel_Returns_MaxAge_3600()
    {
        var (status, cacheControl, _, body) = await ExecuteAsync("/pointer.js?v=stable");

        Assert.Equal(200, status);
        Assert.Equal("public, max-age=3600", cacheControl);
        Assert.Equal(_currentBytes, body);
    }

    [Theory]
    [InlineData("../")]
    [InlineData("../../../etc/passwd")]
    [InlineData("AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public async Task Malformed_V_Returns_404_Without_Exception(string malformedV)
    {
        var (status, _, mismatch, body) = await ExecuteAsync($"/pointer.js?v={Uri.EscapeDataString(malformedV)}");

        Assert.Equal(StatusCodes.Status404NotFound, status);
        Assert.Equal(_currentHash, mismatch);
        Assert.Null(body);
    }

    [Fact]
    public async Task Corrupt_Version_File_Serves_Bare_Files_And_404s_All_V()
    {
        var emptyInfo = WidgetVersionInfo.Empty;

        // Bare file still works
        var (bareStatus, bareCc, _, _) = await ExecuteAsync("/pointer.js", emptyInfo);
        Assert.Equal(200, bareStatus);
        Assert.Equal("no-cache", bareCc);

        // ?v= returns 404 with mismatch header 'unknown'
        var (pinnedStatus, _, mismatch, _) = await ExecuteAsync($"/pointer.js?v={_currentHash}", emptyInfo);
        Assert.Equal(StatusCodes.Status404NotFound, pinnedStatus);
        Assert.Equal("unknown", mismatch);

        // ?v=stable also 404s when version is corrupt/missing
        var (stableStatus, _, stableMismatch, _) = await ExecuteAsync("/pointer.js?v=stable", emptyInfo);
        Assert.Equal(StatusCodes.Status404NotFound, stableStatus);
        Assert.Equal("unknown", stableMismatch);
    }
}
