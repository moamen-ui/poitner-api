using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
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
        var token = _resetTokens.CreateScoped(identity.PublicId, identity.SecurityStamp, TokenPurposes.Erase);
        var link = $"{brand.Urls.App.TrimEnd('/')}/delete-account?token={Uri.EscapeDataString(token)}";

        try
        {
            await _emailService.SendAsync(
                identity.Email,
                $"Confirm deleting your {brand.ProductName} account",
                $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">Confirm deleting your account</h2>
  <p>You asked to delete your account. Click the link below to confirm — it expires in 30 minutes.</p>
  <p><a href=""{link}"" style=""color:#2563eb"">Delete my account &rarr;</a></p>
  <p>Your feedback stays with the workspaces you commented in and is shown as &quot;Deleted user&quot;.</p>
  <p style=""color:#94a3b8;font-size:12px"">If you did not ask for this, ignore this e-mail; nothing happens.</p>
</div>"
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
                ActorKindOverride: AuditActorKind.User
            )
        );

        return Result.Success(MessageKeys.User.EraseLinkSent);
    }

    public async Task<Result> EraseByTokenAsync(string token)
    {
        // One message for every failure (as LoginWithInviteAsync): a guessed/tampered/reused token
        // must not learn which check failed.
        if (!_resetTokens.TryValidateScoped(token, TokenPurposes.Erase, out var publicId, out var stamp, out _))
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
    /// §3.4 — the single erase routine. Precondition (sole-admin, S-13) checked before the
    /// transaction opens; everything else runs inside one transaction.
    /// </summary>
    private async Task<Result> EraseAsync(User identity, Guid actor)
    {
        var liveMemberships = await _memberships.ListForIdentityAsync(identity.Id);
        var soleAdmin = await _memberships.SoleAdminWorkspacesAsync(liveMemberships.Select(m => m.Id));
        if (soleAdmin.Count > 0)
            return _memberships.SoleAdminConflict(soleAdmin);

        var pid = identity.PublicId;
        var originalEmail = identity.Email;
        var tombstoneEmail = $"erased+{pid:N}@tombstone.invalid";

        await _unitOfWork.ExecuteInTransactionAsync(async () =>
        {
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
            //     the link may accept", so nulling would unlock a still-open invite.
            if (!string.IsNullOrEmpty(originalEmail))
            {
                var invites = await _unitOfWork
                    .Repository<Invite>()
                    .Query()
                    .IgnoreQueryFilters()
                    .Where(i => i.Email == originalEmail)
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

            await _unitOfWork.SaveChangesAsync();

            await _audit.WriteAsync(
                new AuditEntry(
                    AuditActions.IdentityErased,
                    AuditTargets.User,
                    pid.ToString(),
                    null,
                    ActorUserIdOverride: actor,
                    ActorKindOverride: AuditActorKind.User
                )
            );
        });

        return Result.Success(MessageKeys.User.Erased);
    }
}
