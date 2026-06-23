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
                    IssuerSigningKey = new SymmetricSecurityKey(
                        System.Text.Encoding.UTF8.GetBytes(config["JWT:SigningKey"]!)
                    ),
                    ValidateIssuerSigningKey = true,
                    NameClaimType = JwtRegisteredClaimNames.Sub,
                    RoleClaimType = "role",
                };
            });

        // Admin access is capability-based (the user's role grants admin), not tied to a role
        // NAME — so renaming/adding roles never weakens authorization.
        services
            .AddAuthorizationBuilder()
            .AddPolicy(Policies.Admin, p => p.RequireClaim("is_admin", "true"));

        return services;
    }
}
