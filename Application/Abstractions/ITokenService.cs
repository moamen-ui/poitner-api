using Pointer.Domain.Entity;

namespace Pointer.Application.Abstractions;

public interface ITokenService
{
    /// <param name="user">The identity. Its Role is used only when <paramref name="membership"/> is null (super admins).</param>
    /// <param name="membership">
    /// The workspace membership this session is opened in, when there is one (DB-11a). Its Role
    /// becomes the token's role/role_id/is_admin/etc, its OwnerId becomes the `tenant` claim, and its
    /// SecurityStamp becomes the `mstamp` claim. Null for a super-admin session (no workspace).
    /// </param>
    /// <param name="keyScopes">
    /// When the session was opened with an API key, that key's <c>ApiKeyScopes</c> value, emitted as a
    /// <c>key_scopes</c> claim so §25 can enforce scopes without re-reading the key. Null for password
    /// logins, and then the claim is omitted entirely.
    /// </param>
    string Issue(User user, WorkspaceMembership? membership, int? keyScopes = null);
}
