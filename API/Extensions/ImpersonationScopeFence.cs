using System;
using Microsoft.AspNetCore.Http;

namespace Pointer.API.Extensions;

/// <summary>
/// DB-13: an impersonation token may READ anything its tenant claim admits, and may do exactly one
/// write — end itself. Exact path comparison (GLM A6 precedent) — a prefix/sub-route match is
/// deliberately never used here.
///
/// DB-13 review fix #1: this method-based fence is necessary but not sufficient. GET is not proof
/// of read-only — a get-or-create GET (an <c>api_keys</c> row minted on first read, a "mark read"
/// side effect, a counter bump) still writes, and the fence has no way to see that. Every such
/// service method must self-guard on <c>ICurrentUser.IsImpersonating</c> (see
/// ProfileService.GetOrCreateApiKeyAsync).
/// </summary>
public static class ImpersonationScopeFence
{
    public static readonly PathString EndPath = new("/api/admin/impersonation/end");

    public static bool Allows(string method, PathString path) =>
        HttpMethods.IsGet(method)
        || HttpMethods.IsHead(method)
        || HttpMethods.IsOptions(method)
        || (
            HttpMethods.IsPost(method)
            && path.HasValue
            && path.Equals(EndPath, StringComparison.OrdinalIgnoreCase)
        );
}
