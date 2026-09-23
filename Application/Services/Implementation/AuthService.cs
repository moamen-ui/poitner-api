using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Auth;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class AuthService : IAuthService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly ICurrentUser _currentUser;
    private readonly ISettingsService _settings;
    private readonly IResetTokenService _resetTokens;
    private readonly IEmailService _emailService;
    private readonly IBrandingService _branding;
    private readonly IApiKeyService _apiKeys;
    private readonly ILoginAttemptLimiter _loginLimiter;
    private readonly IMembershipService _memberships;

    public AuthService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        ICurrentUser currentUser,
        ISettingsService settings,
        IResetTokenService resetTokens,
        IEmailService emailService,
        IBrandingService branding,
        IApiKeyService apiKeys,
        ILoginAttemptLimiter loginLimiter,
        IMembershipService memberships
    )
    {
        _unitOfWork = unitOfWork;
        _apiKeys = apiKeys;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _currentUser = currentUser;
        _settings = settings;
        _resetTokens = resetTokens;
        _emailService = emailService;
        _branding = branding;
        _loginLimiter = loginLimiter;
        _memberships = memberships;
    }

    public async Task<Result> RequestPasswordResetAsync(ForgotPasswordRequest request)
    {
        var emailNormalized = EmailNormalizer.NormalizeRequired(request.Email);
        if (emailNormalized.Length > 0)
        {
            // Anonymous path → bypass the tenant query filter; only real (non-demo) active accounts.
            var user = await _unitOfWork
                .Repository<User>()
                .Query()
                .IgnoreQueryFilters()
                .AsNoTracking()
                .Include(u => u.Role)
                .Where(u =>
                    u.DeletedAt == null && u.IsActive && !u.IsDemo && u.Email == emailNormalized
                )
                .FirstOrDefaultAsync();

            // DB-11a: a non-super-admin identity with no live membership anywhere has nothing to
            // reset into — treat the same as "no such account" (still silent to the caller).
            if (user != null && !(user.Role?.IsSuperAdmin ?? false))
            {
                var memberships = await _memberships.ListForIdentityAsync(user.Id);
                if (memberships.Count == 0)
                    user = null;
            }

            if (user != null)
            {
                var resetBrand = await _branding.BuildResponseAsync("", new HashSet<string>());
                var resetProductName = resetBrand.ProductName;
                var resetAppUrl = resetBrand.Urls.App.TrimEnd('/');
                var token = _resetTokens.Create(user.PublicId, user.SecurityStamp);
                var link = $"{resetAppUrl}/reset?token={Uri.EscapeDataString(token)}";
                // Subject stays product-only for a reset email (it is sent before the recipient is
                // known to be who they claim, so it should read the same for everyone); the workspace
                // name, when there is one to name, only appears in the body. One lookup per send;
                // null (missing row or still the DB-03 placeholder) omits the line entirely.
                var resetWorkspaceName = await WorkspaceNameResolver.ResolveForEmailAsync(
                    _unitOfWork,
                    user.OwnerId
                );
                var resetWorkspaceLine =
                    resetWorkspaceName != null
                        ? $@"<p style=""color:#475569;font-size:13px"">This is for your account in the <b>{System.Net.WebUtility.HtmlEncode(resetWorkspaceName)}</b> workspace.</p>"
                        : string.Empty;
                try
                {
                    await _emailService.SendAsync(
                        user.Email,
                        $"Reset your {resetProductName} password",
                        $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">Reset your password</h2>
  <p>Click the link below to choose a new password. It expires in 30 minutes.</p>
  <p><a href=""{link}"" style=""color:#2563eb"">Reset my password &rarr;</a></p>
  {resetWorkspaceLine}
  <p style=""color:#94a3b8;font-size:12px"">If you didn't request this, you can safely ignore this email.</p>
</div>"
                    );
                }
                catch
                { /* best-effort; sender logs failures */
                }
            }
        }

        // Always succeed — never reveal whether an email is registered.
        return Result.Success();
    }

    public async Task<Result> ResetPasswordAsync(ResetPasswordRequest request)
    {
        if (string.IsNullOrWhiteSpace(request.NewPassword) || request.NewPassword.Length < 8)
            return Result.Failure("Password must be at least 8 characters.");

        if (
            !_resetTokens.TryValidate(
                request.Token ?? string.Empty,
                out var publicId,
                out var tokenStamp
            )
        )
            return Result.Failure("This reset link is invalid or has expired.");

        var user = await _unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Where(u => u.DeletedAt == null && u.PublicId == publicId)
            .FirstOrDefaultAsync();

        if (user == null)
            return Result.Failure("This reset link is invalid or has expired.");

        // H2 (single-use): the token must carry the user's CURRENT security stamp. A link that was
        // already used (or superseded by any later password change) was signed with an older stamp
        // and no longer matches — reject it without revealing why.
        if (user.SecurityStamp != tokenStamp)
            return Result.Failure("This reset link is invalid or has expired.");

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        // Bump the stamp: invalidates this reset link (single-use) AND every existing access token
        // for this user (H1), so a password reset forcibly logs out all sessions.
        user.SecurityStamp = Guid.NewGuid();
        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    public async Task<Result> ChangePasswordAsync(ChangePasswordRequest request)
    {
        if (_currentUser.Id is not Guid publicId)
            return Result.Failure(MessageKeys.Auth.InvalidCredentials);

        // Own row, any tenant — the standard query filter already scopes this correctly (a caller's
        // own row is always visible to themselves), so no IgnoreQueryFilters needed here.
        var user = await _unitOfWork
            .Repository<User>()
            .Query()
            .Where(u => u.DeletedAt == null && u.PublicId == publicId)
            .FirstOrDefaultAsync();

        if (user == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        if (!_passwordHasher.Verify(request.CurrentPassword, user.PasswordHash))
            return Result.Failure(MessageKeys.User.CurrentPasswordIncorrect);

        user.PasswordHash = _passwordHasher.Hash(request.NewPassword);
        // Bump the stamp: invalidates every existing access token for this user (H1), same as
        // ResetPasswordAsync — a password change forcibly logs out all sessions, including this one.
        user.SecurityStamp = Guid.NewGuid();
        _unitOfWork.Repository<User>().Update(user);
        await _unitOfWork.SaveChangesAsync();

        var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
        // Subject stays product-only; the workspace name (when there is one to name) only appears in
        // the body. One lookup per send; null (missing row or still the DB-03 placeholder) falls back
        // to the pre-existing wording.
        var changeWorkspaceName = await WorkspaceNameResolver.ResolveForEmailAsync(
            _unitOfWork,
            user.OwnerId
        );
        try
        {
            await _emailService.SendAsync(
                user.Email,
                $"Your {brand.ProductName} password was changed",
                BuildPasswordChangedEmailHtml(
                    user.DisplayName,
                    brand.ProductName,
                    changeWorkspaceName
                )
            );
        }
        catch
        { /* best-effort; sender logs failures */
        }

        return Result.Success(MessageKeys.User.PasswordChanged);
    }

    // Resolves the workspace's own name from workspaces.name (DB-03). Null ownerId (super admin) →
    // null; a missing row → null.
    private async Task<string?> ResolveTenantNameAsync(Guid? ownerId)
    {
        if (ownerId == null)
            return null;

        return await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AsNoTracking()
            .Where(w => w.Id == ownerId.Value)
            .Select(w => w.Name)
            .FirstOrDefaultAsync();
    }

    // workspaceName is RAW (not yet encoded) — null means omit (missing row, super-admin/global user,
    // or still the DB-03 placeholder).
    private static string BuildPasswordChangedEmailHtml(
        string displayName,
        string productName,
        string? workspaceName = null
    )
    {
        var workspaceClause =
            workspaceName != null
                ? $" in the <b>{System.Net.WebUtility.HtmlEncode(workspaceName)}</b> workspace"
                : string.Empty;
        return $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">Your password was changed</h2>
  <p style=""margin:0 0 16px"">Hi {displayName}, this confirms your {productName} account password{workspaceClause} was just changed. You've been signed out of all devices.</p>
  <p style=""color:#94a3b8;font-size:12px"">If you didn't make this change, reset your password immediately and contact your workspace admin.</p>
</div>";
    }

    public async Task<Result<LoginResponse>> LoginAsync(LoginRequest request)
    {
        var emailNormalized = EmailNormalizer.NormalizeRequired(request.Email);

        // Locked accounts are rejected BEFORE touching the DB or verifying passwords (R5-59 §12).
        if (await _loginLimiter.IsLockedAsync(emailNormalized))
        {
            var retryAfter = await _loginLimiter.GetRetryAfterSecondsAsync(emailNormalized);
            return Result<LoginResponse>.Locked(
                MessageKeys.Auth.TooManyAttempts,
                new LoginResponse { Status = "locked" },
                retryAfter
            );
        }

        // DB-11a: one identity per e-mail. Login is anonymous (no tenant claim yet), so
        // FindIdentityByEmailAsync bypasses the (now membership-based) User query filter itself.
        var user = await _memberships.FindIdentityByEmailAsync(emailNormalized);

        // Verify the password FIRST so account status is only revealed to correct credentials
        // (avoids leaking which emails exist / are pending/rejected to anonymous guessers).
        // Unknown e-mails and wrong passwords BOTH count as failures to prevent enumeration side channels.
        if (user == null || !_passwordHasher.Verify(request.Password, user.PasswordHash))
        {
            await _loginLimiter.RecordFailureAsync(emailNormalized);

            // GLM A8: the identity exists but the password does not verify, and it absorbed a
            // merged row (DB-11a Migration 2 — D1: newest password wins) — point at "Forgot
            // password" instead of the generic message. Reveals nothing beyond "merged identities
            // exist", and the census is expected to make this set empty in production. Still a
            // failed password attempt, so the lockout counter above already recorded it.
            if (
                user != null
                && await _unitOfWork
                    .Repository<User>()
                    .Query()
                    .IgnoreQueryFilters()
                    .AnyAsync(u => u.MergedIntoUserId == user.Id)
            )
                return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidCredentialsAfterMerge);

            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidCredentials);
        }

        // A quick-access client signs in only through their magic link. Their PasswordHash is
        // random and unusable, so the check above already fails — this is the EXPLICIT refusal, so
        // the guarantee does not quietly depend on a hash never matching. It also returns the same
        // message as a wrong password: which accounts are passwordless is not an anonymous
        // caller's business.
        if (user.PasswordlessOnly)
        {
            await _loginLimiter.RecordFailureAsync(emailNormalized);
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidCredentials);
        }

        WorkspaceMembership? membership = null;
        string? tenantName = null;

        if (user.Role?.IsSuperAdmin == true)
        {
            // Super admins own no workspace — unchanged, identity-level status.
            if (user.ApprovalStatus == ApprovalStatus.Pending)
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.PendingApproval,
                    new LoginResponse { Status = "pending" }
                );

            if (user.ApprovalStatus == ApprovalStatus.Rejected)
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Rejected,
                    new LoginResponse { Status = "rejected" }
                );

            if (!user.IsActive)
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Disabled,
                    new LoginResponse { Status = "disabled" }
                );
        }
        else
        {
            // DB-11b: "live" here means LeftAt == null (ListForIdentityAsync's own filter) — a
            // strictly wider set than "candidates" below. An identity with zero rows in this list
            // has never had (or has had entirely removed) a workspace, distinct from having some
            // that are merely pending/rejected/disabled.
            var memberships = await _memberships.ListForIdentityAsync(user.Id);

            if (memberships.Count == 0)
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.NoWorkspace,
                    new LoginResponse { Status = "no-workspace" }
                );

            var candidates = memberships
                .Where(m =>
                    m.IsActive && m.ApprovalStatus == ApprovalStatus.Approved && user.IsActive
                )
                .ToList();

            if (candidates.Count == 0)
            {
                if (memberships.Any(m => m.ApprovalStatus == ApprovalStatus.Pending))
                    return Result<LoginResponse>.Failure(
                        MessageKeys.Auth.PendingApproval,
                        new LoginResponse { Status = "pending" }
                    );
                if (memberships.Any(m => m.ApprovalStatus == ApprovalStatus.Rejected))
                    return Result<LoginResponse>.Failure(
                        MessageKeys.Auth.Rejected,
                        new LoginResponse { Status = "rejected" }
                    );
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Disabled,
                    new LoginResponse { Status = "disabled" }
                );
            }

            if (candidates.Count == 1)
            {
                membership = candidates[0];
            }
            else
            {
                // Several live candidates. D11: the widget's projectKey auto-routes to the workspace
                // that owns that project, so a stakeholder never sees a picker inside a customer's
                // app. Ambiguous (0 or >1 candidate workspaces match the key) falls through to the
                // picker below, same as an unknown/absent key.
                if (!string.IsNullOrWhiteSpace(request.ProjectKey))
                {
                    var keyNormalized = request.ProjectKey.Trim().ToLower();
                    var projectOwnerIds = await _unitOfWork
                        .Repository<Project>()
                        .Query()
                        .IgnoreQueryFilters()
                        .Where(p => p.DeletedAt == null && p.Key == keyNormalized)
                        .Select(p => p.OwnerId)
                        .ToListAsync();

                    var routed = candidates.Where(c => projectOwnerIds.Contains(c.OwnerId)).ToList();
                    if (routed.Count == 1)
                        membership = routed[0];
                }

                if (membership == null)
                {
                    // DB-11b §3.1: the picker. The lockout counter is deliberately NOT reset here —
                    // same as the pending/rejected/disabled returns above, it only resets once a full
                    // session token is actually issued (below, or in SwitchWorkspaceAsync once a
                    // workspace is chosen).
                    var choices = await BuildWorkspaceChoicesAsync(candidates, user.OwnerId);
                    return Result<LoginResponse>.Failure(
                        MessageKeys.Auth.ChooseWorkspace,
                        new LoginResponse
                        {
                            Status = "choose-workspace",
                            Token = _tokenService.IssueSelection(user),
                            Workspaces = choices,
                            User = null,
                        }
                    );
                }
            }

            tenantName = await ResolveTenantNameAsync(membership.OwnerId);
        }

        // Password verified and every status gate passed: reset the lockout counter.
        await _loginLimiter.ResetAsync(emailNormalized);

        var token = _tokenService.Issue(user, membership);

        var response = new LoginResponse
        {
            Status = "ok",
            Token = token,
            User = await BuildMeAsync(user, membership, tenantName),
        };

        return Result<LoginResponse>.Success(response);
    }

    /// <summary>
    /// DB-11b: exchanges a selection token (or an ordinary full token — switching mid-session works
    /// too) for a full JWT of the chosen membership. Stateless: the old token, if any, keeps working
    /// until it expires.
    /// </summary>
    public async Task<Result<LoginResponse>> SwitchWorkspaceAsync(Guid workspaceId)
    {
        if (_currentUser.Id is not Guid publicId)
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidCredentials);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null || !identity.IsActive)
            return Result<LoginResponse>.Forbidden(MessageKeys.Auth.NotAMember);

        // Super admins own no workspace (§3.2).
        if (identity.Role?.IsSuperAdmin == true)
            return Result<LoginResponse>.Forbidden(MessageKeys.Common.Forbidden);

        var membership = await _memberships.GetMembershipAsync(identity.Id, workspaceId);
        if (
            membership == null
            || !membership.IsActive
            || membership.ApprovalStatus != ApprovalStatus.Approved
        )
            return Result<LoginResponse>.Forbidden(MessageKeys.Auth.NotAMember);

        var tenantName = await ResolveTenantNameAsync(membership.OwnerId);
        var token = _tokenService.Issue(identity, membership);

        // The lockout counter (per e-mail, R5-59 §12) is only reset once a full session token is
        // actually issued for this identity — an earlier "choose-workspace" response did not reset
        // it (see LoginAsync). Switching closes that loop for a login that started as a picker.
        await _loginLimiter.ResetAsync(EmailNormalizer.NormalizeRequired(identity.Email));

        return Result<LoginResponse>.Success(
            new LoginResponse
            {
                Status = "ok",
                Token = token,
                User = await BuildMeAsync(identity, membership, tenantName),
            }
        );
    }

    /// <summary>
    /// DB-11b: shared by LoginAsync ("ok") and SwitchWorkspaceAsync — the MeResponse plus the current
    /// workspace id and the full list of live, approved, active memberships (empty for super admins).
    /// </summary>
    private async Task<MeResponse> BuildMeAsync(User identity, WorkspaceMembership? membership, string? tenantName)
    {
        var role = membership?.Role ?? identity.Role;
        var response = UserMapper.ToMeResponse(identity, role, tenantName);
        response.WorkspaceId = membership?.OwnerId;

        if (identity.Role?.IsSuperAdmin != true)
        {
            var memberships = await _memberships.ListForIdentityAsync(identity.Id);
            var candidates = memberships
                .Where(m => m.IsActive && m.ApprovalStatus == ApprovalStatus.Approved)
                .ToList();
            response.Workspaces = await BuildWorkspaceChoicesAsync(candidates, identity.OwnerId);
        }

        return response;
    }

    /// <summary>Resolves workspace names in one batch query — same shape as TenantService.ListAsync's
    /// wsNames.</summary>
    private async Task<List<WorkspaceChoice>> BuildWorkspaceChoicesAsync(
        IReadOnlyCollection<WorkspaceMembership> memberships,
        Guid? homeOwnerId
    )
    {
        if (memberships.Count == 0)
            return new List<WorkspaceChoice>();

        var workspaceIds = memberships.Select(m => m.OwnerId).Distinct().ToList();
        var names = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .Where(w => workspaceIds.Contains(w.Id))
            .Select(w => new { w.Id, w.Name })
            .ToDictionaryAsync(w => w.Id, w => w.Name);

        return memberships
            .Select(m => new WorkspaceChoice
            {
                WorkspaceId = m.OwnerId,
                Name = names.TryGetValue(m.OwnerId, out var n) ? n : Workspace.PlaceholderName,
                RoleName = m.Role?.Name ?? string.Empty,
                IsAdmin = m.Role?.GrantsAdmin ?? false,
                IsHome = m.OwnerId == homeOwnerId,
            })
            .ToList();
    }

    public async Task<Result<LoginResponse>> LoginWithApiKeyAsync(LoginWithApiKeyRequest request)
    {
        var key = request.ApiKey.Trim();
        if (string.IsNullOrEmpty(key))
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidApiKey);

        // Matched on the SHA-256 hash, never on stored plaintext. ResolveAsync ignores query filters
        // because this runs pre-authentication, with no tenant claim to filter by.
        var apiKey = await _apiKeys.ResolveAsync(key);
        if (apiKey?.User == null)
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidApiKey);

        var user = apiKey.User;

        if (user.DeletedAt != null)
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidApiKey);

        WorkspaceMembership? membership = null;
        Role? role = user.Role;

        if (apiKey.OwnerId is Guid ownerId)
        {
            // DB-11a: keys are per membership. login-with-key lands deterministically in the key's
            // own workspace — no picker needed (agents are non-interactive).
            membership = await _memberships.GetMembershipAsync(user.Id, ownerId);
            // F6 (DB-11a review): the membership check alone is not enough — the IDENTITY can also be
            // deactivated (merge, and DB-11c's erase-that-keeps-the-row) independently of any one
            // membership's own IsActive flag. Restore the identity-level guard `main` had.
            if (
                membership == null
                || !membership.IsActive
                || membership.ApprovalStatus != ApprovalStatus.Approved
                || !user.IsActive
            )
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Disabled,
                    new LoginResponse { Status = "disabled" }
                );
            role = membership.Role;
        }
        else
        {
            // Null-owner key = super admin path — identity-level status, unchanged.
            if (user.ApprovalStatus == ApprovalStatus.Pending)
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.PendingApproval,
                    new LoginResponse { Status = "pending" }
                );

            if (user.ApprovalStatus == ApprovalStatus.Rejected)
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Rejected,
                    new LoginResponse { Status = "rejected" }
                );

            if (!user.IsActive)
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Disabled,
                    new LoginResponse { Status = "disabled" }
                );
        }

        var token = _tokenService.Issue(user, membership, apiKey.Scopes);

        // Best-effort usage stamp; throttled to once a minute inside the service.
        await _apiKeys.TouchLastUsedAsync(apiKey.Id);

        var tenantName = membership != null ? await ResolveTenantNameAsync(membership.OwnerId) : null;

        return Result<LoginResponse>.Success(
            new LoginResponse
            {
                Status = "ok",
                Token = token,
                User = UserMapper.ToMeResponse(user, role, tenantName),
            }
        );
    }

    public async Task<Result> RegisterAsync(RegisterRequest request)
    {
        var emailNormalized = EmailNormalizer.NormalizeRequired(request.Email);

        // 1. Resolve the project by key to determine the tenant owner.
        //    Anonymous path → no tenant claim → global query filter hides tenant rows.
        //    We must bypass with IgnoreQueryFilters() and scope manually.
        var projectKeyNormalized = request.ProjectKey.Trim().ToLower();
        // M15: project keys are unique only per (key, owner_id), so the same key can exist under more
        // than one tenant. A bare FirstOrDefault would bind the new account to an ARBITRARY tenant
        // (cross-tenant mis-routing). Fetch up to two matches and REFUSE when the key is ambiguous
        // rather than silently guessing — a deterministic, non-leaky failure.
        var projectMatches = await _unitOfWork
            .Repository<Project>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(p => p.DeletedAt == null && p.Key == projectKeyNormalized)
            .Select(p => new { p.OwnerId })
            .Take(2)
            .ToListAsync();

        if (projectMatches.Count == 0)
            return Result.Failure(MessageKeys.Project.NotFound);
        if (projectMatches.Count > 1)
            return Result.Conflict(MessageKeys.Project.KeyAmbiguous);

        // DB-11a: a workspace_memberships row always belongs to a real workspace (OwnerId NOT
        // NULL) — a null-owner (global) project has no workspace to join a stakeholder into.
        if (projectMatches[0].OwnerId is not Guid projectOwnerId)
            return Result.Failure(MessageKeys.Project.NotFound);

        // 2. Role must exist, be active, NON-admin, and belong to this tenant (or be a global role).
        //    IgnoreQueryFilters() required for the same anonymous-path reason above.
        var role = await _unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.Id == request.RoleId
                && r.DeletedAt == null
                && r.IsActive
                && !r.GrantsAdmin
                && !r.IsSuperAdmin
                && (r.OwnerId == projectOwnerId || r.OwnerId == null)
            );

        if (role == null)
            return Result.Failure(MessageKeys.Role.Invalid);

        // 3. Join-or-create (DB-11a §3.3): one identity per e-mail; a membership per workspace.
        var identity = await _memberships.FindIdentityByEmailAsync(emailNormalized);

        if (identity == null)
        {
            identity = _memberships.NewIdentity(
                emailNormalized,
                _passwordHasher.Hash(request.Password),
                request.DisplayName,
                role,
                projectOwnerId
            );
            await _unitOfWork.Repository<User>().AddAsync(identity);
            await _unitOfWork.SaveChangesAsync();

            await _memberships.JoinAsync(
                identity,
                projectOwnerId,
                role,
                ApprovalStatus.Pending,
                isActive: false,
                inviteId: null
            );
            await _unitOfWork.SaveChangesAsync();

            return Result.Success(MessageKeys.Auth.RegistrationSubmitted);
        }

        var membership = await _memberships.GetMembershipAsync(identity.Id, projectOwnerId);

        if (membership != null)
        {
            if (membership.ApprovalStatus == ApprovalStatus.Rejected)
            {
                // Re-apply ("Request again"): only the genuine account owner (correct password) may
                // re-queue — same message as today.
                if (!_passwordHasher.Verify(request.Password, identity.PasswordHash))
                    return Result.Failure(MessageKeys.Auth.InvalidCredentials);

                membership.ApprovalStatus = ApprovalStatus.Pending;
                membership.RoleId = role.Id;
                _unitOfWork.Repository<WorkspaceMembership>().Update(membership);
                await _unitOfWork.SaveChangesAsync();

                return Result.Success(MessageKeys.Auth.RegistrationSubmitted);
            }

            // Pending or Approved → already has a membership in this workspace.
            return Result.Conflict(MessageKeys.Auth.AccountExists);
        }

        // Identity exists (in some other workspace) but has no membership here yet — the
        // join-or-create generic rule: the request's password must verify against the existing
        // identity, or a PasswordlessOnly identity, before a membership is added (D4).
        if (identity.PasswordlessOnly || !_passwordHasher.Verify(request.Password, identity.PasswordHash))
            return Result.Conflict(MessageKeys.Auth.AccountExists);

        await _memberships.JoinAsync(
            identity,
            projectOwnerId,
            role,
            ApprovalStatus.Pending,
            isActive: false,
            inviteId: null
        );
        await _unitOfWork.SaveChangesAsync();

        return Result.Success(MessageKeys.Auth.RegistrationSubmitted);
    }

    public async Task<Result> RegisterAdminAsync(RegisterAdminRequest request)
    {
        // Check the global toggle — if disabled, self-signup is forbidden.
        var enabled = await _settings.GetBoolAsync(
            ISettingsService.ScopedAdminSignupEnabled,
            fallback: false
        );
        if (!enabled)
            return Result.Forbidden("Self-signup is disabled.");

        var emailNormalized = EmailNormalizer.NormalizeRequired(request.Email);

        // Resolve the global "Workspace Admin" role — anonymous path, must bypass query filter.
        var role = await _unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .FirstOrDefaultAsync(r =>
                r.DeletedAt == null && r.Name == "Workspace Admin" && r.OwnerId == null
            );

        if (role == null)
            return Result.Failure("Workspace Admin role not found.");

        // DB-11a: one identity per e-mail may administer several workspaces (D13 — the old
        // "an address that already owns a workspace cannot accept a new-workspace invite" refusal
        // is removed). A brand-new workspace is minted every time; join-or-create resolves the
        // identity.
        var workspaceId = Guid.NewGuid();
        var identity = await _memberships.FindIdentityByEmailAsync(emailNormalized);

        if (identity != null)
        {
            // D5: same as D4 — the existing account's password must verify.
            if (identity.PasswordlessOnly || !_passwordHasher.Verify(request.Password, identity.PasswordHash))
                return Result.Conflict("An account with that email already exists.");
        }
        else
        {
            identity = _memberships.NewIdentity(
                emailNormalized,
                _passwordHasher.Hash(request.Password),
                request.DisplayName,
                role,
                workspaceId
            );
            // Tenant owns itself while pending; super-admin activates later — false for a new
            // identity, as today.
            identity.IsActive = false;
            await _unitOfWork.Repository<User>().AddAsync(identity);
            await _unitOfWork.SaveChangesAsync();
        }

        await _unitOfWork.Workspaces.AddAsync(
            new Workspace
            {
                Id = workspaceId,
                Name = Workspace.PlaceholderName,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        await _memberships.JoinAsync(
            identity,
            workspaceId,
            role,
            ApprovalStatus.Pending,
            isActive: false,
            inviteId: null
        );
        await _unitOfWork.SaveChangesAsync();

        // Signup plan selector (workspace signup only). Free / none ⇒ today's flow (no subscription row;
        // effective plan resolves to Free). A paid, active, non-hidden plan ⇒ create a subscription in
        // PendingActivation; a super-admin activates it later (approval flip + IBillingProvider.Activate).
        if (request.PlanId is int planId)
        {
            var plan = await _unitOfWork
                .Repository<Plan>()
                .Query()
                .AsNoTracking()
                .FirstOrDefaultAsync(p =>
                    p.Id == planId
                    && p.DeletedAt == null
                    && p.IsActive
                    && p.DisplayState != PlanDisplayState.Hidden
                );

            // Only create a subscription for a real paid plan; Free (or an unknown/invalid id) keeps
            // the zero-write path (missing subscription ⇒ Free).
            if (plan != null && plan.Slug != "free")
            {
                await _unitOfWork
                    .Repository<Subscription>()
                    .AddAsync(
                        new Subscription
                        {
                            OwnerId = workspaceId,
                            PlanId = plan.Id,
                            Status = SubscriptionStatus.PendingActivation,
                        }
                    );
                await _unitOfWork.SaveChangesAsync();
            }
        }

        return Result.Success("Registration submitted. Your workspace is pending approval.");
    }

    public async Task<Result<MeResponse>> MeAsync()
    {
        var publicId = _currentUser.Id;

        if (publicId == null)
            return Result<MeResponse>.Failure(MessageKeys.Auth.InvalidCredentials);

        var user = await _memberships.FindIdentityByPublicIdAsync(publicId.Value);

        if (user == null)
            return Result<MeResponse>.NotFound(MessageKeys.User.NotFound);

        WorkspaceMembership? membership = null;
        string? tenantName = null;
        if (_currentUser.TenantId is Guid tenant)
        {
            membership = await _memberships.GetMembershipAsync(user.Id, tenant);
            tenantName = await ResolveTenantNameAsync(tenant);
        }

        return Result<MeResponse>.Success(await BuildMeAsync(user, membership, tenantName));
    }

    public async Task<Result<LoginResponse>> LoginWithInviteAsync(string token)
    {
        // Every failure below returns the SAME message. An anonymous caller holding a guessed token
        // must not learn which part of the guess was right — "expired" tells them the token existed.
        if (string.IsNullOrWhiteSpace(token))
            return Result<LoginResponse>.Failure(MessageKeys.Invite.LinkInvalid);

        var hash = QuickAccessTokenGenerator.Hash(token.Trim());
        var now = DateTime.UtcNow;

        // Anonymous: there is no tenant claim to scope by, so the filter is bypassed deliberately.
        // The token hash IS the authorisation.
        var link = await _unitOfWork
            .Repository<QuickAccessLink>()
            .Query()
            .IgnoreQueryFilters()
            .Where(l => l.TokenHash == hash && l.DeletedAt == null)
            .FirstOrDefaultAsync();

        if (link is null || link.RevokedAt != null || link.ExpiresAt <= now || link.OwnerId is not Guid ownerId)
            return Result<LoginResponse>.Failure(MessageKeys.Invite.LinkInvalid);

        // MaxUses 0 means unlimited within the TTL — the default. A client returning after the 12h
        // JWT expires has to be able to re-redeem, which single-use would break.
        if (link.MaxUses > 0 && link.Uses >= link.MaxUses)
            return Result<LoginResponse>.Failure(MessageKeys.Invite.LinkInvalid);

        var user = await _memberships.FindIdentityByPublicIdAsync(link.UserId);
        var membership = user == null ? null : await _memberships.GetMembershipAsync(user.Id, ownerId);

        // The account must still be the low-privilege one this link was minted for, live and
        // approved IN THIS WORKSPACE. A link whose membership was disabled, ended, or somehow
        // promoted out of QuickAccess, is not honoured.
        if (
            user is null
            || membership is null
            || !membership.IsActive
            || membership.ApprovalStatus != ApprovalStatus.Approved
            || membership.Role is not { QuickAccess: true }
            // F6 (DB-11a review): restore the identity-level guard `main` had alongside the
            // membership check — an identity can be deactivated independently of this membership.
            || !user.IsActive
        )
            return Result<LoginResponse>.Failure(MessageKeys.Invite.LinkInvalid);

        link.Uses += 1;
        link.LastUsedAt = now;
        await _unitOfWork.SaveChangesAsync();

        return Result<LoginResponse>.Success(
            new LoginResponse
            {
                Status = "ok",
                Token = _tokenService.Issue(user, membership),
                User = UserMapper.ToMeResponse(user, membership.Role, await ResolveTenantNameAsync(membership.OwnerId)),
            }
        );
    }
}
