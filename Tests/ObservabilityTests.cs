using System.Security.Claims;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.Extensions.Logging;
using Pointer.API.Extensions;
using Pointer.API.Middleware;
using Pointer.Infrastructure;
using Xunit;

namespace Pointer.Tests;

/// <summary>R5-58 §6 — request-id middleware, /health registration, and Sentry no-DSN no-op.</summary>
public class ObservabilityTests
{
    [Fact]
    public async Task RequestIdMiddleware_EchoesIncomingId()
    {
        var middleware = new RequestIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Request-Id"] = "test-123";

        await middleware.InvokeAsync(context, new RecordingLogger());

        Assert.Equal("test-123", context.Response.Headers["X-Request-Id"].ToString());
    }

    /// <summary>GLM review F6 — a value that doesn't match the allowed charset/length is
    /// replaced with a generated id rather than echoed/logged verbatim.</summary>
    [Theory]
    [InlineData("short")] // below the 8-char floor
    [InlineData("has a space")]
    [InlineData("has/slash")]
    [InlineData("<script>alert(1)</script>")]
    public async Task RequestIdMiddleware_RejectsInvalidIncomingId_GeneratesInstead(string hostile)
    {
        var middleware = new RequestIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Request-Id"] = hostile;

        await middleware.InvokeAsync(context, new RecordingLogger());

        var id = context.Response.Headers["X-Request-Id"].ToString();
        Assert.NotEqual(hostile, id);
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), id);
    }

    /// <summary>A valid, plain client-supplied id (within the charset/length rule) is still
    /// echoed as-is — the validation must not reject legitimate correlation ids.</summary>
    [Theory]
    [InlineData("abcd1234")]
    [InlineData("Req.Id_09-ABC")]
    public async Task RequestIdMiddleware_EchoesValidIncomingId(string valid)
    {
        var middleware = new RequestIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        context.Request.Headers["X-Request-Id"] = valid;

        await middleware.InvokeAsync(context, new RecordingLogger());

        Assert.Equal(valid, context.Response.Headers["X-Request-Id"].ToString());
    }

    [Fact]
    public async Task RequestIdMiddleware_GeneratesIdWhenMissing()
    {
        var middleware = new RequestIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, new RecordingLogger());

        var id = context.Response.Headers["X-Request-Id"].ToString();
        Assert.Equal(32, id.Length);
        Assert.Matches(new Regex("^[0-9a-f]{32}$"), id);
    }

    [Fact]
    public async Task RequestIdMiddleware_AnonymousRequest_LogsNullUserAndTenant()
    {
        var middleware = new RequestIdMiddleware(_ => Task.CompletedTask);
        var context = new DefaultHttpContext();
        var logger = new RecordingLogger();

        await middleware.InvokeAsync(context, logger);

        var scope = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(
            logger.LastScopeState
        );
        var dict = scope.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.NotNull(dict["RequestId"]);
        Assert.Null(dict["UserId"]);
        Assert.Null(dict["TenantId"]);
    }

    /// <summary>
    /// This middleware is registered before UseAuthentication, so HttpContext.User has no claims
    /// yet at BeginScope time. The scope object must therefore read User lazily — at log-write
    /// time, not at BeginScope-call time — so that a request authenticated further down the
    /// pipeline (inside `next()`) still gets the correct UserId/TenantId on its log lines. This
    /// test simulates that ordering: `next` sets claims on HttpContext.User, and only THEN do we
    /// enumerate the scope captured by BeginScope — mirroring how the real JSON formatter
    /// enumerates the scope when it writes a line deep inside the pipeline.
    /// </summary>
    [Fact]
    public async Task RequestIdMiddleware_ScopeReflectsClaimsSetLaterInPipeline()
    {
        var logger = new RecordingLogger();
        RequestDelegate next = ctx =>
        {
            // Simulate UseAuthentication running after this middleware and populating User.
            ctx.User = new ClaimsPrincipal(
                new ClaimsIdentity(
                    new[]
                    {
                        new Claim(ClaimTypes.NameIdentifier, "user-42"),
                        new Claim("tenant", "tenant-7"),
                    },
                    "TestAuth"
                )
            );
            return Task.CompletedTask;
        };
        var middleware = new RequestIdMiddleware(next);
        var context = new DefaultHttpContext();

        await middleware.InvokeAsync(context, logger);

        var scope = Assert.IsAssignableFrom<IEnumerable<KeyValuePair<string, object?>>>(
            logger.LastScopeState
        );
        var dict = scope.ToDictionary(kv => kv.Key, kv => kv.Value);
        Assert.Equal("user-42", dict["UserId"]);
        Assert.Equal("tenant-7", dict["TenantId"]);
    }

    [Fact]
    public async Task AddObservability_RegistersDbHealthCheck()
    {
        var services = new ServiceCollection();
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        services.AddSingleton<Pointer.Application.Abstractions.ICurrentUser>(new FakeCurrentUser());
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(Guid.NewGuid().ToString()));

        services.AddLogging(logging => services.AddObservability(config, logging));

        await using var provider = services.BuildServiceProvider();
        var healthCheckService = provider.GetRequiredService<HealthCheckService>();

        var report = await healthCheckService.CheckHealthAsync();

        Assert.True(report.Entries.ContainsKey("db"));
        Assert.Equal(HealthStatus.Healthy, report.Entries["db"].Status);
        Assert.Contains("ready", report.Entries["db"].Tags);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    public void AddSentryIfConfigured_NoOp_WhenDsnEmpty(string? dsn)
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Testing" }
        );
        builder.Configuration["SENTRY_DSN"] = dsn;

        // Must not throw, and the API boots fine without ever contacting Sentry.
        var result = builder.AddSentryIfConfigured();
        Assert.Same(builder, result);

        using var app = builder.Build();
    }

    [Fact]
    public void AddSentryIfConfigured_Configures_WhenDsnSet()
    {
        var builder = WebApplication.CreateBuilder(
            new WebApplicationOptions { EnvironmentName = "Testing" }
        );
        // A syntactically valid (fake) DSN — never sent to, this only proves the branch that
        // calls UseSentry runs without throwing when a DSN is present.
        builder.Configuration["SENTRY_DSN"] = "https://public@o0.ingest.sentry.io/0";

        var result = builder.AddSentryIfConfigured();
        Assert.Same(builder, result);

        using var app = builder.Build();
    }

    private sealed class FakeCurrentUser : Pointer.Application.Abstractions.ICurrentUser
    {
        public Guid? Id => null;
        public bool IsAdmin => false;
        public bool IsSuperAdmin => false;
        public bool IsQuickAccess => false;
        public Guid? TenantId => null;
        public int? RoleId => null;
        public string? KeyScopes => null;
        public string? Scope => null;
    }

    private sealed class RecordingLogger : ILogger<RequestIdMiddleware>
    {
        public object? LastScopeState { get; private set; }

        public IDisposable BeginScope<TState>(TState state)
            where TState : notnull
        {
            LastScopeState = state;
            return NoopDisposable.Instance;
        }

        public bool IsEnabled(LogLevel logLevel) => false;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter
        ) { }

        private sealed class NoopDisposable : IDisposable
        {
            public static readonly NoopDisposable Instance = new();

            public void Dispose() { }
        }
    }
}
