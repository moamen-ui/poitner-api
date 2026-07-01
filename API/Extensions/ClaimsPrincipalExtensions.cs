using System.Security.Claims;
using System.IdentityModel.Tokens.Jwt;

namespace Pointer.API.Extensions;

public static class ClaimsPrincipalExtensions
{
    /// <summary>
    /// The authenticated user's id (JWT sub), or null if the claim is missing/unparseable.
    /// Mirrors ICurrentUser.Id — callers that need to enforce authentication use <see cref="GetId"/>.
    /// </summary>
    public static Guid? GetIdOrNull(this ClaimsPrincipal user)
    {
        var sub = user.FindFirst(ClaimTypes.NameIdentifier)?.Value
                  ?? user.FindFirst(JwtRegisteredClaimNames.Sub)?.Value
                  ?? user.FindFirst("sub")?.Value;
        return Guid.TryParse(sub, out var g) ? g : (Guid?)null;
    }

    /// <summary>
    /// The authenticated user's id (JWT sub). Throws <see cref="UnauthorizedAccessException"/> if the
    /// claim is missing/unparseable — never silently returns Guid.Empty, which would let a malformed
    /// token be treated as a real (empty-guid) principal. The thrown exception surfaces as a 401.
    /// </summary>
    public static Guid GetId(this ClaimsPrincipal user)
    {
        var id = user.GetIdOrNull();
        if (id is null || id == Guid.Empty)
            throw new UnauthorizedAccessException("Authenticated principal has no valid subject claim.");
        return id.Value;
    }

    public static bool IsAdmin(this ClaimsPrincipal user) =>
        user.FindFirst("is_admin")?.Value == "true";
}
