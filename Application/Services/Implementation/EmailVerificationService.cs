using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// DB-14 §3.3. Mints and redeems the scoped, one-time e-mail-verification link on the DB-11c token
/// rail (<c>TokenPurposes.VerifyEmail</c>, payload = the normalised address the link proves).
/// </summary>
public class EmailVerificationService : IEmailVerificationService
{
    private readonly IUnitOfWork _unitOfWork;
    private readonly ICurrentUser _currentUser;
    private readonly IMembershipService _memberships;
    private readonly IResetTokenService _resetTokens;
    private readonly IEmailService _emailService;
    private readonly IBrandingService _branding;
    private readonly IMemoryCache _cache;
    private readonly ILogger<EmailVerificationService> _logger;
    private readonly IAuditWriter _audit;

    public EmailVerificationService(
        IUnitOfWork unitOfWork,
        ICurrentUser currentUser,
        IMembershipService memberships,
        IResetTokenService resetTokens,
        IEmailService emailService,
        IBrandingService branding,
        IMemoryCache cache,
        ILogger<EmailVerificationService> logger,
        IAuditWriter? audit = null
    )
    {
        _unitOfWork = unitOfWork;
        _currentUser = currentUser;
        _memberships = memberships;
        _resetTokens = resetTokens;
        _emailService = emailService;
        _branding = branding;
        _cache = cache;
        _logger = logger;
        _audit = audit ?? NoopAuditWriter.Instance;
    }

    private static string ResendKey(Guid publicId) => $"verify_sent:{publicId}";

    /// <summary>DB-14 §3.4: the gate's own 60s cache key — cleared here so a just-confirmed
    /// identity's next admin write is unlocked immediately rather than waiting out the TTL.</summary>
    private static string GateKey(Guid publicId) => $"emailverified:{publicId}";

    private static bool IsExempt(User identity) =>
        identity.EmailVerifiedAt != null
        || identity.IsDemo
        || identity.PasswordlessOnly
        || (identity.Role?.IsSuperAdmin ?? false);

    public async Task SendAsync(User identity)
    {
        if (IsExempt(identity))
            return;

        var token = _resetTokens.CreateScoped(
            identity.PublicId,
            identity.SecurityStamp,
            TokenPurposes.VerifyEmail,
            identity.Email
        );
        var brand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var link = $"{brand.Urls.App.TrimEnd('/')}/verify-email?token={Uri.EscapeDataString(token)}";

        try
        {
            var sent = await _emailService.SendAsync(
                identity.Email,
                $"Verify your {brand.ProductName} e-mail address",
                BuildVerifyEmailHtml(link, identity.Email, brand.ProductName)
            );
            if (!sent)
                _logger.LogWarning(
                    "Verification mail to {PublicId} failed: send returned false (daily cap or disabled)",
                    identity.PublicId
                );
        }
        catch (Exception ex)
        {
            // §9 step 5 watches this exact line at Warning (not Information): IEmailService is
            // capped per day, so a signup burst can leave identities unverified with a throttled
            // resend (GLM DB-14 #4). Never the address.
            _logger.LogWarning(ex, "Verification mail to {PublicId} failed: {Reason}", identity.PublicId, ex.Message);
        }

        _cache.Set(ResendKey(identity.PublicId), true, TimeSpan.FromMinutes(5));
    }

    public async Task<Result> ResendAsync()
    {
        if (_currentUser.Id is not Guid publicId)
            return Result.Failure(MessageKeys.Auth.InvalidCredentials);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null)
            return Result.NotFound(MessageKeys.User.NotFound);

        if (identity.EmailVerifiedAt != null)
            return Result.Success(MessageKeys.Auth.AlreadyVerified);

        if (identity.IsDemo || identity.PasswordlessOnly || (identity.Role?.IsSuperAdmin ?? false))
            return Result.Failure(MessageKeys.Auth.VerificationNotApplicable);

        if (_cache.TryGetValue(ResendKey(publicId), out _))
            return Result.Failure(MessageKeys.Auth.VerificationRecentlySent);

        await SendAsync(identity);
        return Result.Success(MessageKeys.Auth.VerificationSent);
    }

    public async Task<Result> ConfirmAsync(string token)
    {
        // One message for every failure (as EraseByTokenAsync/ConfirmEmailChangeAsync): a guessed/
        // tampered/reused token must not learn which check failed.
        if (
            !_resetTokens.TryValidateScoped(
                token,
                TokenPurposes.VerifyEmail,
                out var publicId,
                out var stamp,
                out var payload
            )
            || string.IsNullOrEmpty(payload)
        )
            return Result.Failure(MessageKeys.Auth.VerificationLinkInvalid);

        var identity = await _memberships.FindIdentityByPublicIdAsync(publicId);
        if (identity == null || identity.SecurityStamp != stamp)
            return Result.Failure(MessageKeys.Auth.VerificationLinkInvalid);

        if (EmailNormalizer.NormalizeRequired(payload) != identity.Email)
            return Result.Failure(MessageKeys.Auth.VerificationLinkInvalid);

        // Idempotent re-click: the stamp is NOT rotated by this action, so the link stays valid
        // until expiry by design — it grants nothing beyond "verified". Still writes the audit row
        // below (a re-confirmation is a real, harmless event) rather than returning a 200 with no
        // audit row, which AuditCoverageFilter's Audit:StrictCoverage=true (Development) would turn
        // into a 500 — same rationale as ConfirmEmailChangeAsync review finding #3.
        var alreadyVerified = identity.EmailVerifiedAt != null;
        if (!alreadyVerified)
        {
            identity.EmailVerifiedAt = DateTime.UtcNow;
            _unitOfWork.Repository<User>().Update(identity);
            await _unitOfWork.SaveChangesAsync();
            _cache.Remove(GateKey(publicId));
        }

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthEmailVerified,
                AuditTargets.User,
                identity.PublicId.ToString(),
                null,
                After: alreadyVerified
                    ? new Dictionary<string, string> { ["already_verified"] = "true" }
                    : null,
                // Anonymous path — no ICurrentUser.Id — so the actor is forced explicitly (same
                // convention as ConfirmEmailChangeAsync/EraseByTokenAsync).
                ActorUserIdOverride: identity.PublicId,
                ActorKindOverride: AuditActorKind.User
            )
        );

        return Result.Success(MessageKeys.Auth.EmailVerified);
    }

    private static string BuildVerifyEmailHtml(string link, string email, string productName)
    {
        var encodedEmail = System.Net.WebUtility.HtmlEncode(email);
        var encodedLink = System.Net.WebUtility.HtmlEncode(link);
        return $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">Verify your e-mail address</h2>
  <p style=""margin:0 0 16px"">Confirm that {encodedEmail} is yours to unlock admin actions in {productName}. The link expires in 30 minutes.</p>
  <p><a href=""{encodedLink}"" style=""color:#2563eb"">Verify my e-mail &rarr;</a></p>
  <p style=""color:#94a3b8;font-size:12px"">If you did not sign up, ignore this e-mail.</p>
</div>";
    }
}
