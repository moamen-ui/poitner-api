using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Controllers;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.API.Auth;
using Pointer.Application.Response;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-12 §6 test 7 — the coverage filter contract: the status is read from
/// <c>executed.Result</c> (never the response), a 400+ result / an exception / an already-written
/// row / a [NoAudit] action are all exempt, and strict mode REPLACES the result (legal — the
/// result has not executed yet when an action filter returns).
/// </summary>
public class AuditCoverageFilterTests
{
    private const string TestAction = "x.y";

    [Audited(TestAction)]
    private static IActionResult AuditedAction() => new EmptyResult();

    [NoAudit("not security relevant")]
    private static IActionResult NoAuditAction() => new EmptyResult();

    private static IActionResult PlainAction() => new EmptyResult();

    private static IConfiguration Config(params (string Key, string Value)[] pairs)
    {
        var builder = new ConfigurationBuilder();
        foreach (var (key, value) in pairs)
            builder.AddInMemoryCollection(new Dictionary<string, string?> { [key] = value });
        return builder.Build();
    }

    private static ActionContext ActionContextFor(string methodName)
    {
        var method =
            typeof(AuditCoverageFilterTests).GetMethod(
                methodName,
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Static
            ) ?? throw new InvalidOperationException($"test method {methodName} not found");
        return new ActionContext(
            new DefaultHttpContext(),
            new Microsoft.AspNetCore.Routing.RouteData(),
            new ControllerActionDescriptor { MethodInfo = method }
        );
    }

    private static ActionExecutingContext ExecutingContext(string methodName)
    {
        var filters = new List<IFilterMetadata>();
        return new ActionExecutingContext(
            ActionContextFor(methodName),
            filters,
            new Dictionary<string, object?>(),
            controller: new object()
        );
    }

    private static ActionExecutedContext Executed(
        ActionExecutingContext ctx,
        IActionResult result,
        Exception? exception = null
    ) =>
        // ctx IS an ActionContext (FilterContext derives from it) — reuse it so both stages
        // describe the same action.
        new(ctx, ctx.Filters, new object()) { Result = result, Exception = exception };

    /// <summary>The next-stage delegate the filter awaits — returns the pre-built executed context.</summary>
    private static ActionExecutionDelegate Next(ActionExecutedContext executed) =>
        () => Task.FromResult(executed);

    private static AuditCoverageFilter Filter(params (string Key, string Value)[] config) =>
        new(NullLogger<AuditCoverageFilter>.Instance, Config(config));

    [Fact]
    public async Task AuditedAction_NoRow_MarksGap()
    {
        var ctx = ExecutingContext(nameof(AuditedAction));
        var executed = Executed(ctx, new OkObjectResult(Result.Success()));

        await Filter().OnActionExecutionAsync(ctx, Next(executed));

        Assert.Equal(TestAction, ctx.HttpContext.Items["audit.gap"]);
        Assert.IsType<OkObjectResult>(executed.Result); // non-strict: the result is untouched
    }

    [Fact]
    public async Task FailureResultOnTheAction_NoGap()
    {
        // 400 ON THE RESULT — the response's StatusCode is never read (still the default 200 here).
        var ctx = ExecutingContext(nameof(AuditedAction));
        var executed = Executed(ctx, new BadRequestObjectResult(Result.Failure("x")));

        await Filter().OnActionExecutionAsync(ctx, Next(executed));

        Assert.False(ctx.HttpContext.Items.ContainsKey("audit.gap"));
        Assert.Equal(StatusCodes.Status200OK, ctx.HttpContext.Response.StatusCode); // untouched
    }

    [Fact]
    public async Task WrittenItemPresent_NoGap()
    {
        var ctx = ExecutingContext(nameof(AuditedAction));
        ctx.HttpContext.Items[Infrastructure.Audit.AuditWriter.WrittenItemKey] = true;
        var executed = Executed(ctx, new OkObjectResult(Result.Success()));

        await Filter().OnActionExecutionAsync(ctx, Next(executed));

        Assert.False(ctx.HttpContext.Items.ContainsKey("audit.gap"));
    }

    [Fact]
    public async Task NoAuditAction_NoGap()
    {
        var ctx = ExecutingContext(nameof(NoAuditAction));
        var executed = Executed(ctx, new OkObjectResult(Result.Success()));

        await Filter(("Audit:Strict", "true")).OnActionExecutionAsync(ctx, Next(executed));

        Assert.False(ctx.HttpContext.Items.ContainsKey("audit.gap"));
        Assert.IsType<OkObjectResult>(executed.Result);
    }

    [Fact]
    public async Task PlainAction_NoGap_EvenInStrictMode()
    {
        var ctx = ExecutingContext(nameof(PlainAction));
        var executed = Executed(ctx, new OkObjectResult(Result.Success()));

        await Filter(("Audit:Strict", "true")).OnActionExecutionAsync(ctx, Next(executed));

        Assert.False(ctx.HttpContext.Items.ContainsKey("audit.gap"));
        Assert.IsType<OkObjectResult>(executed.Result);
    }

    [Fact]
    public async Task ActionThrew_NoGap()
    {
        var ctx = ExecutingContext(nameof(AuditedAction));
        var executed = Executed(
            ctx,
            new EmptyResult(),
            new InvalidOperationException("the action itself failed")
        );

        await Filter(("Audit:Strict", "true")).OnActionExecutionAsync(ctx, Next(executed));

        Assert.False(ctx.HttpContext.Items.ContainsKey("audit.gap"));
    }

    [Fact]
    public async Task StrictMode_ReplacesResultWithFailed500()
    {
        var ctx = ExecutingContext(nameof(AuditedAction));
        var executed = Executed(ctx, new OkObjectResult(Result.Success()));

        await Filter(("Audit:Strict", "true")).OnActionExecutionAsync(ctx, Next(executed));

        var replacement = Assert.IsType<ObjectResult>(executed.Result);
        Assert.Equal(StatusCodes.Status500InternalServerError, replacement.StatusCode);
        var failure = Assert.IsType<Result>(replacement.Value);
        Assert.False(failure.IsSuccess);
        Assert.Equal("Audit gap", failure.Message);
    }

    [Fact]
    public async Task StrictModeDefaultsOff_ResultUntouched()
    {
        var ctx = ExecutingContext(nameof(AuditedAction));
        var executed = Executed(ctx, new OkObjectResult(Result.Success()));

        // No Audit:Strict anywhere — default OFF (DB-12 PART 1: no action is [Audited] yet).
        await Filter().OnActionExecutionAsync(ctx, Next(executed));

        Assert.Equal(TestAction, ctx.HttpContext.Items["audit.gap"]);
        Assert.IsType<OkObjectResult>(executed.Result);
    }
}
