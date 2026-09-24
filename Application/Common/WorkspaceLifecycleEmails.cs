using Pointer.Application.Common.Email;

namespace Pointer.Application.Common;

/// <summary>DB-18 §3.7. Which of the five workspace-lifecycle e-mails to build.</summary>
public enum WorkspaceLifecycleEmailKind
{
    /// <summary>E1 — sent to the requester only, right after RequestDeletionAsync.</summary>
    ConfirmDeletion,

    /// <summary>E2 — sent to every live admin (including the requester) after ConfirmDeletionAsync.</summary>
    Scheduled,

    /// <summary>E3 — the T-24h reminder, sent to every live admin.</summary>
    Reminder,

    /// <summary>E4 — sent to the admins captured before the delete, after ExecuteDueDeletionAsync succeeds.</summary>
    Deleted,

    /// <summary>E5 — sent to every live admin after CancelDeletionAsync / OperatorCancelDeletionAsync.</summary>
    Cancelled,
}

/// <summary>DB-18 §3.7. Everything a template needs — the caller fills only what its kind uses.</summary>
public sealed record WorkspaceLifecycleEmailModel(
    string WorkspaceName,
    string ProductName,
    string AppUrl,
    string? Link = null,
    int GraceDays = 0,
    DateTime? ScheduledFor = null,
    /// <summary>Who confirmed/cancelled — a display name, or null for an operator action (rendered
    /// as "the platform operator" / "مشغّل المنصة" — R17 spirit: the workspace never learns which one).</summary>
    string? ActorName = null,
    /// <summary>E5 only — true when the workspace is still paused after the cancel.</summary>
    bool StillPaused = false,
    int BackupDays = 30,
    /// <summary>The brand's primary colour — plumbed through to <see cref="EmailLayout.Wrap"/> /
    /// <see cref="EmailComponents.Button"/> exactly like every other transactional e-mail.</summary>
    string? BrandColor = null,
    /// <summary>The brand's logo URL — plumbed through to <see cref="EmailLayout.Wrap"/>'s header;
    /// null falls back to the typographic wordmark, same as every other template.</summary>
    string? LogoUrl = null
);

/// <summary>
/// DB-18 §3.7. Builds the five workspace-lifecycle e-mails (en + ar, branding-aware) on the shared
/// branded layout — every kind renders through <see cref="EmailTemplateBuilder"/>'s
/// <c>Workspace*</c> methods, which in turn wrap through <see cref="EmailLayout.Wrap"/> (the same
/// 580px card shell, buttons and callouts as every other transactional e-mail). Arabic renders
/// <c>lang="ar" dir="rtl"</c> end to end via <see cref="EmailLayout.Wrap"/>'s <c>lang</c> parameter.
/// Workspace/display names are HTML-encoded exactly once, inside each builder method. Every other
/// e-mail in this codebase is English-only; this is the first en/ar split (see
/// <c>PreferencesService.cs</c> for the "ar"|"en"|null <c>Language</c> values it reads).
/// </summary>
public static class WorkspaceLifecycleEmails
{
    public static (string Subject, string Html) Build(
        WorkspaceLifecycleEmailKind kind,
        string? lang,
        WorkspaceLifecycleEmailModel model
    )
    {
        var isAr = lang == "ar";
        var date = model.ScheduledFor is DateTime dt ? $"{dt:yyyy-MM-dd HH:mm} UTC" : string.Empty;

        return kind switch
        {
            WorkspaceLifecycleEmailKind.ConfirmDeletion =>
                EmailTemplateBuilder.WorkspaceConfirmDeletion(
                    isAr,
                    model.WorkspaceName,
                    model.ProductName,
                    model.AppUrl,
                    model.Link ?? string.Empty,
                    model.GraceDays,
                    model.BrandColor,
                    model.LogoUrl
                ),
            WorkspaceLifecycleEmailKind.Scheduled =>
                EmailTemplateBuilder.WorkspaceDeletionScheduled(
                    isAr,
                    model.WorkspaceName,
                    model.ActorName,
                    date,
                    model.ProductName,
                    model.AppUrl,
                    model.BrandColor,
                    model.LogoUrl
                ),
            WorkspaceLifecycleEmailKind.Reminder => EmailTemplateBuilder.WorkspaceDeletionReminder(
                isAr,
                model.WorkspaceName,
                date,
                model.ProductName,
                model.AppUrl,
                model.BrandColor,
                model.LogoUrl
            ),
            WorkspaceLifecycleEmailKind.Deleted => EmailTemplateBuilder.WorkspaceDeleted(
                isAr,
                model.WorkspaceName,
                date,
                model.BackupDays,
                model.ProductName,
                model.AppUrl,
                model.BrandColor,
                model.LogoUrl
            ),
            WorkspaceLifecycleEmailKind.Cancelled =>
                EmailTemplateBuilder.WorkspaceDeletionCancelled(
                    isAr,
                    model.WorkspaceName,
                    model.ActorName,
                    model.StillPaused,
                    model.ProductName,
                    model.AppUrl,
                    model.BrandColor,
                    model.LogoUrl
                ),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
