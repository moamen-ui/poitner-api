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
    int BackupDays = 30
);

/// <summary>
/// DB-18 §3.7. Builds the five workspace-lifecycle e-mails (en + ar, branding-aware). Wrapper copies
/// <c>IdentityEraseService.cs</c>'s inline-HTML styling; the Arabic body is wrapped
/// <c>&lt;div dir="rtl" lang="ar"&gt;</c>. Workspace/display names are HTML-encoded — they are
/// user-controlled. Every existing e-mail in this codebase is English-only; this is the first
/// en/ar split (see <c>PreferencesService.cs</c> for the "ar"|"en"|null <c>Language</c> values it reads).
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
        var workspace = System.Net.WebUtility.HtmlEncode(model.WorkspaceName);
        var product = System.Net.WebUtility.HtmlEncode(model.ProductName);
        var name = System.Net.WebUtility.HtmlEncode(
            model.ActorName ?? (isAr ? "مشغّل المنصة" : "the platform operator")
        );
        var date = model.ScheduledFor is DateTime dt ? $"{dt:yyyy-MM-dd HH:mm} UTC" : string.Empty;
        var app = model.AppUrl.TrimEnd('/');
        var link = model.Link ?? string.Empty;

        return kind switch
        {
            WorkspaceLifecycleEmailKind.ConfirmDeletion => BuildConfirmDeletion(
                isAr,
                workspace,
                product,
                link,
                model.GraceDays
            ),
            WorkspaceLifecycleEmailKind.Scheduled => BuildScheduled(
                isAr,
                workspace,
                name,
                date,
                app
            ),
            WorkspaceLifecycleEmailKind.Reminder => BuildReminder(isAr, workspace, date, app),
            WorkspaceLifecycleEmailKind.Deleted => BuildDeleted(
                isAr,
                workspace,
                date,
                model.BackupDays
            ),
            WorkspaceLifecycleEmailKind.Cancelled => BuildCancelled(
                isAr,
                workspace,
                name,
                model.StillPaused
            ),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };
    }

    private static string Wrap(bool isAr, string innerHtml) =>
        isAr
            ? $@"<div dir=""rtl"" lang=""ar"" style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6;text-align:right"">
{innerHtml}
</div>"
            : $@"<div style=""font-family:system-ui,sans-serif;color:#0f172a;line-height:1.6"">
{innerHtml}
</div>";

    private static (string, string) BuildConfirmDeletion(
        bool isAr,
        string workspace,
        string product,
        string link,
        int graceDays
    )
    {
        if (isAr)
        {
            var subject = $"تأكيد حذف مساحة العمل {workspace}";
            var html = Wrap(
                isAr,
                $@"<p>طلبتَ حذف مساحة العمل <strong>{workspace}</strong> على {product}. للتأكيد، افتح الرابط أدناه وأدخل كلمة المرور. تنتهي صلاحية الرابط خلال 30 دقيقة ويعمل مرة واحدة فقط.</p>
<p><a href=""{link}"" style=""color:#2563eb"">مراجعة الحذف وتأكيده &larr;</a></p>
<p>قبل الحذف: يمكنك تصدير التعليقات من الإعدادات ← منطقة الخطر ← تصدير البيانات، أو إيقاف مساحة العمل مؤقتًا بدلًا من حذفها — الإيقاف المؤقت يحتفظ بكل شيء ويوقف استقبال الملاحظات الجديدة حتى تستأنفها.</p>
<p>بعد التأكيد تُحذف مساحة العمل بعد {graceDays} أيام، ويمكن لأي مسؤول في مساحة العمل إلغاء الحذف قبل ذلك.</p>
<p style=""color:#94a3b8;font-size:12px"">إذا لم تطلب ذلك فتجاهل هذه الرسالة ولن يحدث شيء، وننصحك بتغيير كلمة المرور.</p>"
            );
            return (subject, html);
        }

        var subjectEn = $"Confirm deleting the {workspace} workspace";
        var htmlEn = Wrap(
            isAr,
            $@"<p>You asked to delete the workspace <strong>{workspace}</strong> on {product}. To confirm, open the link below and enter your password. The link expires in 30 minutes and works once.</p>
<p><a href=""{link}"" style=""color:#2563eb"">Review and confirm deletion &rarr;</a></p>
<p>Before you delete: you can export your comments from Settings &rarr; Danger zone &rarr; Export data, or pause the workspace instead — pausing keeps everything and stops new feedback until you resume.</p>
<p>After you confirm, the workspace is deleted after {graceDays} days; until then any workspace admin can cancel.</p>
<p style=""color:#94a3b8;font-size:12px"">If you did not ask for this, ignore this e-mail — nothing happens — and consider changing your password.</p>"
        );
        return (subjectEn, htmlEn);
    }

    private static (string, string) BuildScheduled(
        bool isAr,
        string workspace,
        string name,
        string date,
        string app
    )
    {
        if (isAr)
        {
            var subject = $"ستُحذف مساحة العمل {workspace} في {date}";
            var html = Wrap(
                isAr,
                $@"<p>أكّد {name} حذف مساحة العمل <strong>{workspace}</strong>. ستُحذف نهائيًا في {date}، بما في ذلك المشاريع والتعليقات والردود ولقطات الشاشة والإعدادات ومفاتيح API والحسابات التي أُنشئت في مساحة العمل هذه ولا تنتمي إلى أي مساحة عمل أخرى. حتى ذلك الحين تكون مساحة العمل للقراءة فقط.</p>
<p>للاحتفاظ بها افتح {app}/settings واختر <strong>إلغاء الحذف</strong>، وللاحتفاظ بنسخة اختر <strong>تصدير البيانات</strong>.</p>"
            );
            return (subject, html);
        }

        var subjectEn = $"{workspace} will be deleted on {date}";
        var htmlEn = Wrap(
            isAr,
            $@"<p>{name} confirmed deleting the workspace <strong>{workspace}</strong>. It will be permanently deleted on {date} — projects, comments, replies, screenshots, settings, API keys, and the accounts that were created in this workspace and belong to no other workspace. Until then the workspace is read-only.</p>
<p>To keep it, open {app}/settings and choose <strong>Cancel deletion</strong>. To keep a copy, choose <strong>Export data</strong>.</p>"
        );
        return (subjectEn, htmlEn);
    }

    private static (string, string) BuildReminder(
        bool isAr,
        string workspace,
        string date,
        string app
    )
    {
        if (isAr)
        {
            var subject = $"ستُحذف مساحة العمل {workspace} خلال 24 ساعة";
            var html = Wrap(
                isAr,
                $@"<p>تذكير: ستُحذف مساحة العمل <strong>{workspace}</strong> نهائيًا في {date}. للاحتفاظ بها افتح {app}/settings واختر <strong>إلغاء الحذف</strong>. صدّر بياناتك قبل ذلك إذا أردت الاحتفاظ بنسخة.</p>"
            );
            return (subject, html);
        }

        var subjectEn = $"{workspace} will be deleted in 24 hours";
        var htmlEn = Wrap(
            isAr,
            $@"<p>Reminder: the workspace <strong>{workspace}</strong> will be permanently deleted on {date}. To keep it, open {app}/settings and choose <strong>Cancel deletion</strong>. Export your data before then if you want a copy.</p>"
        );
        return (subjectEn, htmlEn);
    }

    private static (string, string) BuildDeleted(
        bool isAr,
        string workspace,
        string date,
        int backupDays
    )
    {
        if (isAr)
        {
            var subject = $"حُذفت مساحة العمل {workspace}";
            var html = Wrap(
                isAr,
                $@"<p>حُذفت مساحة العمل <strong>{workspace}</strong> نهائيًا في {date} بناءً على طلب أحد مسؤوليها. تنتهي صلاحية النسخ المتبقية في نسخنا الاحتياطية خلال {backupDays} يومًا. لا يمكن التراجع عن هذا الإجراء.</p>"
            );
            return (subject, html);
        }

        var subjectEn = $"The {workspace} workspace has been deleted";
        var htmlEn = Wrap(
            isAr,
            $@"<p>The workspace <strong>{workspace}</strong> was permanently deleted on {date} at the request of one of its admins. Remaining copies in our backups expire within {backupDays} days. This cannot be undone.</p>"
        );
        return (subjectEn, htmlEn);
    }

    private static (string, string) BuildCancelled(
        bool isAr,
        string workspace,
        string name,
        bool stillPaused
    )
    {
        if (isAr)
        {
            var subject = $"أُلغي حذف مساحة العمل {workspace}";
            var stillPausedLine = stillPaused
                ? "<p>ما زالت مساحة العمل موقوفة مؤقتًا، ويمكن لأي مسؤول استئنافها من الإعدادات.</p>"
                : string.Empty;
            var html = Wrap(
                isAr,
                $@"<p>ألغى {name} الحذف المجدول لمساحة العمل <strong>{workspace}</strong>. لم يُحذف أي شيء.</p>
{stillPausedLine}"
            );
            return (subject, html);
        }

        var subjectEn = $"Deletion of {workspace} was cancelled";
        var stillPausedLineEn = stillPaused
            ? "<p>The workspace is still paused; an admin can resume it from Settings.</p>"
            : string.Empty;
        var htmlEn = Wrap(
            isAr,
            $@"<p>{name} cancelled the scheduled deletion of <strong>{workspace}</strong>. Nothing was deleted.</p>
{stillPausedLineEn}"
        );
        return (subjectEn, htmlEn);
    }
}
