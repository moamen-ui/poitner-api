using System.Web;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using Pointer.API.Controllers;
using Pointer.API.Extensions;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Upload;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Pointer.Infrastructure.Storage;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Regression coverage for the "widget/dashboard screenshot shows alt text" production bug:
/// <see cref="UploadSigner.SignedUrl(string)"/> returns a RELATIVE URL, so an &lt;img src&gt; built
/// from it resolves against the WIDGET's/DASHBOARD's own origin, not the API's. Every
/// client-facing producer must now hand back an ABSOLUTE URL, via <see cref="IPublicBaseUrl"/>
/// (API layer, since Application/Infrastructure can't touch HttpContext) — see
/// <see cref="HttpContextPublicBaseUrl"/> and <see cref="PublicBaseUrlExtensions.Absolutize"/>.
/// </summary>
public class ScreenshotAbsoluteUrlTests
{
    private sealed class FakeHttpContextAccessor : IHttpContextAccessor
    {
        public HttpContext? HttpContext { get; set; }
    }

    private sealed class FakePublicBaseUrl(string? baseUrl) : IPublicBaseUrl
    {
        public string? Get() => baseUrl;
    }

    /// <summary>
    /// IsSuperAdmin defaults to true (ScreenshotPurgeTests.FakeCurrentUser precedent) so the
    /// AppDbContext this backs bypasses tenant query filters by default — these tests are about URL
    /// absolutization, not tenant isolation.
    /// </summary>
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true;
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private sealed class FakeFileStorage(string relativePathToReturn) : IFileStorage
    {
        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult(relativePathToReturn);

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
    }

    private static UploadSigner RealSigner() =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789",
                    }
                )
                .Build()
        );

    private static AppDbContext BuildContext(string dbName, ICurrentUser? user = null) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user ?? new FakeCurrentUser(),
            new ConfigurationBuilder().Build()
        );

    private static IFormFile MakePngFormFile()
    {
        // Minimal valid PNG magic-byte header (0x89 'P' 'N' 'G' ...) — ValidateImageMagicBytes only
        // inspects the first 4 bytes for image/png, so the rest is filler.
        var bytes = new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 0, 0, 0, 0 };
        return new FormFile(new MemoryStream(bytes), 0, bytes.Length, "file", "shot.png")
        {
            Headers = new HeaderDictionary(),
            ContentType = "image/png",
        };
    }

    // ───────────────────────── IPublicBaseUrl / HttpContextPublicBaseUrl ─────────────────────────

    [Fact]
    public void HttpContextPublicBaseUrl_UsesRequestSchemeAndHost_WhenPublicUrlNotConfigured()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("widget-host.example.com");
        var accessor = new FakeHttpContextAccessor { HttpContext = httpContext };
        var config = new ConfigurationBuilder().Build();

        var resolver = new HttpContextPublicBaseUrl(accessor, config);

        Assert.Equal("https://widget-host.example.com", resolver.Get());
    }

    [Fact]
    public void HttpContextPublicBaseUrl_PrefersConfiguredPublicUrl_OverRequestHost()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("widget-host.example.com");
        var accessor = new FakeHttpContextAccessor { HttpContext = httpContext };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Pointer:PublicUrl"] = "https://api.pointer.moamen.work/",
                }
            )
            .Build();

        var resolver = new HttpContextPublicBaseUrl(accessor, config);

        // Configured origin wins, trailing slash trimmed, regardless of the request's own host.
        Assert.Equal("https://api.pointer.moamen.work", resolver.Get());
    }

    [Fact]
    public void HttpContextPublicBaseUrl_ReturnsNull_WhenNoHttpContext()
    {
        var accessor = new FakeHttpContextAccessor { HttpContext = null };
        var config = new ConfigurationBuilder().Build();

        var resolver = new HttpContextPublicBaseUrl(accessor, config);

        Assert.Null(resolver.Get());
    }

    [Fact]
    public void Absolutize_PrependsBaseUrl_WhenResolved()
    {
        IPublicBaseUrl pub = new FakePublicBaseUrl("https://api.example.com");

        var result = pub.Absolutize("/api/uploads/file?p=uploads%2Fx.png&exp=1&sig=abc");

        Assert.Equal(
            "https://api.example.com/api/uploads/file?p=uploads%2Fx.png&exp=1&sig=abc",
            result
        );
    }

    [Fact]
    public void Absolutize_ReturnsRelativeUrlUnchanged_WhenGetReturnsNull_NoThrow()
    {
        IPublicBaseUrl pub = new FakePublicBaseUrl(null);
        const string relative = "/api/uploads/file?p=uploads%2Fx.png&exp=1&sig=abc";

        var result = pub.Absolutize(relative);

        Assert.Equal(relative, result);
    }

    [Fact]
    public void Absolutize_ReturnsRelativeUrlUnchanged_WhenPublicBaseUrlItselfIsNull_NoThrow()
    {
        // Simulates a hosted-job / no-DI-registration caller: IPublicBaseUrl? is null outright.
        IPublicBaseUrl? pub = null;
        const string relative = "/api/uploads/file?p=uploads%2Fx.png&exp=1&sig=abc";

        var result = pub.Absolutize(relative);

        Assert.Equal(relative, result);
    }

    // ───────────────────────────────── UploadsController.Upload ─────────────────────────────────

    [Fact]
    public async Task Upload_ReturnsAbsoluteUrl_UsingRequestHost_WhenPublicUrlUnset()
    {
        using var db = BuildContext(Guid.NewGuid().ToString());
        db.Projects.Add(
            new Project
            {
                Key = "proj",
                Name = "Proj",
                OwnerId = Guid.NewGuid(),
            }
        );
        await db.SaveChangesAsync();

        var uow = new UnitOfWork(db);
        var signer = RealSigner();
        var relSaved = "uploads/global/proj/abc123.png";

        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "https";
        httpContext.Request.Host = new HostString("widget-host.example.com");
        var accessor = new FakeHttpContextAccessor { HttpContext = httpContext };
        var publicBaseUrl = new HttpContextPublicBaseUrl(
            accessor,
            new ConfigurationBuilder().Build()
        );

        var controller = new UploadsController(
            new FakeFileStorage(relSaved),
            uow,
            signer,
            publicBaseUrl,
            Substitute.For<IWebHostEnvironment>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var response = await controller.Upload(MakePngFormFile(), "proj") as OkObjectResult;

        Assert.NotNull(response);
        var result = Assert.IsType<Result<UploadResponse>>(response!.Value);
        Assert.True(result.IsSuccess);
        Assert.StartsWith("https://widget-host.example.com/api/uploads/file?p=", result.Data!.Url);
    }

    [Fact]
    public async Task Upload_ReturnsAbsoluteUrl_UsingConfiguredPublicUrl_WhenSet()
    {
        using var db = BuildContext(Guid.NewGuid().ToString());
        db.Projects.Add(
            new Project
            {
                Key = "proj",
                Name = "Proj",
                OwnerId = Guid.NewGuid(),
            }
        );
        await db.SaveChangesAsync();

        var uow = new UnitOfWork(db);
        var signer = RealSigner();
        var relSaved = "uploads/global/proj/abc123.png";

        // Request host deliberately different from Pointer:PublicUrl, to prove the configured
        // origin wins (e.g. the API sits behind a vanity domain different from the pod's own host).
        var httpContext = new DefaultHttpContext();
        httpContext.Request.Scheme = "http";
        httpContext.Request.Host = new HostString("internal-pod-host:8090");
        var accessor = new FakeHttpContextAccessor { HttpContext = httpContext };
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["Pointer:PublicUrl"] = "https://api.pointer.moamen.work",
                }
            )
            .Build();
        var publicBaseUrl = new HttpContextPublicBaseUrl(accessor, config);

        var controller = new UploadsController(
            new FakeFileStorage(relSaved),
            uow,
            signer,
            publicBaseUrl,
            Substitute.For<IWebHostEnvironment>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = httpContext },
        };

        var response = await controller.Upload(MakePngFormFile(), "proj") as OkObjectResult;

        Assert.NotNull(response);
        var result = Assert.IsType<Result<UploadResponse>>(response!.Value);
        Assert.True(result.IsSuccess);
        Assert.StartsWith("https://api.pointer.moamen.work/api/uploads/file?p=", result.Data!.Url);

        // (c) The absolute URL still validates via GetFile — the signature is over "relPath|exp"
        // only, unaffected by the origin prefix.
        var query = HttpUtility.ParseQueryString(new Uri(result.Data.Url).Query);
        var p = query["p"]!;
        var exp = long.Parse(query["exp"]!);
        var sig = query["sig"]!;
        Assert.True(signer.Validate(p, exp, sig));
    }

    // ───────────────────────────── CommentService reads (MapElementToDto) ─────────────────────────

    private static async Task<int> SeedCommentAsync(
        AppDbContext db,
        int projectId,
        Guid ownerId,
        string? screenshotUrl,
        Guid authorId
    )
    {
        var comment = new Comment
        {
            ProjectId = projectId,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = authorId,
            Body = "b",
            OwnerId = ownerId,
            Element = new ElementCapture { ScreenshotUrl = screenshotUrl },
        };
        db.Comments.Add(comment);
        await db.SaveChangesAsync();
        return comment.Id;
    }

    private static CommentService BuildCommentService(
        AppDbContext db,
        FakeCurrentUser user,
        IUploadSigner signer,
        IPublicBaseUrl? publicBaseUrl
    ) =>
        new(
            new UnitOfWork(db),
            Substitute.For<IProjectService>(),
            Substitute.For<IPredefinedActionService>(),
            Substitute.For<IFileStorage>(),
            user,
            signer,
            Substitute.For<ISettingsService>(),
            new PassThroughEntitlements(),
            publicBaseUrl: publicBaseUrl
        );

    [Fact]
    public async Task GetById_ReturnsAbsoluteScreenshotUrl_ForStoredRelativeValue()
    {
        var ownerId = Guid.NewGuid();
        var author = Guid.NewGuid();
        // The db context's own ICurrentUser (baked in at construction, drives the tenant query
        // filters on Project/Comment) must be THIS same instance, scoped to the comment's owner —
        // it is independent of whatever ICurrentUser CommentService itself is built with.
        var user = new FakeCurrentUser { Id = author, TenantId = ownerId };
        using var db = BuildContext(Guid.NewGuid().ToString(), user);
        db.Projects.Add(
            new Project
            {
                Key = "proj",
                Name = "Proj",
                OwnerId = ownerId,
            }
        );
        await db.SaveChangesAsync();
        var project = await db.Projects.FirstAsync(p => p.Key == "proj");

        var signer = RealSigner();
        var storedRelative = "uploads/" + ownerId.ToString("N") + "/proj/shot.png";
        var commentId = await SeedCommentAsync(db, project.Id, ownerId, storedRelative, author);

        var publicBaseUrl = new FakePublicBaseUrl("https://api.pointer.moamen.work");
        var service = BuildCommentService(db, user, signer, publicBaseUrl);

        var result = await service.GetByIdAsync(commentId, author);

        Assert.True(result.IsSuccess, result.Message);
        var url = result.Data!.Element.ScreenshotUrl!;
        Assert.StartsWith("https://api.pointer.moamen.work/api/uploads/file?p=", url);
        Assert.True(signer.Validate(signer.ExtractRelPath(url), ParseExp(url), ParseSig(url)));
    }

    [Fact]
    public async Task GetById_ReturnsAbsoluteScreenshotUrl_ForStoredAbsoluteValue()
    {
        var ownerId = Guid.NewGuid();
        var author = Guid.NewGuid();
        var user = new FakeCurrentUser { Id = author, TenantId = ownerId };
        using var db = BuildContext(Guid.NewGuid().ToString(), user);
        db.Projects.Add(
            new Project
            {
                Key = "proj",
                Name = "Proj",
                OwnerId = ownerId,
            }
        );
        await db.SaveChangesAsync();
        var project = await db.Projects.FirstAsync(p => p.Key == "proj");

        var signer = RealSigner();
        // A legacy row that already stored a fully-qualified (but stale-host) URL.
        var storedAbsolute =
            "https://old-host.example.com/uploads/" + ownerId.ToString("N") + "/proj/shot.png";
        var commentId = await SeedCommentAsync(db, project.Id, ownerId, storedAbsolute, author);

        var publicBaseUrl = new FakePublicBaseUrl("https://api.pointer.moamen.work");
        var service = BuildCommentService(db, user, signer, publicBaseUrl);

        var result = await service.GetByIdAsync(commentId, author);

        Assert.True(result.IsSuccess, result.Message);
        var url = result.Data!.Element.ScreenshotUrl!;
        // Re-signed against the CURRENT public origin, not the stale one the row happened to store.
        Assert.StartsWith("https://api.pointer.moamen.work/api/uploads/file?p=", url);
        Assert.DoesNotContain("old-host.example.com", url);
    }

    [Fact]
    public async Task GetById_ReturnsRelativeScreenshotUrl_WhenNoPublicBaseUrlWired_NoThrow()
    {
        // (d) No HttpContext / no IPublicBaseUrl registered (e.g. a hosted-job-style caller) — the
        // relative signed URL is returned unchanged rather than throwing.
        var ownerId = Guid.NewGuid();
        var author = Guid.NewGuid();
        var user = new FakeCurrentUser { Id = author, TenantId = ownerId };
        using var db = BuildContext(Guid.NewGuid().ToString(), user);
        db.Projects.Add(
            new Project
            {
                Key = "proj",
                Name = "Proj",
                OwnerId = ownerId,
            }
        );
        await db.SaveChangesAsync();
        var project = await db.Projects.FirstAsync(p => p.Key == "proj");

        var signer = RealSigner();
        var storedRelative = "uploads/" + ownerId.ToString("N") + "/proj/shot.png";
        var commentId = await SeedCommentAsync(db, project.Id, ownerId, storedRelative, author);

        var service = BuildCommentService(db, user, signer, publicBaseUrl: null);

        var result = await service.GetByIdAsync(commentId, author);

        Assert.True(result.IsSuccess, result.Message);
        var url = result.Data!.Element.ScreenshotUrl!;
        Assert.StartsWith("/api/uploads/file?p=", url);
        Assert.DoesNotContain("://", url);
    }

    private static long ParseExp(string absoluteUrl) =>
        long.Parse(HttpUtility.ParseQueryString(new Uri(absoluteUrl).Query)["exp"]!);

    private static string ParseSig(string absoluteUrl) =>
        HttpUtility.ParseQueryString(new Uri(absoluteUrl).Query)["sig"]!;
}
