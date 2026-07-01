using Pointer.Application.DTOs.Demo;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IDemoService
{
    /// <summary>
    /// Provisions an ephemeral demo tenant and emails the credentials to <paramref name="recipientEmail"/>
    /// (email-gated to curb fake requests). Enforces a per-email daily limit + the global active cap.
    /// </summary>
    Task<Result<DemoSessionResponse>> ProvisionAsync(string serverUrl, string recipientEmail);

    /// <summary>
    /// Converts an ephemeral demo user (IsDemo=true) into a permanent registered
    /// account in place. All tenant data (projects, comments) is preserved.
    /// Returns a fresh JWT + MeResponse on success.
    /// </summary>
    Task<Result<UpgradeDemoResponse>> UpgradeAsync(Guid callerPublicId, UpgradeDemoRequest request);
}
