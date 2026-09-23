using System.Reflection;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pointer.API.Auth;
using Pointer.API.Controllers.Admin;
using Pointer.Application.Abstractions;
using Pointer.Application.Response;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-14 §6 test 5 — the admin-write gate's own contract, mirroring
/// <see cref="AuditCoverageFilterTests"/>'s shape for building an <c>ActionExecutingContext</c> by
/// hand. Namespace rule: <see cref="ProjectsController"/> (Pointer.API.Controllers.Admin) is the
/// gated controller; <see cref="Pointer.API.Controllers.MeController"/> (Pointer.API.Controllers) is
/// the non-admin control.
/// </summary>
public class RequireVerifiedEmailFilterTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
    }

    private sealed class RecordingLogger : ILogger<RequireVerifiedEmailFilter>
    {
        public int WarningCount { get; private set; }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull => NoopDisposable.Instance;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        )
        {
            if (logLevel == LogLevel.Warning)
                WarningCount++;
        }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();

            public void Dispose() { }
        }
    }

    [AllowUnverified]
    private static void AllowUnverifiedAction() { }

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w => w.Ignore(Microsoft.EntityFrameworkCore.Diagnostics.InMemoryEventId.TransactionIgnoredWarning))
                .Options,
            u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static (ActionExecutingContext Ctx, ActionExecutedContext Executed) Build(
        Type controllerType,
        MethodInfo method,
        string httpMethod,
        FakeCurrentUser current,
        IServiceProvider services,
        bool authenticated = true
    )
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = httpMethod;
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(
            authenticated
                ? new System.Security.Claims.ClaimsIdentity(authenticationType: "Test")
                : new System.Security.Claims.ClaimsIdentity()
        );

        var cad = new ControllerActionDescriptor { MethodInfo = method, ControllerTypeInfo = controllerType.GetTypeInfo() };
        var actionContext = new ActionContext(httpContext, new Microsoft.AspNetCore.Routing.RouteData(), cad);
        var executingCtx = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object()
        );
        var executedCtx = new ActionExecutedContext(actionContext, executingCtx.Filters, new object())
        {
            Result = new OkObjectResult(Result.Success()),
        };
        return (executingCtx, executedCtx);
    }

    private static ActionExecutionDelegate Next(ActionExecutedContext executed, Action? onNext = null) =>
        () =>
        {
            onNext?.Invoke();
            return Task.FromResult(executed);
        };

    private static ServiceCollection ServicesWithCurrentUserOnly(FakeCurrentUser current)
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(current);
        return services;
    }

    private static (IServiceProvider Services, IMemoryCache Cache) ServicesWithDb(
        FakeCurrentUser current,
        AppDbContext db
    )
    {
        var cache = new MemoryCache(new MemoryCacheOptions());
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(current);
        services.AddSingleton(db);
        return (services.BuildServiceProvider(), cache);
    }

    private static User SeedUser(AppDbContext db, Guid publicId, DateTime? verifiedAt, bool isDemo = false)
    {
        var role = new Role { Name = "Engineer", GrantsAdmin = false, IsSystem = false, IsActive = true };
        db.Roles.Add(role);
        db.SaveChanges();
        var user = new User
        {
            PublicId = publicId,
            Email = $"{publicId:N}@t.com",
            PasswordHash = "h",
            DisplayName = "U",
            RoleId = role.Id,
            IsActive = true,
            IsDemo = isDemo,
            EmailVerifiedAt = verifiedAt,
        };
        db.Users.Add(user);
        db.SaveChanges();
        return user;
    }

    private static MethodInfo AdminCreate => typeof(ProjectsController).GetMethod(nameof(ProjectsController.Create))!;
    private static MethodInfo AdminList => typeof(ProjectsController).GetMethod(nameof(ProjectsController.List))!;
    private static MethodInfo MeChangePassword =>
        typeof(Pointer.API.Controllers.MeController).GetMethod(nameof(Pointer.API.Controllers.MeController.ChangePassword))!;

    [Fact]
    public async Task Unverified_Returns403WithHeader()
    {
        var publicId = Guid.NewGuid();
        var current = new FakeCurrentUser { Id = publicId };
        using var db = Ctx(current, Guid.NewGuid().ToString());
        SeedUser(db, publicId, verifiedAt: null);
        var (services, cache) = ServicesWithDb(current, db);
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());

        var (ctx, executed) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.False(nextCalled);
        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(StatusCodes.Status403Forbidden, result.StatusCode);
        Assert.Equal("true", ctx.HttpContext.Response.Headers["X-Email-Verification-Required"].ToString());
    }

    [Fact]
    public async Task Verified_CallsNext()
    {
        var publicId = Guid.NewGuid();
        var current = new FakeCurrentUser { Id = publicId };
        using var db = Ctx(current, Guid.NewGuid().ToString());
        SeedUser(db, publicId, verifiedAt: DateTime.UtcNow);
        var (services, cache) = ServicesWithDb(current, db);
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());

        var (ctx, executed) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
        Assert.Null(ctx.Result);
    }

    [Fact]
    public async Task Demo_CallsNext()
    {
        var publicId = Guid.NewGuid();
        var current = new FakeCurrentUser { Id = publicId };
        using var db = Ctx(current, Guid.NewGuid().ToString());
        SeedUser(db, publicId, verifiedAt: null, isDemo: true);
        var (services, cache) = ServicesWithDb(current, db);
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());

        var (ctx, executed) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task SuperAdmin_CallsNext_WithoutLookup()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        // No AppDbContext registered — proves the super-admin branch never asks RequestServices for
        // one (GetRequiredService<AppDbContext> would throw NotSupportedException otherwise, and
        // there is nothing to catch it before that point in the code).
        var services = ServicesWithCurrentUserOnly(current).BuildServiceProvider();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());

        var (ctx, executed) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task UnparsableSub_CallsNext_LogsWarning_NoLookup()
    {
        var current = new FakeCurrentUser { Id = null };
        var services = ServicesWithCurrentUserOnly(current).BuildServiceProvider();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new RecordingLogger();
        var filter = new RequireVerifiedEmailFilter(cache, logger);

        var (ctx, executed) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
        Assert.Equal(1, logger.WarningCount);
    }

    [Fact]
    public async Task MissingUserRow_CallsNext()
    {
        var publicId = Guid.NewGuid();
        var current = new FakeCurrentUser { Id = publicId };
        using var db = Ctx(current, Guid.NewGuid().ToString()); // no row seeded
        var (services, cache) = ServicesWithDb(current, db);
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());

        var (ctx, executed) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Get_CallsNext_WithoutLookup()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid() };
        var services = ServicesWithCurrentUserOnly(current).BuildServiceProvider(); // no AppDbContext
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());

        var (ctx, executed) = Build(typeof(ProjectsController), AdminList, "GET", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task NonAdminController_CallsNext_WithoutLookup()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid() };
        var services = ServicesWithCurrentUserOnly(current).BuildServiceProvider(); // no AppDbContext
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());

        var (ctx, executed) = Build(typeof(Pointer.API.Controllers.MeController), MeChangePassword, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task AllowUnverified_CallsNext_WithoutLookup()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid() };
        var services = ServicesWithCurrentUserOnly(current).BuildServiceProvider(); // no AppDbContext
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());

        var method = typeof(RequireVerifiedEmailFilterTests).GetMethod(
            nameof(AllowUnverifiedAction),
            BindingFlags.NonPublic | BindingFlags.Static
        )!;
        var (ctx, executed) = Build(typeof(ProjectsController), method, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Unauthenticated_CallsNext_WithoutLookup()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid() };
        var services = ServicesWithCurrentUserOnly(current).BuildServiceProvider(); // no AppDbContext
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());

        var (ctx, executed) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services, authenticated: false);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Cache_TwoCalls_SharesEntry_AndClearsAfterConfirm()
    {
        var publicId = Guid.NewGuid();
        var current = new FakeCurrentUser { Id = publicId };
        using var db = Ctx(current, Guid.NewGuid().ToString());
        SeedUser(db, publicId, verifiedAt: null);
        var (services, _) = ServicesWithDb(current, db);
        var cache = new MemoryCache(new MemoryCacheOptions());
        var filter = new RequireVerifiedEmailFilter(cache, new RecordingLogger());
        var key = $"emailverified:{publicId}";

        Assert.False(cache.TryGetValue(key, out _));

        var (ctx1, executed1) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        await filter.OnActionExecutionAsync(ctx1, Next(executed1));
        Assert.True(cache.TryGetValue(key, out var cached1));
        Assert.Equal(false, (bool)cached1!); // unverified

        // Second call within TTL: still gated (cache holds the pre-verification value) — the cache
        // key existing after one call and being reused is the documented alternative to a query
        // counter (§6 test 5).
        var (ctx2, executed2) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        await filter.OnActionExecutionAsync(ctx2, Next(executed2));
        Assert.NotNull(ctx2.Result);

        // Simulate EmailVerificationService.ConfirmAsync: the row becomes verified and the gate's
        // cache key is cleared — the very next call re-queries and passes.
        var toVerify = db.Users.IgnoreQueryFilters().Single(u => u.PublicId == publicId);
        toVerify.EmailVerifiedAt = DateTime.UtcNow;
        db.SaveChanges();
        cache.Remove(key);

        var (ctx3, executed3) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx3, Next(executed3, () => nextCalled = true));
        Assert.True(nextCalled);
    }

    [Fact]
    public async Task LookupThrows_FailsOpen()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid() };
        // AppDbContext missing → GetRequiredService throws inside the cache factory → caught, fail-open.
        var services = ServicesWithCurrentUserOnly(current).BuildServiceProvider();
        var cache = new MemoryCache(new MemoryCacheOptions());
        var logger = new RecordingLogger();
        var filter = new RequireVerifiedEmailFilter(cache, logger);

        var (ctx, executed) = Build(typeof(ProjectsController), AdminCreate, "POST", current, services);
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
        Assert.Equal(1, logger.WarningCount);
    }
}
