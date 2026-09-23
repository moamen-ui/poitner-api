using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

/// <summary>
/// DB-18 §3.4. Self-service workspace pause / delete lifecycle: export-first, pause-instead, e-mail
/// + password confirmed delete with a cancellable grace period. Session methods (Pause/Resume/
/// RequestDeletion/CancelDeletion) resolve the caller's workspace from <c>ICurrentUser</c>; the
/// anonymous token methods (Preview/Confirm/PauseInstead) resolve it from the scoped e-mailed token;
/// the operator methods act on an explicit workspace id; the job methods run with no tenant context
/// at all.
/// </summary>
public interface IWorkspaceLifecycleService
{
    Task<Result<WorkspaceResponse>> PauseAsync();
    Task<Result<WorkspaceResponse>> ResumeAsync();
    Task<Result<WorkspaceDeletionRequestResponse>> RequestDeletionAsync();
    Task<Result<WorkspaceResponse>> CancelDeletionAsync();

    /// <summary>Anonymous — the token is the credential.</summary>
    Task<Result<WorkspaceDeletionPreviewResponse>> PreviewDeletionAsync(string token);

    /// <summary>Anonymous — the token is the credential.</summary>
    Task<Result<WorkspaceDeletionScheduledResponse>> ConfirmDeletionAsync(
        ConfirmWorkspaceDeletionRequest request
    );

    /// <summary>Anonymous — the token is the credential.</summary>
    Task<Result> PauseInsteadAsync(string token);

    Task<Result> OperatorPauseAsync(Guid workspaceId);
    Task<Result> OperatorResumeAsync(Guid workspaceId);
    Task<Result> OperatorCancelDeletionAsync(Guid workspaceId);

    /// <summary>Job — sends the T-24h reminder (E3) and reschedules a missed one. No tenant context.</summary>
    Task SendDueRemindersAsync(DateTime now);

    /// <summary>Job, per item — re-checks due-ness, deletes via <c>TenantService.HardDeleteAsync</c>, e-mails E4.</summary>
    Task<Result> ExecuteDueDeletionAsync(Guid workspaceId);
}
