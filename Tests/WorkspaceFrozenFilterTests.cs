using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pointer.API.Auth;
using Pointer.API.Controllers;
using Pointer.API.Controllers.Admin;
using Pointer.Application.Abstractions;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-18 §3.5/§6 test 7 — the workspace-freeze gate's own contract, mirroring
/// <see cref="RequireVerifiedEmailFilterTests"/>'s shape for building an
/// <see cref="ActionExecutingContext"/> by hand.
/// </summary>
public class WorkspaceFrozenFilterTests
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
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private sealed class RecordingLogger : ILogger<WorkspaceFrozenFilter>
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

    private sealed class FakeWorkspaceState : IWorkspaceStateService
    {
        public WorkspaceFreeze Freeze { get; set; } = new(false, false, false, null);
        public Exception? Throws { get; set; }

        public Task<WorkspaceFreeze> GetAsync(Guid workspaceId) =>
            Throws != null ? throw Throws : Task.FromResult(Freeze);

        public void Invalidate(Guid workspaceId) { }
    }

    private static (ActionExecutingContext Ctx, ActionExecutedContext Executed) Build(
        Type controllerType,
        MethodInfo method,
        string httpMethod,
        FakeCurrentUser current,
        IServiceProvider services,
        bool authenticated = true,
        bool allowAnonymous = false
    )
    {
        var httpContext = new DefaultHttpContext { RequestServices = services };
        httpContext.Request.Method = httpMethod;
        httpContext.User = new System.Security.Claims.ClaimsPrincipal(
            authenticated
                ? new System.Security.Claims.ClaimsIdentity(authenticationType: "Test")
                : new System.Security.Claims.ClaimsIdentity()
        );

        var cad = new ControllerActionDescriptor
        {
            MethodInfo = method,
            ControllerTypeInfo = controllerType.GetTypeInfo(),
            EndpointMetadata = allowAnonymous
                ? new List<object> { new AllowAnonymousAttribute() }
                : new List<object>(),
        };
        var actionContext = new ActionContext(
            httpContext,
            new Microsoft.AspNetCore.Routing.RouteData(),
            cad
        );
        var executingCtx = new ActionExecutingContext(
            actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?>(),
            controller: new object()
        );
        var executedCtx = new ActionExecutedContext(
            actionContext,
            executingCtx.Filters,
            new object()
        )
        {
            Result = new OkObjectResult(Result.Success()),
        };
        return (executingCtx, executedCtx);
    }

    private static ActionExecutionDelegate Next(
        ActionExecutedContext executed,
        Action? onNext = null
    ) =>
        () =>
        {
            onNext?.Invoke();
            return Task.FromResult(executed);
        };

    private static IServiceProvider Services(
        FakeCurrentUser current,
        FakeWorkspaceState? state = null
    )
    {
        var services = new ServiceCollection();
        services.AddSingleton<ICurrentUser>(current);
        services.AddSingleton<IWorkspaceStateService>(state ?? new FakeWorkspaceState());
        return services.BuildServiceProvider();
    }

    // WorkspaceController.Pause has no [AllowWhenWorkspacePaused] — blocked while frozen.
    private static MethodInfo Pause =>
        typeof(WorkspaceController).GetMethod(nameof(WorkspaceController.Pause))!;

    // WorkspaceController.Get is a GET with no attribute — reads pass for a non-key session.
    private static MethodInfo Get =>
        typeof(WorkspaceController).GetMethod(nameof(WorkspaceController.Get))!;

    // WorkspaceController.Resume carries [AllowWhenWorkspacePaused].
    private static MethodInfo Resume =>
        typeof(WorkspaceController).GetMethod(nameof(WorkspaceController.Resume))!;

    // AuthController.Me carries [AllowWhenWorkspacePaused(AllowKeySessions = true)].
    private static MethodInfo Me => typeof(AuthController).GetMethod(nameof(AuthController.Me))!;

    [Fact]
    public async Task Frozen_Post_Returns423_WithHeader()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, true, false, null),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Pause,
            "POST",
            current,
            Services(current, state)
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.False(nextCalled);
        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(423, result.StatusCode);
        Assert.Equal("true", ctx.HttpContext.Response.Headers["X-Workspace-Paused"].ToString());
        Assert.Equal(MessageKeys.Workspace.Paused, ((Result)result.Value!).Message);
    }

    [Fact]
    public async Task OperatorPause_MessageIsPausedByOperator()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var state = new FakeWorkspaceState { Freeze = new WorkspaceFreeze(true, true, true, null) };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Pause,
            "POST",
            current,
            Services(current, state)
        );
        await filter.OnActionExecutionAsync(ctx, Next(executed));

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(423, result.StatusCode);
        Assert.Equal(MessageKeys.Workspace.PausedByOperator, ((Result)result.Value!).Message);
    }

    [Fact]
    public async Task DeletionScheduled_MessageIsDeletionScheduledReadOnly()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, false, false, DateTime.UtcNow.AddDays(7)),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Pause,
            "POST",
            current,
            Services(current, state)
        );
        await filter.OnActionExecutionAsync(ctx, Next(executed));

        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(
            MessageKeys.Workspace.DeletionScheduledReadOnly,
            ((Result)result.Value!).Message
        );
    }

    [Fact]
    public async Task Frozen_Get_Passes()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, true, false, null),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Get,
            "GET",
            current,
            Services(current, state)
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
        Assert.Null(ctx.Result);
    }

    [Fact]
    public async Task Frozen_AllowWhenPaused_Passes()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, true, false, null),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Resume,
            "POST",
            current,
            Services(current, state)
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task Frozen_KeySession_Get_Returns423()
    {
        var current = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            KeyScopes = "read",
        };
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, true, false, null),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        // A GET, but a key session — D18.4: every key-session call is checked, reads included.
        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Get,
            "GET",
            current,
            Services(current, state)
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.False(nextCalled);
        var result = Assert.IsType<ObjectResult>(ctx.Result);
        Assert.Equal(423, result.StatusCode);
    }

    [Fact]
    public async Task Frozen_KeySession_AuthMe_Passes()
    {
        var current = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            KeyScopes = "read",
        };
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, true, false, null),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(AuthController),
            Me,
            "GET",
            current,
            Services(current, state)
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task SuperAdmin_Passes()
    {
        var current = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            IsSuperAdmin = true,
            TenantId = Guid.NewGuid(),
        };
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, true, false, null),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Pause,
            "POST",
            current,
            Services(current, state)
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task NoTenant_Passes()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = null };
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, true, false, null),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Pause,
            "POST",
            current,
            Services(current, state)
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task AllowAnonymous_Passes()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, true, false, null),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Pause,
            "POST",
            current,
            Services(current, state),
            allowAnonymous: true
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }

    [Fact]
    public async Task LookupThrows_FailsOpen()
    {
        var current = new FakeCurrentUser { Id = Guid.NewGuid(), TenantId = Guid.NewGuid() };
        var state = new FakeWorkspaceState { Throws = new InvalidOperationException("boom") };
        var logger = new RecordingLogger();
        var filter = new WorkspaceFrozenFilter(logger);

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Pause,
            "POST",
            current,
            Services(current, state)
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
        Assert.Equal(1, logger.WarningCount);
    }

    [Fact]
    public async Task NotAuthenticated_Passes()
    {
        var current = new FakeCurrentUser();
        var state = new FakeWorkspaceState
        {
            Freeze = new WorkspaceFreeze(true, true, false, null),
        };
        var filter = new WorkspaceFrozenFilter(new RecordingLogger());

        var (ctx, executed) = Build(
            typeof(WorkspaceController),
            Pause,
            "POST",
            current,
            Services(current, state),
            authenticated: false
        );
        var nextCalled = false;
        await filter.OnActionExecutionAsync(ctx, Next(executed, () => nextCalled = true));

        Assert.True(nextCalled);
    }
}
