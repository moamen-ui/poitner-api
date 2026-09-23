// Infrastructure/Auth/JwtTokenService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure.Auth;

public class JwtOptions
{
    public string SigningKey { get; set; } = "";
    public string Issuer { get; set; } = "pointer-api";
    public int LifetimeHours { get; set; } = 12;
    public int SelectionLifetimeMinutes { get; set; } = 5;

    // R5-62: two-key rotation window. When Keys is empty, SigningKey is treated as a single key
    // with id "k0" (legacy/backward-compatible config — existing .env.prod files need no changes).
    public string ActiveKeyId { get; set; } = "k0";
    public List<JwtKeyEntry> Keys { get; set; } = new();
}

// R5-62: one signing-key slot (an id + its secret). Never log the secret — only the id.
public class JwtKeyEntry
{
    public string Id { get; set; } = "";
    public string Secret { get; set; } = "";
}

public class JwtTokenService(IOptions<JwtOptions> opts) : ITokenService
{
    // R5-62: resolves the key new tokens are signed with. Legacy config (no JWT:Keys) always
    // resolves to a synthetic "k0" entry wrapping JWT:SigningKey, so existing deployments are
    // unaffected. When JWT:Keys is configured, ActiveKeyId must name one of its (non-empty) entries.
    public static JwtKeyEntry ResolveActiveKey(JwtOptions o)
    {
        if (o.Keys is { Count: > 0 })
        {
            var entry = o.Keys.FirstOrDefault(k =>
                k.Id == o.ActiveKeyId && !string.IsNullOrEmpty(k.Secret)
            );
            if (entry is null)
                throw new InvalidOperationException(
                    $"JWT:ActiveKeyId '{o.ActiveKeyId}' is not present in JWT:Keys."
                );
            return entry;
        }
        return new JwtKeyEntry { Id = "k0", Secret = o.SigningKey };
    }

    public string Issue(User u, WorkspaceMembership? membership, int? keyScopes = null)
    {
        var o = opts.Value;
        var activeKey = ResolveActiveKey(o);
        // KeyId on the signing key flows into SigningCredentials.Kid, which JwtHeader picks up as
        // the `kid` header automatically — no manual header construction needed.
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(activeKey.Secret))
        {
            KeyId = activeKey.Id,
        };
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
        var token = new JwtSecurityToken(
            o.Issuer,
            o.Issuer,
            claims,
            expires: DateTime.UtcNow.AddHours(o.LifetimeHours),
            signingCredentials: creds
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }

    // DB-11b: a selection token carries only enough to identify the caller and prove they hold
    // valid credentials — no tenant/role claims, so it cannot pass Policies.Admin/SuperAdmin, and
    // AuthenticationExtensions fences it to POST /api/auth/switch-workspace by exact path
    // (SelectionScopeFence, GLM A6) regardless of Auth:ValidateSecurityStamp.
    public string IssueSelection(User u)
    {
        var o = opts.Value;
        var activeKey = ResolveActiveKey(o);
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(activeKey.Secret))
        {
            KeyId = activeKey.Id,
        };
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.PublicId.ToString()),
            new Claim("email", u.Email),
            new Claim("name", u.DisplayName),
            new Claim("stamp", u.SecurityStamp.ToString()),
            new Claim("scope", "select_workspace"),
        };
        var token = new JwtSecurityToken(
            o.Issuer,
            o.Issuer,
            claims,
            expires: DateTime.UtcNow.AddMinutes(o.SelectionLifetimeMinutes),
            signingCredentials: creds
        );
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
