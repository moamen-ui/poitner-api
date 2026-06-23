using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

namespace Pointer.API.Extensions;

public static class ClaimsPrincipalExtensions
{
    public static Guid GetId(this ClaimsPrincipal user)
    {
        var sub = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? user.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var g) ? g : Guid.Empty;
    }

    public static bool IsAdmin(this ClaimsPrincipal user) =>
        user.FindFirst("is_admin")?.Value == "true";
}
