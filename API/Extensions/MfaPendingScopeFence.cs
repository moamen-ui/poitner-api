using Microsoft.AspNetCore.Http;

namespace Pointer.API.Extensions;

/// <summary>
/// R5-61: which route a <c>scope=mfa_pending</c> token may be used against. Static so tests can call
/// <see cref="Allows"/> directly — same shape as <see cref="SelectionScopeFence"/> (GLM A6 exact-path
/// precedent): case-insensitive, no trailing slash, no sub-routes.
/// </summary>
public static class MfaPendingScopeFence
{
    public static readonly PathString VerifyPath = new("/api/auth/mfa/verify");

    // Nit (§12): `PathString.Equals` is an exact match, so "/api/auth/mfa/verify/" (trailing slash)
    // is refused by design — same exact-path precedent as SelectionScopeFence, deliberately not
    // normalizing a trailing slash into a match.
    public static bool Allows(PathString path) =>
        path.HasValue && path.Equals(VerifyPath, StringComparison.OrdinalIgnoreCase);
}
