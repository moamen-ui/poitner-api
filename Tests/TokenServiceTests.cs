using System.IdentityModel.Tokens.Jwt;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Options;
using Pointer.Domain.Entity;
using Pointer.Infrastructure.Auth;
using Xunit;

public class TokenServiceTests
{
    [Fact]
    public void Issue_includes_sub_role_email_and_admin_flag()
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
        var role = new Role
        {
            Id = 2,
            Name = "Developer",
            GrantsAdmin = false,
        };
        // DB-11f: a session's role is its membership's role — a non-super identity with no
        // membership carries no role (UserMapper.SessionRole). Pass a membership to exercise the
        // claims this test is actually about.
        var membership = new WorkspaceMembership
        {
            Id = 1,
            OwnerId = Guid.NewGuid(),
            RoleId = role.Id,
            Role = role,
            SecurityStamp = Guid.NewGuid(),
        };
        var token = svc.Issue(
            new User
            {
                Id = 7,
                Email = "a@b.c",
                DisplayName = "A",
                RoleId = role.Id,
                Role = role,
            },
            membership
        );
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("a@b.c", jwt.Claims.First(c => c.Type == "email").Value);
        Assert.Equal("Developer", jwt.Claims.First(c => c.Type == "role").Value);
        Assert.Equal("2", jwt.Claims.First(c => c.Type == "role_id").Value);
        Assert.Equal("false", jwt.Claims.First(c => c.Type == "is_admin").Value);
        Assert.False(string.IsNullOrEmpty(jwt.Subject));
    }

    [Fact]
    public void Issue_includes_is_super_admin_true_when_role_is_super_admin()
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
        var role = new Role
        {
            Id = 1,
            Name = "SuperAdmin",
            GrantsAdmin = true,
            IsSuperAdmin = true,
        };
        var token = svc.Issue(
            new User
            {
                Id = 1,
                Email = "sa@test.com",
                DisplayName = "SA",
                RoleId = role.Id,
                Role = role,
            },
            null
        );
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("true", jwt.Claims.First(c => c.Type == "is_super_admin").Value);
    }

    [Fact]
    public void Issue_includes_tenant_claim_when_membership_is_present_and_omits_it_when_null()
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
        var role = new Role
        {
            Id = 2,
            Name = "Developer",
            GrantsAdmin = false,
        };
        var tenantId = Guid.NewGuid();
        var membership = new WorkspaceMembership
        {
            Id = 9,
            OwnerId = tenantId,
            RoleId = role.Id,
            Role = role,
            SecurityStamp = Guid.NewGuid(),
        };

        // Membership present: should have tenant claim
        var tokenWithTenant = svc.Issue(
            new User
            {
                Id = 5,
                Email = "dev@tenant.com",
                DisplayName = "Dev",
                RoleId = role.Id,
                Role = role,
                OwnerId = tenantId,
            },
            membership
        );
        var jwtWithTenant = new JwtSecurityTokenHandler().ReadJwtToken(tokenWithTenant);
        Assert.Equal(
            tenantId.ToString(),
            jwtWithTenant.Claims.First(c => c.Type == "tenant").Value
        );

        // No membership (e.g. super admin): should NOT have tenant claim
        var tokenNoTenant = svc.Issue(
            new User
            {
                Id = 6,
                Email = "global@test.com",
                DisplayName = "Global",
                RoleId = role.Id,
                Role = role,
                OwnerId = null,
            },
            null
        );
        var jwtNoTenant = new JwtSecurityTokenHandler().ReadJwtToken(tokenNoTenant);
        Assert.DoesNotContain(jwtNoTenant.Claims, c => c.Type == "tenant");
    }

    [Fact]
    public void Issue_WithMembership_EmitsTenantAndMstamp()
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
        var identityRole = new Role
        {
            Id = 1,
            Name = "Workspace Admin",
            GrantsAdmin = true,
        };
        var membershipRole = new Role
        {
            Id = 2,
            Name = "Developer",
            GrantsAdmin = false,
        };
        var tenantId = Guid.NewGuid();
        var mstamp = Guid.NewGuid();
        var membership = new WorkspaceMembership
        {
            Id = 3,
            OwnerId = tenantId,
            RoleId = membershipRole.Id,
            Role = membershipRole,
            SecurityStamp = mstamp,
        };
        var user = new User
        {
            Id = 5,
            Email = "dev@tenant.com",
            DisplayName = "Dev",
            RoleId = identityRole.Id,
            Role = identityRole,
        };

        var token = svc.Issue(user, membership);
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);

        Assert.Equal(tenantId.ToString(), jwt.Claims.First(c => c.Type == "tenant").Value);
        Assert.Equal(mstamp.ToString(), jwt.Claims.First(c => c.Type == "mstamp").Value);
        // The MEMBERSHIP's role wins over the identity's own (legacy) role.
        Assert.Equal("Developer", jwt.Claims.First(c => c.Type == "role").Value);
        Assert.Equal("2", jwt.Claims.First(c => c.Type == "role_id").Value);
    }

    [Fact]
    public void Issue_includes_security_stamp_claim()
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
        var stamp = Guid.NewGuid();
        var token = svc.Issue(
            new User
            {
                Id = 7,
                Email = "a@b.c",
                DisplayName = "A",
                RoleId = role.Id,
                Role = role,
                SecurityStamp = stamp,
            },
            null
        );
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal(stamp.ToString(), jwt.Claims.First(c => c.Type == "stamp").Value);
    }

    [Fact]
    public void ResetToken_roundtrips_publicId_and_stamp_and_rejects_tampering()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(
                new Dictionary<string, string?> { ["JWT:SigningKey"] = new string('k', 40) }
            )
            .Build();
        var svc = new ResetTokenService(config);
        var id = Guid.NewGuid();
        var stamp = Guid.NewGuid();

        var token = svc.Create(id, stamp);
        Assert.True(svc.TryValidate(token, out var gotId, out var gotStamp));
        Assert.Equal(id, gotId);
        Assert.Equal(stamp, gotStamp);

        // Tampered signature / malformed token is rejected.
        Assert.False(svc.TryValidate(token + "x", out _, out _));
        Assert.False(svc.TryValidate("garbage", out _, out _));
    }
}
