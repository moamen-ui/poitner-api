using Microsoft.Extensions.Options;
using Pointer.Domain.Entity;
using Pointer.Infrastructure.Auth;
using System.IdentityModel.Tokens.Jwt;
using Xunit;
public class TokenServiceTests
{
    [Fact] public void Issue_includes_sub_role_email_and_admin_flag()
    {
        var opts = Options.Create(new JwtOptions { SigningKey = new string('k', 40), Issuer = "pointer-api", LifetimeHours = 12 });
        var svc = new JwtTokenService(opts);
        var role = new Role { Id = 2, Name = "Developer", GrantsAdmin = false };
        var token = svc.Issue(new User { Id = 7, Email = "a@b.c", DisplayName = "A", RoleId = role.Id, Role = role });
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("a@b.c", jwt.Claims.First(c => c.Type == "email").Value);
        Assert.Equal("Developer", jwt.Claims.First(c => c.Type == "role").Value);
        Assert.Equal("2", jwt.Claims.First(c => c.Type == "role_id").Value);
        Assert.Equal("false", jwt.Claims.First(c => c.Type == "is_admin").Value);
        Assert.False(string.IsNullOrEmpty(jwt.Subject));
    }
}
