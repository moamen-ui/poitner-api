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
    public string Issue(User u, WorkspaceMembership? membership, int? keyScopes = null)
    {
        var o = opts.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        // DB-11a: the membership's role (per workspace) wins when present; a super-admin session
        // (no membership) falls back to the identity's own (legacy) Role.
        var role = membership?.Role ?? u.Role;
        var roleId = membership?.RoleId ?? u.RoleId;
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.PublicId.ToString()),
            new Claim("email", u.Email),
            new Claim("name", u.DisplayName),
            new Claim("role_id", roleId.ToString()),
            new Claim("role", role?.Name ?? string.Empty),
            new Claim("is_admin", (role?.GrantsAdmin ?? false) ? "true" : "false"),
            new Claim("is_super_admin", (role?.IsSuperAdmin ?? false) ? "true" : "false"),
            new Claim("is_quick_access", (role?.QuickAccess ?? false) ? "true" : "false"),
            // H1: session-invalidation stamp. Validated per-request (when Auth:ValidateSecurityStamp
            // is on) so disable/reject/password-change can revoke this token before it expires.
            new Claim("stamp", u.SecurityStamp.ToString()),
        };
        if (membership is not null)
        {
            claims.Add(new Claim("tenant", membership.OwnerId.ToString()));
            // DB-11a/R16: this membership's own stamp. Rotated on role change / disable / removal —
            // revokes THIS workspace's sessions only, never the identity's other workspaces.
            claims.Add(new Claim("mstamp", membership.SecurityStamp.ToString()));
        }
        // Only present when this session was opened with an API key; §25 enforces against it.
        if (keyScopes is not null)
            claims.Add(new Claim("key_scopes", keyScopes.Value.ToString()));
        var token = new JwtSecurityToken(o.Issuer, o.Issuer, claims,
            expires: DateTime.UtcNow.AddHours(o.LifetimeHours), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
