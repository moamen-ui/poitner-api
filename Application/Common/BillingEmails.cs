using Pointer.Application.Common.Email;

namespace Pointer.Application.Common;

/// <summary>DB-20 §5 task 20. Which of the three period-job e-mails to build.</summary>
public enum BillingEmailKind
{
    /// <summary>h2 — the T-N-days renewal reminder.</summary>
    Reminder,

    /// <summary>h3 (and h1's comp-end handoff) — the period ended unpaid, grace window running.</summary>
    PastDue,

    /// <summary>h4 — the grace window elapsed; the workspace moved to Free.</summary>
    Downgraded,
}

/// <summary>DB-20 §5 task 20. Everything a billing e-mail needs.</summary>
public sealed record BillingEmailModel(
    string WorkspaceName,
    string ProductName,
    string AppUrl,
    string? BrandColor = null,
    string? LogoUrl = null
);

/// <summary>
/// DB-20 §5 task 20. Builds the three period-job e-mails (en + ar, branding-aware) on the shared
/// branded layout — same pattern as <see cref="WorkspaceLifecycleEmails"/>.
/// </summary>
public static class BillingEmails
{
    public static (string Subject, string Html) Build(
        BillingEmailKind kind,
        string? lang,
        BillingEmailModel model
    )
    {
        var isAr = lang == "ar";
        return kind switch
        {
            BillingEmailKind.Reminder => EmailTemplateBuilder.BillingRenewalReminder(
                isAr,
                model.WorkspaceName,
                model.ProductName,
                model.AppUrl,
                model.BrandColor,
                model.LogoUrl
            ),
            BillingEmailKind.PastDue => EmailTemplateBuilder.BillingPastDue(
                isAr,
                model.WorkspaceName,
                model.ProductName,
                model.AppUrl,
                model.BrandColor,
                model.LogoUrl
            ),
            BillingEmailKind.Downgraded => EmailTemplateBuilder.BillingDowngraded(
                isAr,
                model.WorkspaceName,
                model.ProductName,
                model.AppUrl,
                model.BrandColor,
                model.LogoUrl
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }
}
