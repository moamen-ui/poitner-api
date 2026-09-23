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

    /// <summary>
    /// DB-11b: issues a 5-minute selection token (claims: <c>sub</c>, <c>email</c>, <c>name</c>,
    /// <c>stamp</c>, <c>scope = "select_workspace"</c> — no <c>tenant</c>/<c>role_id</c>/<c>role</c>/
    /// <c>is_admin</c>/<c>is_super_admin</c>/<c>is_quick_access</c>/<c>mstamp</c>) for a caller with
    /// several live memberships to pick one via <c>POST /api/auth/switch-workspace</c>.
    /// </summary>
    string IssueSelection(User user);

    /// <summary>
    /// DB-13: issues a read-only, time-boxed impersonation token for a super admin operator viewing
    /// <paramref name="workspaceId"/> under the live <paramref name="sessionId"/>. Claims: <c>sub</c>,
    /// <c>email</c>, <c>name</c>, <c>role_id</c>, <c>role</c>, <c>is_admin = "true"</c>,
    /// <c>is_super_admin = "true"</c>, <c>stamp</c>, <c>tenant = workspaceId</c>,
    /// <c>scope = "impersonate"</c>, <c>imp = sessionId</c> — no <c>mstamp</c>, no
    /// <c>is_quick_access</c>. Hard-expires at <paramref name="expiresAt"/> (≤ 60 min).
    /// </summary>
    string IssueImpersonation(
        User operatorUser,
        Guid workspaceId,
        long sessionId,
        DateTime expiresAt
    );
}
