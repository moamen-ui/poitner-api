// Infrastructure/Auth/JwtTokenService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
namespace Pointer.Infrastructure.Auth;
public class JwtOptions { public string SigningKey { get; set; } = ""; public string Issuer { get; set; } = "pointer-api"; public int LifetimeHours { get; set; } = 12; }
public class JwtTokenService(IOptions<JwtOptions> opts) : ITokenService
{
    public string Issue(User u)
    {
        var o = opts.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        // u.Role (the Role entity) must be loaded by the caller (AuthService Includes it).
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.PublicId.ToString()),
            new Claim("email", u.Email),
            new Claim("name", u.DisplayName),
            new Claim("role_id", u.RoleId.ToString()),
            new Claim("role", u.Role?.Name ?? string.Empty),
            new Claim("is_admin", (u.Role?.GrantsAdmin ?? false) ? "true" : "false"),
        };
        var token = new JwtSecurityToken(o.Issuer, o.Issuer, claims,
            expires: DateTime.UtcNow.AddHours(o.LifetimeHours), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
