using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.IdentityModel.Tokens;
using Pointer.API.Auth;
using System.IdentityModel.Tokens.Jwt;

namespace Pointer.API.Extensions;

public static class AuthenticationExtensions
{
    public static IServiceCollection AddJwtAuth(
        this IServiceCollection services,
        IConfiguration config
    )
    {
        // Fail fast at startup: a missing/short signing key silently produces forgeable tokens
        // (HS256 requires ≥ 256-bit / 32-byte keys). Refuse to boot rather than run insecurely.
        var signingKey = config["JWT:SigningKey"];
        if (string.IsNullOrEmpty(signingKey))
            throw new InvalidOperationException("JWT:SigningKey is not configured. Set a random secret of at least 32 bytes.");
        var keyBytes = System.Text.Encoding.UTF8.GetBytes(signingKey);
        if (keyBytes.Length < 32)
            throw new InvalidOperationException($"JWT:SigningKey is too short ({keyBytes.Length} bytes). HS256 requires at least 32 bytes.");

        services
            .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
            .AddJwtBearer(options =>
            {
                options.MapInboundClaims = false;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidIssuer = config["JWT:Issuer"],
                    ValidateAudience = true,
                    ValidAudience = config["JWT:Issuer"],
                    ValidateLifetime = true,
                    IssuerSigningKey = new SymmetricSecurityKey(keyBytes),
                    ValidateIssuerSigningKey = true,
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role",
                };
            });

        // Admin access is capability-based (the user's role grants admin), not tied to a role
        // NAME — so renaming/adding roles never weakens authorization.
        services
            .AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, p => p.RequireClaim("is_admin", "true"))
            .AddPolicy(Policies.SuperAdmin, p => p.RequireClaim("is_super_admin", "true"));

        return services;
    }
}
