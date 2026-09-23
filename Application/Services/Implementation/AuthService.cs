using Microsoft.EntityFrameworkCore;
using Npgsql;
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
    private readonly IAuditWriter _audit;

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
        IMembershipService memberships,
        IAuditWriter? audit = null
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
        _audit = audit ?? NoopAuditWriter.Instance;
    }

    // ── DB-12 audit helpers ─────────────────────────────────────────────────────────────────

    /// <summary>auth.login.succeeded — identity found, session issued. `owner` = the membership's
    /// workspace (null for a super admin).</summary>
    private Task AuditLoginSucceededAsync(User user, Guid? ownerId, string source) =>
        _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthLoginSucceeded,
                AuditTargets.User,
                user.PublicId.ToString(),
                ownerId,
                After: new Dictionary<string, string> { ["source"] = source }
            )
        );

    /// <summary>auth.login.failed — password path. Identity resolved (even with the wrong password)
    /// → target user/public_id (actor override); unknown e-mail → email_hash (D12.3: never the raw
    /// address).</summary>
    private Task AuditLoginFailedAsync(User? user, string emailNormalized, string reason) =>
        _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthLoginFailed,
                user != null ? AuditTargets.User : AuditTargets.EmailHash,
                user != null ? user.PublicId.ToString() : PseudonymHasher.EmailHash(emailNormalized),
                null,
                After: new Dictionary<string, string> { ["reason"] = reason },
                ActorUserIdOverride: user?.PublicId,
                ActorKindOverride: user != null ? AuditActorKind.User : null
            )
        );

    /// <summary>auth.login.failed — API-key path. A resolvable key names itself (prefix, never the
    /// key); an unresolvable one hashes the literal "api_key" (there is no e-mail on this path at
    /// all).</summary>
    private Task AuditApiKeyLoginFailedAsync(ApiKey? apiKey, string reason) =>
        _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthLoginFailed,
                apiKey != null ? AuditTargets.ApiKey : AuditTargets.EmailHash,
                apiKey != null ? apiKey.Id.ToString() : PseudonymHasher.EmailHash("api_key"),
                null,
                After: new Dictionary<string, string> { ["reason"] = reason },
                ActorUserIdOverride: apiKey?.User?.PublicId,
                ActorKindOverride: apiKey?.User != null ? AuditActorKind.User : null
            )
        );

    /// <summary>auth.login.failed — magic-link path. No identity is trustworthy until the very last
    /// check succeeds, so every failure hashes the literal "magic_link" rather than naming anyone.</summary>
    private Task AuditMagicLinkFailedAsync(string reason) =>
        _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthLoginFailed,
                AuditTargets.EmailHash,
                PseudonymHasher.EmailHash("magic_link"),
                null,
                After: new Dictionary<string, string> { ["reason"] = reason }
            )
        );

    /// <summary>auth.register.stakeholder — new identity or re-apply; `owner` = the project's
    /// workspace.</summary>
    private Task AuditRegisterStakeholderAsync(User identity, Guid ownerId, int roleId) =>
        _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthRegisterStakeholder,
                AuditTargets.User,
                identity.PublicId.ToString(),
                ownerId,
                After: new Dictionary<string, string> { ["role_id"] = roleId.ToString(), ["status"] = "pending" },
                ActorUserIdOverride: identity.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );

    public async Task<Result> RequestPasswordResetAsync(ForgotPasswordRequest request)
    {
        var emailNormalized = EmailNormalizer.NormalizeRequired(request.Email);
        User? initialUser = null;
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
            initialUser = user;

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

        // Always audited when there is something to hash — identity found or not (D12.3: hash only
        // when not). A blank/normalized-empty address has no identity and nothing to hash: skip the
        // write rather than record EmailHash("") (review finding #10).
        if (initialUser != null || emailNormalized.Length > 0)
        {
            await _audit.WriteAsync(
                new AuditEntry(
                    AuditActions.AuthPasswordResetRequested,
                    initialUser != null ? AuditTargets.User : AuditTargets.EmailHash,
                    initialUser != null
                        ? initialUser.PublicId.ToString()
                        : PseudonymHasher.EmailHash(emailNormalized),
                    null,
                    ActorUserIdOverride: initialUser?.PublicId,
                    ActorKindOverride: initialUser != null ? AuditActorKind.User : null
                )
            );
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

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthPasswordReset,
                AuditTargets.User,
                user.PublicId.ToString(),
                null,
                ActorUserIdOverride: user.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );

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

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthPasswordChanged,
                AuditTargets.User,
                user.PublicId.ToString(),
                _currentUser.TenantId
            )
        );

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

    /// <summary>
    /// DB-11d §3.2 — step 1: password-confirmed request to change the caller's e-mail. Sends a
    /// scoped confirmation link to the NEW address and a notice to the OLD one; nothing is written
    /// to the database until POST /api/auth/confirm-email-change redeems the token.
    /// </summary>
    public async Task<Result> RequestEmailChangeAsync(ChangeEmailRequest request)
    {
        if (_currentUser.Id is not Guid publicId)
            return Result.Failure(MessageKeys.Auth.InvalidCredentials);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        // §3.1 exclusions, super admin first: the seeder would re-create the old address on boot
        // (AdminSeeder re-finds the super admin by ADMIN__EMAIL every boot).
        if (identity.Role?.IsSuperAdmin == true)
            return Result.Forbidden(MessageKeys.User.ChangeEmailSuperAdmin);

        // Magic-link (passwordless) identities have no password to confirm a change with — D14b
        // defers this to an admin re-invite.
        if (identity.PasswordlessOnly)
            return Result.Failure(MessageKeys.User.ChangeEmailNeedsPassword);

        if (!_passwordHasher.Verify(request.CurrentPassword, identity.PasswordHash))
            return Result.Failure(MessageKeys.User.CurrentPasswordIncorrect);

        var newEmail = EmailNormalizer.NormalizeRequired(request.NewEmail);
        if (newEmail == identity.Email)
            return Result.Failure(MessageKeys.User.EmailUnchanged);

        // D14 — never merge: a courtesy check for the message; ux_users_email_live (R14) is the
        // authority for case variants this app-level check might miss.
        if (await _memberships.FindIdentityByEmailAsync(newEmail) is not null)
            return Result.Conflict(MessageKeys.User.EmailTaken);

        var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var token = _resetTokens.CreateScoped(
            identity.PublicId,
            identity.SecurityStamp,
            TokenPurposes.ChangeEmail,
            newEmail
        );
        var link = $"{brand.Urls.App.TrimEnd('/')}/confirm-email?token={Uri.EscapeDataString(token)}";
        var workspaceName = await WorkspaceNameResolver.ResolveForEmailAsync(_unitOfWork, identity.OwnerId);
        var workspaceLine =
            workspaceName != null
                ? $@"<p style=""color:#475569;font-size:13px"">This is for your account in the <b>{System.Net.WebUtility.HtmlEncode(workspaceName)}</b> workspace.</p>"
                : null;

        // Review finding #1: the OLD-address security notice goes FIRST, and each send gets its
        // own best-effort try/catch — a failure delivering one address's e-mail (e.g. a bounce or
        // provider hiccup) must not suppress the other's.
        try
        {
            await _emailService.SendAsync(
                identity.Email,
                $"Your {brand.ProductName} e-mail address is being changed",
                BuildEmailChangeHtml(
                    "Your e-mail address is being changed",
                    $"Someone signed in to your account and asked to change its e-mail address to {System.Net.WebUtility.HtmlEncode(newEmail)}. If that was you, confirm it from the e-mail we sent there. If it was not you, change your password now — that cancels the request.",
                    null,
                    null
                )
            );
        }
        catch
        { /* best-effort; sender logs failures */
        }

        try
        {
            await _emailService.SendAsync(
                newEmail,
                $"Confirm your new {brand.ProductName} e-mail address",
                BuildEmailChangeHtml(
                    "Confirm your new e-mail address",
                    "You asked to use this address for your account. Click the link below to confirm — it expires in 30 minutes. After confirming you will be signed out everywhere and sign in again with this address. If you did not ask for this, ignore this e-mail; nothing changes.",
                    link,
                    workspaceLine
                )
            );
        }
        catch
        { /* best-effort; sender logs failures */
        }

        // DB-12 §3.6: reserved rows AuthEmailChangeRequested/AuthEmailChanged — hashes only, never
        // the raw address (D12.3). Identity-wide, not workspace-scoped (same convention as
        // IdentityErased's user-target row): OwnerId null.
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthEmailChangeRequested,
                AuditTargets.User,
                identity.PublicId.ToString(),
                null,
                Before: new Dictionary<string, string> { ["email_hash"] = PseudonymHasher.EmailHash(identity.Email) },
                After: new Dictionary<string, string> { ["email_hash"] = PseudonymHasher.EmailHash(newEmail) }
            )
        );

        return Result.Success(MessageKeys.User.EmailChangeLinkSent);
    }

    /// <summary>
    /// DB-11d §3.3 — step 2: redeems the scoped token from RequestEmailChangeAsync. Anonymous; the
    /// token is the credential. Applies the change, rotates the identity's security stamp (every
    /// session ends), and notifies the OLD address.
    /// </summary>
    public async Task<Result> ConfirmEmailChangeAsync(string token)
    {
        // One message for every failure (as EraseByTokenAsync/LoginWithInviteAsync): a guessed/
        // tampered/reused token must not learn which check failed.
        if (
            !_resetTokens.TryValidateScoped(
                token,
                TokenPurposes.ChangeEmail,
                out var publicId,
                out var stamp,
                out var payload
            )
            || string.IsNullOrEmpty(payload)
        )
            return Result.Failure(MessageKeys.User.EmailChangeLinkInvalid);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (
            identity == null
            || identity.SecurityStamp != stamp
            || identity.PasswordlessOnly
            || identity.Role?.IsSuperAdmin == true
            || !identity.IsActive
        )
            return Result.Failure(MessageKeys.User.EmailChangeLinkInvalid);

        var newEmail = EmailNormalizer.NormalizeRequired(payload);
        if (newEmail == identity.Email)
            // Stamp rotation makes a genuine re-click impossible (the token no longer validates);
            // this branch is unreachable in practice. Review finding #3: it must not return a bare
            // 200 with no audit row (strict-coverage would turn that into a 500) — treat it as the
            // same invalid-link failure as every other unreachable/tampered case.
            return Result.Failure(MessageKeys.User.EmailChangeLinkInvalid);

        // D14 again: someone may have registered the address during the 30-minute window.
        if (await _memberships.FindIdentityByEmailAsync(newEmail) is not null)
            return Result.Conflict(MessageKeys.User.EmailTaken);

        var oldEmail = identity.Email;
        var oldStamp = identity.SecurityStamp;
        var oldRecipientEmail = identity.RecipientEmail;
        identity.Email = newEmail;
        identity.SecurityStamp = Guid.NewGuid();
        // Review finding #6: a demo identity's recipient_email override must not keep receiving
        // mail addressed to an account whose sign-in address has moved on.
        if (identity.RecipientEmail != null)
            identity.RecipientEmail = null;
        _unitOfWork.Repository<User>().Update(identity);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException e)
            when (e.InnerException is PostgresException { SqlState: "23505", ConstraintName: "ux_users_email_live" })
        {
            // ux_users_email_live (R14) is the authority for case variants the check above might
            // miss (InMemory tests never hit this branch — the rehearsal/production DB does).
            // Review finding #2: the constraint name is checked explicitly (never swallow an
            // unrelated 23505), and the in-memory mutation is reverted and detached so the tracked
            // entity is not left Modified with a change that was never persisted.
            identity.Email = oldEmail;
            identity.SecurityStamp = oldStamp;
            identity.RecipientEmail = oldRecipientEmail;
            _unitOfWork.ClearChangeTracker();
            return Result.Conflict(MessageKeys.User.EmailTaken);
        }

        var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
        try
        {
            await _emailService.SendAsync(
                oldEmail,
                $"Your {brand.ProductName} e-mail address was changed",
                BuildEmailChangeHtml(
                    "Your e-mail address was changed",
                    $"Your account's e-mail address is now {System.Net.WebUtility.HtmlEncode(newEmail)}. If you did not do this, contact your workspace admin immediately.",
                    null,
                    null
                )
            );
        }
        catch
        { /* best-effort; sender logs failures */
        }

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthEmailChanged,
                AuditTargets.User,
                identity.PublicId.ToString(),
                null,
                Before: new Dictionary<string, string> { ["email_hash"] = PseudonymHasher.EmailHash(oldEmail) },
                After: new Dictionary<string, string> { ["email_hash"] = PseudonymHasher.EmailHash(newEmail) },
                // Anonymous path — no ICurrentUser.Id — so the actor is forced explicitly (same
                // review-finding-#8 convention as IdentityEraseService's confirm-erase path).
                ActorUserIdOverride: identity.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );

        return Result.Success(MessageKeys.User.EmailChanged);
    }

    // workspaceLine is raw HTML (already encoded internally) or null to omit it; link null omits
    // the call-to-action paragraph (the two notice-only e-mails have nothing to click).
    private static string BuildEmailChangeHtml(
        string heading,
        string paragraph,
        string? link,
        string? workspaceLine
    )
    {
        // Review finding #8: the token itself is already Uri.EscapeDataString-encoded, but the
        // link as a whole (scheme/host from branding config) is still untrusted enough to encode
        // before it lands inside an href attribute.
        var linkHtml =
            link != null
                ? $@"<p><a href=""{System.Net.WebUtility.HtmlEncode(link)}"" style=""color:#2563eb"">Confirm my new e-mail &rarr;</a></p>"
                : string.Empty;
        return $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">{heading}</h2>
  <p style=""margin:0 0 16px"">{paragraph}</p>
  {linkHtml}
  {workspaceLine ?? string.Empty}
</div>";
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
            await AuditLoginFailedAsync(null, emailNormalized, "locked");
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
            {
                await AuditLoginFailedAsync(user, emailNormalized, "invalid_credentials");
                return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidCredentialsAfterMerge);
            }

            await AuditLoginFailedAsync(user, emailNormalized, "invalid_credentials");
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
            await AuditLoginFailedAsync(user, emailNormalized, "passwordless");
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidCredentials);
        }

        WorkspaceMembership? membership = null;
        string? tenantName = null;

        if (user.Role?.IsSuperAdmin == true)
        {
            // Super admins own no workspace — unchanged, identity-level status.
            if (user.ApprovalStatus == ApprovalStatus.Pending)
            {
                await AuditLoginFailedAsync(user, emailNormalized, "pending");
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.PendingApproval,
                    new LoginResponse { Status = "pending" }
                );
            }

            if (user.ApprovalStatus == ApprovalStatus.Rejected)
            {
                await AuditLoginFailedAsync(user, emailNormalized, "rejected");
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Rejected,
                    new LoginResponse { Status = "rejected" }
                );
            }

            if (!user.IsActive)
            {
                await AuditLoginFailedAsync(user, emailNormalized, "disabled");
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Disabled,
                    new LoginResponse { Status = "disabled" }
                );
            }
        }
        else
        {
            // DB-11b: "live" here means LeftAt == null (ListForIdentityAsync's own filter) — a
            // strictly wider set than "candidates" below. An identity with zero rows in this list
            // has never had (or has had entirely removed) a workspace, distinct from having some
            // that are merely pending/rejected/disabled.
            var memberships = await _memberships.ListForIdentityAsync(user.Id);

            if (memberships.Count == 0)
            {
                await AuditLoginFailedAsync(user, emailNormalized, "no_workspace");
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.NoWorkspace,
                    new LoginResponse { Status = "no-workspace" }
                );
            }

            var candidates = memberships
                .Where(m =>
                    m.IsActive && m.ApprovalStatus == ApprovalStatus.Approved && user.IsActive
                )
                .ToList();

            if (candidates.Count == 0)
            {
                if (memberships.Any(m => m.ApprovalStatus == ApprovalStatus.Pending))
                {
                    await AuditLoginFailedAsync(user, emailNormalized, "pending");
                    return Result<LoginResponse>.Failure(
                        MessageKeys.Auth.PendingApproval,
                        new LoginResponse { Status = "pending" }
                    );
                }
                if (memberships.Any(m => m.ApprovalStatus == ApprovalStatus.Rejected))
                {
                    await AuditLoginFailedAsync(user, emailNormalized, "rejected");
                    return Result<LoginResponse>.Failure(
                        MessageKeys.Auth.Rejected,
                        new LoginResponse { Status = "rejected" }
                    );
                }
                await AuditLoginFailedAsync(user, emailNormalized, "disabled");
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
                    // Landing on the picker with a CORRECT password is not a login failure (the
                    // response is 400 only because the client must choose; review finding #5).
                    var choices = await BuildWorkspaceChoicesAsync(candidates, user.OwnerId);
                    // DB-11b §3.1: returned as a SUCCESS envelope (HTTP 200), not a failure — the
                    // credentials were verified and a selection token was issued. The dashboard's
                    // generated client rejects any envelope with isSuccess==false before its caller
                    // ever sees `status`, and a bearer token has no business living in a 4xx body
                    // that proxies/error loggers may capture. The widget and CLI already branch on
                    // `status` (not HTTP status), so this changes nothing for them.
                    return Result<LoginResponse>.Success(
                        new LoginResponse
                        {
                            Status = "choose-workspace",
                            Token = _tokenService.IssueSelection(user),
                            Workspaces = choices,
                            User = null,
                        },
                        MessageKeys.Auth.ChooseWorkspace
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

        await AuditLoginSucceededAsync(user, membership?.OwnerId, "password");

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

        // F2 (DB-11b review): a session opened with an API key carries a `key_scopes` claim and is
        // scoped to exactly the membership that key was minted for (DB-11a). Letting it switch would
        // launder a scoped agent session into an unscoped human one — refuse it outright, regardless
        // of which workspace is requested.
        if (_currentUser.KeyScopes is not null)
            return Result<LoginResponse>.Forbidden(MessageKeys.Common.Forbidden);

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

        // F1 (DB-11b review): a membership row can outlive the workspace's own soft-delete — the
        // workspace itself must still be live, or a member of an already-deleted workspace could
        // switch into it and mint a token for a tenant that no longer really exists.
        var workspaceLive = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .AnyAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (!workspaceLive)
            return Result<LoginResponse>.Forbidden(MessageKeys.Auth.NotAMember);

        var tenantName = await ResolveTenantNameAsync(membership.OwnerId);
        var token = _tokenService.Issue(identity, membership);

        // F5 (DB-11b review): only a selection-token session resets the per-e-mail lockout counter
        // here — that is the one case where LoginAsync deliberately did NOT reset it yet (the picker
        // response). A caller that already holds a full token (switching mid-session) had its lockout
        // reset at that earlier login already; letting ANY valid token reset it again would let a
        // signed-in session quietly clear another concurrent lockout window for the same e-mail.
        if (_currentUser.Scope == "select_workspace")
            await _loginLimiter.ResetAsync(EmailNormalizer.NormalizeRequired(identity.Email));

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthWorkspaceSwitched,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId
            )
        );

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
        // F6 (DB-11b review): prefer the resolved membership's own workspace (the authoritative
        // answer right after a login/switch, when it names the JUST-CHOSEN workspace); fall back to
        // the caller's JWT `tenant` claim so a tenant-scoped token never reports a null WorkspaceId
        // merely because the membership lookup came back empty (e.g. MeAsync racing a membership
        // change) — the doc's §3.3 "Current workspace id (JWT tenant)" still holds either way.
        response.WorkspaceId = membership?.OwnerId ?? _currentUser.TenantId;

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
        {
            await AuditApiKeyLoginFailedAsync(null, "invalid_credentials");
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidApiKey);
        }

        // Matched on the SHA-256 hash, never on stored plaintext. ResolveAsync ignores query filters
        // because this runs pre-authentication, with no tenant claim to filter by.
        var apiKey = await _apiKeys.ResolveAsync(key);
        if (apiKey?.User == null)
        {
            await AuditApiKeyLoginFailedAsync(null, "invalid_credentials");
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidApiKey);
        }

        var user = apiKey.User;

        if (user.DeletedAt != null)
        {
            await AuditApiKeyLoginFailedAsync(apiKey, "invalid_credentials");
            return Result<LoginResponse>.Failure(MessageKeys.Auth.InvalidApiKey);
        }

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
            {
                await AuditApiKeyLoginFailedAsync(apiKey, "disabled");
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Disabled,
                    new LoginResponse { Status = "disabled" }
                );
            }
            role = membership.Role;
        }
        else
        {
            // Null-owner key = super admin path — identity-level status, unchanged.
            if (user.ApprovalStatus == ApprovalStatus.Pending)
            {
                await AuditApiKeyLoginFailedAsync(apiKey, "pending");
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.PendingApproval,
                    new LoginResponse { Status = "pending" }
                );
            }

            if (user.ApprovalStatus == ApprovalStatus.Rejected)
            {
                await AuditApiKeyLoginFailedAsync(apiKey, "rejected");
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Rejected,
                    new LoginResponse { Status = "rejected" }
                );
            }

            if (!user.IsActive)
            {
                await AuditApiKeyLoginFailedAsync(apiKey, "disabled");
                return Result<LoginResponse>.Failure(
                    MessageKeys.Auth.Disabled,
                    new LoginResponse { Status = "disabled" }
                );
            }
        }

        var token = _tokenService.Issue(user, membership, apiKey.Scopes);

        // Best-effort usage stamp; throttled to once a minute inside the service.
        await _apiKeys.TouchLastUsedAsync(apiKey.Id);

        var tenantName = membership != null ? await ResolveTenantNameAsync(membership.OwnerId) : null;

        await AuditLoginSucceededAsync(user, membership?.OwnerId, "api_key");

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

            await AuditRegisterStakeholderAsync(identity, projectOwnerId, role.Id);
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

                await AuditRegisterStakeholderAsync(identity, projectOwnerId, role.Id);
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

        await AuditRegisterStakeholderAsync(identity, projectOwnerId, role.Id);
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

        // The Workspace row must exist before any row that references it via a workspace-id FK
        // (users.owner_id ⇒ fk_users_workspaces_owner_id, memberships, etc.) — save it first.
        await _unitOfWork.Workspaces.AddAsync(
            new Workspace
            {
                Id = workspaceId,
                Name = Workspace.PlaceholderName,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        await _unitOfWork.SaveChangesAsync();

        if (identity == null)
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
        int? subscribedPlanId = null;
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
                subscribedPlanId = plan.Id;
            }
        }

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthRegisterAdmin,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: subscribedPlanId is int spid
                    ? new Dictionary<string, string> { ["plan_id"] = spid.ToString() }
                    : null,
                ActorUserIdOverride: identity.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceCreated,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["source"] = "self_serve" },
                ActorUserIdOverride: identity.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );

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
        {
            await AuditMagicLinkFailedAsync("invalid_credentials");
            return Result<LoginResponse>.Failure(MessageKeys.Invite.LinkInvalid);
        }

        // MaxUses 0 means unlimited within the TTL — the default. A client returning after the 12h
        // JWT expires has to be able to re-redeem, which single-use would break.
        if (link.MaxUses > 0 && link.Uses >= link.MaxUses)
        {
            await AuditMagicLinkFailedAsync("revoked_key");
            return Result<LoginResponse>.Failure(MessageKeys.Invite.LinkInvalid);
        }

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
        {
            await AuditMagicLinkFailedAsync("disabled");
            return Result<LoginResponse>.Failure(MessageKeys.Invite.LinkInvalid);
        }

        link.Uses += 1;
        link.LastUsedAt = now;
        await _unitOfWork.SaveChangesAsync();

        await AuditLoginSucceededAsync(user, membership.OwnerId, "magic_link");

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
