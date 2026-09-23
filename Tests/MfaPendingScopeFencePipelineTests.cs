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
/// R5-61 — the mfa_pending scope fence's pipeline wiring, same shape as
/// <see cref="SelectionScopeFencePipelineTests"/>: exercises the REAL <c>AddJwtAuth</c>
/// configuration's <c>Events.OnTokenValidated</c> exactly as Kestrel would, for a scope=mfa_pending
/// principal on a disallowed path, a sub-route/trailing-slash variant, and the exact allowed path.
/// </summary>
public class MfaPendingScopeFencePipelineTests
{
    private static async Task<AuthenticateResult?> InvokeOnTokenValidated(string path)
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["JWT:SigningKey"] = new string('k', 40),
                    ["JWT:Issuer"] = "pointer-api",
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
                    new Claim("scope", "mfa_pending"),
                },
                "jwt"
            )
        );

        var ctx = new TokenValidatedContext(httpContext, scheme, options) { Principal = principal };

        await options.Events!.OnTokenValidated!(ctx);

        return ctx.Result;
    }

    [Fact]
    public async Task MfaPendingToken_OnWrongPath_FailsAuthentication()
    {
        var result = await InvokeOnTokenValidated("/api/auth/me");

        Assert.NotNull(result);
        Assert.NotNull(result!.Failure);
    }

    [Fact]
    public async Task MfaPendingToken_OnLoginPath_FailsAuthentication()
    {
        // Per the R5-61 doc's original wording the endpoint might be read as bare
        // POST /api/auth/mfa — the actual route is /api/auth/mfa/verify, so the bare path must
        // NOT be treated as allowed.
        var result = await InvokeOnTokenValidated("/api/auth/mfa");

        Assert.NotNull(result);
        Assert.NotNull(result!.Failure);
    }

    [Fact]
    public async Task MfaPendingToken_OnVerifySubRoute_FailsAuthentication()
    {
        var result = await InvokeOnTokenValidated("/api/auth/mfa/verify/");

        Assert.NotNull(result);
        Assert.NotNull(result!.Failure);
    }

    [Fact]
    public async Task MfaPendingToken_OnExactVerifyPath_IsNotTripped()
    {
        var result = await InvokeOnTokenValidated("/api/auth/mfa/verify");

        Assert.Null(result);
    }

    /// <summary>Finding #13: "mfa_pending token cannot reach /api/me/mfa/*" — the scoped token is
    /// fenced to exactly POST /api/auth/mfa/verify, so none of the MfaController routes (a
    /// different controller entirely, under api/me/mfa) ever accept it.</summary>
    [Theory]
    [InlineData("/api/me/mfa/enrol")]
    [InlineData("/api/me/mfa/verify")]
    [InlineData("/api/me/mfa/disable")]
    public async Task MfaPendingToken_OnMeMfaRoutes_FailsAuthentication(string path)
    {
        var result = await InvokeOnTokenValidated(path);

        Assert.NotNull(result);
        Assert.NotNull(result!.Failure);
    }
}
