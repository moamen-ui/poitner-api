using System.Security.Claims;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Pointer.API.Extensions;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// F4 (DB-11b review): the selection-scope fence had no pipeline test — <see cref="WorkspaceSwitchTests"/>
/// only unit-tests <see cref="SelectionScopeFence.Allows"/> directly, never the wiring in
/// <see cref="AuthenticationExtensions.AddJwtAuth"/> that actually calls it. This project has no
/// <c>Microsoft.AspNetCore.Mvc.Testing</c> / <c>WebApplicationFactory</c> reference (`Tests/Pointer.Tests.csproj`
/// has none and none of the existing tests use one), so this exercises the REAL <c>AddJwtBearer</c>
/// configuration instead: it resolves the actually-configured <see cref="JwtBearerOptions"/> from the
/// real DI registration and invokes <c>Events.OnTokenValidated</c> exactly as Kestrel would for an
/// inbound request, for a selection-scoped principal on a disallowed path and on the allowed one.
/// </summary>
public class SelectionScopeFencePipelineTests
{
    private static async Task<AuthenticateResult?> InvokeOnTokenValidated(string path)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["JWT:SigningKey"] = new string('k', 40),
                    ["JWT:Issuer"] = "pointer-api",
                    // Auth:ValidateSecurityStamp intentionally left unset (defaults to false, same
                    // as dev) — only the scope fence itself is under test here.
                }
            )
            .Build();

        var services = new ServiceCollection();
        services.AddJwtAuth(config);
        await using var provider = services.BuildServiceProvider();

        var options = provider
            .GetRequiredService<IOptionsMonitor<JwtBearerOptions>>()
            .Get(JwtBearerDefaults.AuthenticationScheme);

        var scheme = new AuthenticationScheme(
            JwtBearerDefaults.AuthenticationScheme,
            null,
            typeof(JwtBearerHandler)
        );

        var httpContext = new DefaultHttpContext { RequestServices = provider };
        httpContext.Request.Path = new PathString(path);

        var principal = new ClaimsPrincipal(
            new ClaimsIdentity(
                new[]
                {
                    new Claim("sub", Guid.NewGuid().ToString()),
                    new Claim("scope", "select_workspace"),
                },
                "jwt"
            )
        );

        var ctx = new TokenValidatedContext(httpContext, scheme, options) { Principal = principal };

        await options.Events!.OnTokenValidated!(ctx);

        return ctx.Result;
    }

    [Fact]
    public async Task SelectionToken_OnWrongPath_FailsAuthentication()
    {
        // GET /api/auth/me (or any non-switch route) with a selection token must 401.
        var result = await InvokeOnTokenValidated("/api/auth/me");

        Assert.NotNull(result);
        Assert.NotNull(result!.Failure);
    }

    [Fact]
    public async Task SelectionToken_OnSwitchWorkspaceSubRoute_FailsAuthentication()
    {
        // GLM A6: a sub-route/trailing-slash variant must NOT be treated as the switch endpoint.
        var result = await InvokeOnTokenValidated("/api/auth/switch-workspace/");

        Assert.NotNull(result);
        Assert.NotNull(result!.Failure);
    }

    [Fact]
    public async Task SelectionToken_OnSwitchWorkspacePath_IsNotTripped()
    {
        // The exact switch-workspace path must NOT be failed by the fence: OnTokenValidated returns
        // without ever calling ctx.Fail/ctx.Success (the stamp-validation branch is off by default),
        // so no AuthenticateResult is set at all — status != 401 downstream.
        var result = await InvokeOnTokenValidated("/api/auth/switch-workspace");

        Assert.Null(result);
    }
}
