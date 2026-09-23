using Pointer.Application.DTOs.Demo;
using Pointer.Application.Response;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Interfaces;

public interface IDemoService
{
    /// <summary>
    /// Provisions an ephemeral demo tenant and emails the credentials to <paramref name="recipientEmail"/>
    /// (email-gated to curb fake requests). Enforces a per-email daily limit + the global active cap.
    /// </summary>
    Task<Result<DemoSessionResponse>> ProvisionAsync(string serverUrl, string recipientEmail);

    /// <summary>
    /// Converts an ephemeral demo user (IsDemo=true) into a permanent registered account in place,
    /// on the CALLER'S SESSION workspace (<paramref name="workspaceId"/> — never "the one demo the
    /// identity belongs to": an identity may administer more than one demo, DB-17 §3.3). All tenant
    /// data (projects, comments) is preserved. Returns a fresh JWT + MeResponse on success.
    /// </summary>
    Task<Result<UpgradeDemoResponse>> UpgradeAsync(
        Guid callerPublicId,
        Guid workspaceId,
        UpgradeDemoRequest request
    );

    /// <summary>DB-17 §3.5. Sends the one T-2h reminder to each live demo whose expiry is within warnWindow and not yet warned; stamps
    /// workspaces.demo_expiry_warned_at whether or not the send succeeded (one attempt, D17.6). Returns the number of workspaces stamped.</summary>
    Task<int> WarnExpiringAsync(DateTime nowUtc, TimeSpan warnWindow);

    /// <summary>DB-17 §3.6. Self-service one-time extension by the demo's Workspace Admin (POST /api/demo/extend); workspaceId = the session's tenant.</summary>
    Task<Result<DemoStatusResponse>> ExtendAsync(Guid callerPublicId, Guid workspaceId);

    /// <summary>DB-17 §3.4. Clears the TTL of converted workspaces this identity administers (no-op unless Demo:ConvertRequiresVerification was on at convert time).</summary>
    Task OnEmailVerifiedAsync(User identity);

    /// <summary>DB-17 §3.7. Deletes demo_email_* throttle rows older than 2 days (the raw-address legacy rows included).</summary>
    Task<int> SweepThrottleRowsAsync(DateTime nowUtc);
}
