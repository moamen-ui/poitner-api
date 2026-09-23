using System;
using System.Collections.Generic;
using System.IdentityModel.Tokens.Jwt;
using System.Linq;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pointer.API.Extensions;
using Pointer.Domain.Entity;
using Pointer.Infrastructure.Auth;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// R5-62: `kid` header + two-key rotation window. <see cref="JwtTokenService.ResolveActiveKey"/>
/// picks the signing key for new tokens; <see cref="AuthenticationExtensions.ResolveAllKeys"/>
/// resolves the validation key ring. Both fall back to a synthetic "k0" wrapping the legacy
/// single <c>JWT:SigningKey</c> when no <c>JWT:Keys</c> are configured, so existing deployments
/// need zero config changes.
/// </summary>
public class JwtKeyRotationTests
{
    private static ClaimsPrincipal ValidateWithKeys(
        string token,
        List<JwtKeyEntry> keys,
        string issuer = "pointer-api"
    )
    {
        var validationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = issuer,
            ValidateAudience = true,
            ValidAudience = issuer,
            ValidateLifetime = true,
            IssuerSigningKeys = keys.Select(k => new SymmetricSecurityKey(
                    Encoding.UTF8.GetBytes(k.Secret)
                )
                {
                    KeyId = k.Id,
                })
                .ToList(),
            ValidateIssuerSigningKey = true,
        };
        return new JwtSecurityTokenHandler().ValidateToken(token, validationParameters, out _);
    }

    private static User MakeUser(Role role) =>
        new User
        {
            Id = 1,
            Email = "a@b.c",
            DisplayName = "A",
            RoleId = role.Id,
            Role = role,
        };

    [Fact]
    public void Issue_SetsKidHeader()
    {
        var opts = Options.Create(
            new JwtOptions
            {
                SigningKey = new string('k', 40),
                Issuer = "pointer-api",
                LifetimeHours = 12,
            }
        );
        var svc = new JwtTokenService(opts);
        var role = new Role { Id = 2, Name = "Developer" };

        var token = svc.Issue(MakeUser(role), null);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("k0", jwt.Header.Kid);

        // The selection token (DB-11b) must carry a `kid` too — it is validated by the same
        // multi-key ring.
        var selectionToken = svc.IssueSelection(MakeUser(role));
        var selectionJwt = new JwtSecurityTokenHandler().ReadJwtToken(selectionToken);
        Assert.Equal("k0", selectionJwt.Header.Kid);
    }

    [Fact]
    public void Validate_AcceptsTokenFromActiveKey()
    {
        var k0 = new JwtKeyEntry { Id = "k0", Secret = new string('a', 32) };
        var k1 = new JwtKeyEntry { Id = "k1", Secret = new string('b', 32) };
        var opts = Options.Create(
            new JwtOptions
            {
                SigningKey = k0.Secret,
                Issuer = "pointer-api",
                LifetimeHours = 12,
                ActiveKeyId = "k1",
                Keys = new List<JwtKeyEntry> { k0, k1 },
            }
        );
        var svc = new JwtTokenService(opts);
        var role = new Role { Id = 2, Name = "Developer" };

        var token = svc.Issue(MakeUser(role), null);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("k1", jwt.Header.Kid);

        var principal = ValidateWithKeys(token, new List<JwtKeyEntry> { k0, k1 });
        Assert.NotNull(principal);
    }

    [Fact]
    public void Validate_AcceptsTokenFromOldKeyDuringOverlap()
    {
        var k0 = new JwtKeyEntry { Id = "k0", Secret = new string('a', 32) };
        var k1 = new JwtKeyEntry { Id = "k1", Secret = new string('b', 32) };
        // Token issued while k0 was still the active key.
        var issueOpts = Options.Create(
            new JwtOptions
            {
                SigningKey = k0.Secret,
                Issuer = "pointer-api",
                LifetimeHours = 12,
                ActiveKeyId = "k0",
                Keys = new List<JwtKeyEntry> { k0, k1 },
            }
        );
        var svc = new JwtTokenService(issueOpts);
        var role = new Role { Id = 2, Name = "Developer" };
        var token = svc.Issue(MakeUser(role), null);

        // Deployment has since switched the active key to k1, but k0 is still in the ring
        // (the rotation-window overlap) — the old token must still validate.
        var principal = ValidateWithKeys(token, new List<JwtKeyEntry> { k0, k1 });
        Assert.NotNull(principal);
    }

    [Fact]
    public void Validate_RejectsTokenAfterKeyRemoval()
    {
        var k0 = new JwtKeyEntry { Id = "k0", Secret = new string('a', 32) };
        var k1 = new JwtKeyEntry { Id = "k1", Secret = new string('b', 32) };
        var issueOpts = Options.Create(
            new JwtOptions
            {
                SigningKey = k0.Secret,
                Issuer = "pointer-api",
                LifetimeHours = 12,
                ActiveKeyId = "k0",
                Keys = new List<JwtKeyEntry> { k0 },
            }
        );
        var svc = new JwtTokenService(issueOpts);
        var role = new Role { Id = 2, Name = "Developer" };
        var token = svc.Issue(MakeUser(role), null);

        // k0 has been retired from the ring (post rotation-window) — the token must be rejected.
        Assert.Throws<SecurityTokenSignatureKeyNotFoundException>(() =>
            ValidateWithKeys(token, new List<JwtKeyEntry> { k1 })
        );
    }

    [Fact]
    public void Validate_AcceptsTokenWithNoKidHeader_AgainstCurrentKey()
    {
        // Simulates a token issued by the pre-R5-62 code path (no `kid` header at all) that is
        // still outstanding (up to 12h) right after the deploy. IssuerSigningKeys with no `kid`
        // match on the token falls back to trying every configured key.
        var k0 = new JwtKeyEntry { Id = "k0", Secret = new string('a', 32) };
        var keyWithNoId = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(k0.Secret));
        var creds = new SigningCredentials(keyWithNoId, SecurityAlgorithms.HmacSha256);
        var claims = new List<Claim>
        {
            new Claim(JwtRegisteredClaimNames.Sub, Guid.NewGuid().ToString()),
        };
        var legacyToken = new JwtSecurityToken(
            "pointer-api",
            "pointer-api",
            claims,
            expires: DateTime.UtcNow.AddHours(1),
            signingCredentials: creds
        );
        var raw = new JwtSecurityTokenHandler().WriteToken(legacyToken);
        Assert.Null(new JwtSecurityTokenHandler().ReadJwtToken(raw).Header.Kid);

        var principal = ValidateWithKeys(raw, new List<JwtKeyEntry> { k0 });
        Assert.NotNull(principal);
    }

    [Fact]
    public void LegacyConfig_SingleSigningKey_WorksAsK0()
    {
        var secret = new string('k', 40);
        var o = new JwtOptions { SigningKey = secret, Keys = new List<JwtKeyEntry>() };

        var active = JwtTokenService.ResolveActiveKey(o);
        Assert.Equal("k0", active.Id);
        Assert.Equal(secret, active.Secret);

        var svc = new JwtTokenService(Options.Create(o));
        var role = new Role { Id = 2, Name = "Developer" };
        var token = svc.Issue(MakeUser(role), null);

        var principal = ValidateWithKeys(token, new List<JwtKeyEntry> { active });
        Assert.NotNull(principal);
    }

    [Fact]
    public void AllKeys_MustBe32Bytes()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["JWT:SigningKey"] = new string('k', 40),
                    ["JWT:Keys:0:Id"] = "k0",
                    ["JWT:Keys:0:Secret"] = "too-short",
                }
            )
            .Build();

        Assert.Throws<InvalidOperationException>(() =>
            AuthenticationExtensions.ResolveAllKeys(config)
        );
    }

    [Fact]
    public void ResolveAllKeys_SkipsEmptyRotationSlot()
    {
        // An unset k1 (empty Secret, matching docker-compose.prod.yml's JWT__Keys__1__Secret
        // default of "") must not trip the >= 32-byte check at startup.
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?>
                {
                    ["JWT:SigningKey"] = new string('k', 40),
                    ["JWT:Keys:0:Id"] = "k0",
                    ["JWT:Keys:0:Secret"] = new string('k', 40),
                    ["JWT:Keys:1:Id"] = "",
                    ["JWT:Keys:1:Secret"] = "",
                }
            )
            .Build();

        var keys = AuthenticationExtensions.ResolveAllKeys(config);
        Assert.Single(keys);
        Assert.Equal("k0", keys[0].Id);
    }

    [Fact]
    public void ActiveKeyId_NotInList_Throws()
    {
        var k0 = new JwtKeyEntry { Id = "k0", Secret = new string('a', 32) };
        var k1 = new JwtKeyEntry { Id = "k1", Secret = new string('b', 32) };
        var o = new JwtOptions
        {
            ActiveKeyId = "k2",
            Keys = new List<JwtKeyEntry> { k0, k1 },
        };

        Assert.Throws<InvalidOperationException>(() => JwtTokenService.ResolveActiveKey(o));
    }
}
