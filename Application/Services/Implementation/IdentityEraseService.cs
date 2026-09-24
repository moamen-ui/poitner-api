using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Common.Email;
using Pointer.Application.DTOs.User;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <inheritdoc cref="IIdentityEraseService"/>
public class IdentityEraseService : IIdentityEraseService
{
    // See UserService's identical constant — Role has no dedicated "is the canonical admin" flag
    // beyond the literal system role name.
    private const string WorkspaceAdminRoleName = "Workspace Admin";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMembershipService _memberships;
    private readonly ICurrentUser _currentUser;
    private readonly IPasswordHasher _passwordHasher;
    private readonly IResetTokenService _resetTokens;
    private readonly IEmailService _emailService;
    private readonly IBrandingService _branding;
    private readonly IAuditWriter _audit;

    public IdentityEraseService(
        IUnitOfWork unitOfWork,
        IMembershipService memberships,
        ICurrentUser currentUser,
        IPasswordHasher passwordHasher,
        IResetTokenService resetTokens,
        IEmailService emailService,
        IBrandingService branding,
        IAuditWriter? audit = null
    )
    {
        _unitOfWork = unitOfWork;
        _memberships = memberships;
        _currentUser = currentUser;
        _passwordHasher = passwordHasher;
        _resetTokens = resetTokens;
        _emailService = emailService;
        _branding = branding;
        _audit = audit ?? NoopAuditWriter.Instance;
    }

    public async Task<Result> EraseSelfAsync(DeleteMyAccountRequest request)
    {
        if (_currentUser.Id is not Guid publicId)
            return Result.Failure(MessageKeys.Common.Forbidden);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        if (identity.Role?.IsSuperAdmin == true)
            return Result.Forbidden(MessageKeys.User.CannotEraseSuperAdmin);

        // Passwordless (magic-link) identities confirm through the e-mailed link (§3.4b) — there is
        // no password to check here.
        if (identity.PasswordlessOnly)
            return Result.Failure(MessageKeys.User.EraseNeedsEmailConfirmation);

        if (!_passwordHasher.Verify(request.Password, identity.PasswordHash))
            return Result.Failure(MessageKeys.User.CurrentPasswordIncorrect);

        return await EraseAsync(identity, publicId);
    }

    public async Task<Result> EraseByPublicIdAsync(Guid publicId)
    {
        if (!_currentUser.IsSuperAdmin)
            return Result.Forbidden(MessageKeys.Common.Forbidden);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        if (identity.Role?.IsSuperAdmin == true)
            return Result.Forbidden(MessageKeys.User.CannotEraseSuperAdmin);

        var actor = _currentUser.Id ?? Guid.Empty;
        return await EraseAsync(identity, actor);
    }

    public async Task<Result> RequestEraseLinkAsync()
    {
        if (_currentUser.Id is not Guid publicId)
            return Result.Failure(MessageKeys.Common.Forbidden);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        if (identity.Role?.IsSuperAdmin == true)
            return Result.Forbidden(MessageKeys.User.CannotEraseSuperAdmin);

        // Only the rail a passwordless identity actually needs — a password account confirms with
        // its password at DELETE /api/me instead (one rail per account kind).
        if (!identity.PasswordlessOnly)
            return Result.Failure(MessageKeys.User.EraseUsePassword);

        var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var token = _resetTokens.CreateScoped(
            identity.PublicId,
            identity.SecurityStamp,
            TokenPurposes.Erase
        );
        var link =
            $"{brand.Urls.App.TrimEnd('/')}/delete-account?token={Uri.EscapeDataString(token)}";

        try
        {
            await _emailService.SendAsync(
                identity.Email,
                $"Confirm deleting your {brand.ProductName} account",
                EmailTemplateBuilder.AccountEraseConfirm(
                    link,
                    brand.ProductName,
                    brand.PrimaryColor,
                    brand.Urls.App.TrimEnd('/')
                )
            );
        }
        catch
        { /* best-effort; sender logs failures */
        }

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.IdentityEraseRequested,
                AuditTargets.User,
                identity.PublicId.ToString(),
                _currentUser.TenantId,
                ActorUserIdOverride: identity.PublicId,
                // Review finding #8: only force User on the anonymous path (no ICurrentUser.Id) —
                // this call site is always authenticated-as-self, so leaving it null lets AuditWriter's
                // normal actor-kind resolution run (User here; it would be SuperAdmin if this rail were
                // ever reachable by one, which RequestEraseLinkAsync's own guard above prevents).
                ActorKindOverride: _currentUser.Id is null ? AuditActorKind.User : null
            )
        );

        return Result.Success(MessageKeys.User.EraseLinkSent);
    }

    public async Task<Result> EraseByTokenAsync(string token)
    {
        // One message for every failure (as LoginWithInviteAsync): a guessed/tampered/reused token
        // must not learn which check failed.
        if (
            !_resetTokens.TryValidateScoped(
                token,
                TokenPurposes.Erase,
                out var publicId,
                out var stamp,
                out _
            )
        )
            return Result.Failure(MessageKeys.User.EraseLinkInvalid);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null || identity.SecurityStamp != stamp)
            return Result.Failure(MessageKeys.User.EraseLinkInvalid);

        if (!identity.PasswordlessOnly)
            return Result.Failure(MessageKeys.User.EraseLinkInvalid);

        if (identity.Role?.IsSuperAdmin == true)
            return Result.Failure(MessageKeys.User.EraseLinkInvalid);

        return await EraseAsync(identity, publicId);
    }

    /// <summary>
    /// DB-11c review finding #5: locks <paramref name="workspaceId"/>'s membership rows
    /// (<c>SELECT … FOR UPDATE</c>) — same helper as <c>UserService</c>. No-ops on a non-relational
    /// provider (see <see cref="IUnitOfWork.ExecuteSqlRawAsync"/>).
    /// </summary>
    private Task LockWorkspaceMembershipsAsync(Guid workspaceId) =>
        _unitOfWork.ExecuteSqlRawAsync(
            "SELECT id FROM workspace_memberships WHERE owner_id = {0} FOR UPDATE",
            workspaceId
        );

    /// <summary>
    /// §3.4 — the single erase routine. Fast pre-check (sole-admin, S-13) before the transaction
    /// opens (message ordering); the authoritative, race-safe recheck runs inside the transaction,
    /// under lock, immediately before the writes (review finding #5).
    /// </summary>
    private async Task<Result> EraseAsync(User identity, Guid actor)
    {
        var liveMemberships = await _memberships.ListForIdentityAsync(identity.Id);
        var soleAdminPreCheck = await _memberships.SoleAdminWorkspacesAsync(
            liveMemberships.Select(m => m.Id)
        );
        if (soleAdminPreCheck.Count > 0)
            return _memberships.SoleAdminConflict(soleAdminPreCheck);

        var pid = identity.PublicId;
        var originalEmail = identity.Email;
        var tombstoneEmail = $"erased+{pid:N}@tombstone.invalid";
        // Workspaces where this identity is (was) the live Workspace Admin — the only ones the
        // post-write invariant recheck (below) needs to look at.
        var adminWorkspaceIds = liveMemberships
            .Where(m => m.Role.Name == WorkspaceAdminRoleName)
            .Select(m => m.OwnerId)
            .Distinct()
            .ToList();
        // Review finding #8: force User only on the anonymous confirm-erase path (no
        // ICurrentUser.Id) — self-erase and the super-admin path resolve correctly (User /
        // SuperAdmin, respectively) through AuditWriter's normal actor-kind inference when left null.
        var actorKindOverride = _currentUser.Id is null
            ? AuditActorKind.User
            : (AuditActorKind?)null;

        Result? conflict = null;

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
            conflict = null;

            // Lock every workspace this identity administers before rechecking — closes the race
            // between two concurrent guards (remove/demote/disable/erase) over the same workspace(s).
            foreach (var workspaceId in adminWorkspaceIds)
                await LockWorkspaceMembershipsAsync(workspaceId);

            var soleAdmin = await _memberships.SoleAdminWorkspacesAsync(
                liveMemberships.Select(m => m.Id)
            );
            if (soleAdmin.Count > 0)
            {
                conflict = _memberships.SoleAdminConflict(soleAdmin);
                return;
            }

            // 1. End every live membership (kept for audit; never deletes the identity).
            foreach (var m in liveMemberships)
                await _memberships.EndAsync(m, MembershipEndReason.AccountErased, actor);

            // 2. Hard-delete the person's secrets — every workspace, including already-revoked rows
            //    (they are still secrets).
            var apiKeys = await _unitOfWork
                .Repository<ApiKey>()
                .Query()
                .IgnoreQueryFilters()
                .Where(k => k.UserId == identity.Id)
                .ToListAsync();
            _unitOfWork.Repository<ApiKey>().RemoveRange(apiKeys);

            var deviceLogins = await _unitOfWork
                .Repository<DeviceLogin>()
                .Query()
                .IgnoreQueryFilters()
                .Where(d => d.UserId == pid)
                .ToListAsync();
            _unitOfWork.Repository<DeviceLogin>().RemoveRange(deviceLogins);

            // R5-61 review fix #9: MFA is identity-level (operator only in practice, but the code
            // path is generic) — erase clears the secret/enablement/replay-watermark and hard-deletes
            // every recovery-code row for this identity, same treatment as the DeviceLogin purge above.
            identity.TotpSecret = null;
            identity.TotpEnabledAt = null;
            identity.TotpLastStep = null;

            var recoveryCodes = await _unitOfWork
                .Repository<UserRecoveryCode>()
                .Query()
                .IgnoreQueryFilters()
                .Where(c => c.UserId == identity.Id)
                .ToListAsync();
            _unitOfWork.Repository<UserRecoveryCode>().RemoveRange(recoveryCodes);

            var quickAccessLinks = await _unitOfWork
                .Repository<QuickAccessLink>()
                .Query()
                .IgnoreQueryFilters()
                .Where(l => l.UserId == pid)
                .ToListAsync();
            _unitOfWork.Repository<QuickAccessLink>().RemoveRange(quickAccessLinks);

            var notifications = await _unitOfWork
                .Repository<Notification>()
                .Query()
                .IgnoreQueryFilters()
                .Where(n => n.UserId == pid)
                .ToListAsync();
            _unitOfWork.Repository<Notification>().RemoveRange(notifications);

            var aiRules = await _unitOfWork
                .Repository<AiRule>()
                .Query()
                .IgnoreQueryFilters()
                .Where(r => r.UserId == pid)
                .ToListAsync();
            _unitOfWork.Repository<AiRule>().RemoveRange(aiRules);

            // 2b. Invites (GLM A2): tombstone every row locked to this address — accepted, open,
            //     expired or revoked. OVERWRITE, never null: Invite.Email == null means "anyone with
            //     the link may accept", so nulling would unlock a still-open invite. Review finding
            //     #6: compare case-insensitively — invites.email is not guaranteed to already be
            //     lower-cased everywhere it's written, unlike users.email.
            var normalizedEmail = EmailNormalizer.NormalizeRequired(originalEmail);
            if (normalizedEmail.Length > 0)
            {
                var invites = await _unitOfWork
                    .Repository<Invite>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(i => i.Email != null && i.Email.ToLower() == normalizedEmail)
                    .ToListAsync();
                foreach (var invite in invites)
                {
                    invite.Email = tombstoneEmail;
                    _unitOfWork.Repository<Invite>().Update(invite);
                }
            }

            // 3. Tombstone the row. PublicId is kept so authored content still resolves.
            identity.Email = tombstoneEmail;
            identity.DisplayName = "Deleted user";
            identity.PasswordHash = _passwordHasher.Hash(
                Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
            );
            identity.PasswordlessOnly = true;
            identity.Language = null;
            identity.Theme = null;
            identity.AddCommentShortcut = null;
            identity.RecipientEmail = null;
            identity.IsActive = false;
            identity.SecurityStamp = Guid.NewGuid();
            var now = DateTime.UtcNow;
            identity.ErasedAt = now;
            identity.DeletedAt = now;
            _unitOfWork.Repository<User>().Update(identity);

            // 3b. Review finding #3: tombstone every predecessor row this identity absorbed via the
            //     DB-11a same-e-mail merge — otherwise their original e-mail/name survived even
            //     though the canonical identity was erased (they are already soft-deleted and
            //     membership-less, so there is nothing else to end/scrub for them).
            var mergedPredecessors = await _unitOfWork
                .Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .Where(u => u.MergedIntoUserId == identity.Id)
                .ToListAsync();
            foreach (var predecessor in mergedPredecessors)
            {
                predecessor.Email = $"erased+{predecessor.PublicId:N}@tombstone.invalid";
                predecessor.DisplayName = "Deleted user";
                predecessor.PasswordHash = _passwordHasher.Hash(
                    Guid.NewGuid().ToString("N") + Guid.NewGuid().ToString("N")
                );
                predecessor.RecipientEmail = null;
                predecessor.SecurityStamp = Guid.NewGuid();
                predecessor.ErasedAt = now;
                _unitOfWork.Repository<User>().Update(predecessor);
            }

            await _unitOfWork.SaveChangesAsync();

            // Review finding #5: re-run the sole-admin count, inside the same transaction, right
            // after the write — an invariant-violation guard against a race the lock above should
            // already have made impossible.
            foreach (var workspaceId in adminWorkspaceIds)
            {
                var remaining = await _memberships.CountLiveAdminsAsync(workspaceId);
                if (remaining == 0)
                    throw new InvalidOperationException(
                        "S-13 invariant violated: a workspace was left with no live Workspace Admin (erase race)."
                    );
            }

            // Review finding #9: identity.erased is written with OwnerId=null (this row is
            // operator-level: PublicId is not a workspace-scoped thing), which makes it invisible to
            // a workspace-scoped audit query even though the erase ended that workspace's membership.
            // Fix: also write one identity.erased row PER ended membership, target=membership,
            // OwnerId=that membership's workspace — the same "one row per membership" shape
            // ownership.transferred already uses (§3.6), and exactly what DB-12 §3.6's catalogue row
            // for identity.erased lists both targets for ("membership/id; user/public_id").
            foreach (var m in liveMemberships)
            {
                await _audit.WriteAsync(
                    new AuditEntry(
                        AuditActions.IdentityErased,
                        AuditTargets.Membership,
                        m.Id.ToString(),
                        m.OwnerId,
                        ActorUserIdOverride: actor,
                        ActorKindOverride: actorKindOverride
                    )
                );
            }

            await _audit.WriteAsync(
                new AuditEntry(
                    AuditActions.IdentityErased,
                    AuditTargets.User,
                    pid.ToString(),
                    null,
                    ActorUserIdOverride: actor,
                    ActorKindOverride: actorKindOverride
                )
            );
        });

        if (conflict != null)
            return conflict;

        return Result.Success(MessageKeys.User.Erased);
    }
}
