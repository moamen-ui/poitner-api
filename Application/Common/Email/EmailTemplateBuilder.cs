namespace Pointer.Application.Common.Email;

/// <summary>
/// Every transactional e-mail body, as one static method per template. Each method returns a
/// complete HTML document via <see cref="EmailLayout.Wrap"/>; services keep only the subject line
/// and the send.
/// </summary>
/// <remarks>
/// Workspace mentions are strictly opt-in: a template may only say "workspace" when a non-null
/// <c>workspaceName</c> was passed (or in fixed security wording that is workspace-agnostic in the
/// tests' eyes — the change-password warning). All user-derived values are HTML-encoded exactly
/// once, here.
/// </remarks>
public static class EmailTemplateBuilder
{
    // ── 1. Verify e-mail ───────────────────────────────────────────────────────────────────

    public static string VerifyEmail(
        string link,
        string email,
        string productName,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var content =
            Heading("Verify your email address")
            + Paragraph(
                $"Confirm that <strong>{EmailLayout.Html(email)}</strong> is yours to unlock admin actions in {EmailLayout.Html(productName)}. The link expires in 30 minutes."
            )
            + EmailComponents.Button("Verify email address", link, primaryColor ?? "")
            + Disclaimer("If you did not sign up, ignore this email.");

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Confirm your email address"
        );
    }

    // ── 2. Password reset ──────────────────────────────────────────────────────────────────

    public static string PasswordReset(
        string link,
        string productName,
        string? workspaceName = null,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var content =
            Heading("Reset your password")
            + Paragraph(
                "Click the button below to choose a new password. It expires in 30 minutes."
            )
            + WorkspaceCallout(
                workspaceName,
                ws => $"This is for your account in the <strong>{ws}</strong> workspace."
            )
            + EmailComponents.Button("Reset password", link, primaryColor ?? "")
            + Disclaimer("If you didn't request this, you can safely ignore this email.");

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Choose a new password"
        );
    }

    // ── 3. Password changed ────────────────────────────────────────────────────────────────

    public static string PasswordChanged(
        string displayName,
        string productName,
        string? workspaceName = null,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var workspaceClause =
            workspaceName != null
                ? $" in the <strong>{EmailLayout.Html(workspaceName)}</strong> workspace"
                : string.Empty;
        var content =
            Heading("Your password was changed")
            + Paragraph(
                $"Hi {EmailLayout.Html(displayName)}, this confirms your {EmailLayout.Html(productName)} account password{workspaceClause} was just changed. You've been signed out of all devices."
            )
            + EmailComponents.Callout(
                "If you didn't make this change, reset your password immediately and contact your workspace admin.",
                "#f59e0b"
            );

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Your password was changed"
        );
    }

    // ── 4. Email change — notice to the old address ────────────────────────────────────────

    public static string EmailChangeNoticeOld(
        string newEmail,
        string productName,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var content =
            Heading("Your email address is being changed")
            + Paragraph(
                $"Someone signed in to your account and asked to change its email address to <strong>{EmailLayout.Html(newEmail)}</strong>. If that was you, confirm it from the email we sent there."
            )
            + EmailComponents.Callout(
                "If it was not you, change your password now — that cancels the request.",
                "#f59e0b"
            );

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Your email address is being changed"
        );
    }

    // ── 5. Email change — confirmation link to the new address ─────────────────────────────

    public static string EmailChangeConfirmNew(
        string link,
        string productName,
        string? workspaceName = null,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var content =
            Heading("Confirm your new email address")
            + Paragraph(
                "You asked to use this address for your account. Click the button below to confirm — it expires in 30 minutes. After confirming you will be signed out everywhere and sign in again with this address."
            )
            + WorkspaceCallout(
                workspaceName,
                ws => $"This is for your account in the <strong>{ws}</strong> workspace."
            )
            + EmailComponents.Button("Confirm my new email", link, primaryColor ?? "")
            + Disclaimer("If you did not ask for this, ignore this email; nothing changes.");

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Confirm your new email address"
        );
    }

    // ── 6. Email change — completed notice to the old address ──────────────────────────────

    public static string EmailChangeCompletedOld(
        string newEmail,
        string productName,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var content =
            Heading("Your email address was changed")
            + Paragraph(
                $"Your account's email address is now <strong>{EmailLayout.Html(newEmail)}</strong>."
            )
            + EmailComponents.Callout(
                "If you did not do this, contact your workspace admin immediately.",
                "#f59e0b"
            );

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Your email address was changed"
        );
    }

    // ── 7. Workspace invite ────────────────────────────────────────────────────────────────

    public static string WorkspaceInvite(
        string joinUrl,
        string? roleName,
        string productName,
        DateTime expiresAtUtc,
        bool isNewWorkspace = false,
        string? workspaceName = null,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var encodedRoleName = roleName is null ? null : EmailLayout.Html(roleName);
        var roleLine =
            isNewWorkspace
                ? "You've been invited to create a workspace. Open the link below and choose your own password — nobody else ever sees it."
            : workspaceName != null && encodedRoleName != null
                ? $"You've been invited to join the <strong>{EmailLayout.Html(workspaceName)}</strong> workspace as <strong>{encodedRoleName}</strong>."
            : workspaceName != null
                ? $"You've been invited to join the <strong>{EmailLayout.Html(workspaceName)}</strong> workspace."
            : encodedRoleName != null
                ? $"You've been invited to join as <strong>{encodedRoleName}</strong>."
            : string.Empty;

        var content =
            Heading($"You're invited to {EmailLayout.Html(productName)}")
            + (roleLine.Length > 0 ? Paragraph(roleLine) : string.Empty)
            + EmailComponents.Button("Accept invite", joinUrl, primaryColor ?? "")
            + Disclaimer(
                $"This link expires on {expiresAtUtc:yyyy-MM-dd HH:mm} UTC. If you weren't expecting this, you can ignore this email."
            );

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "You're invited"
        );
    }

    // ── 8. Quick-access (stakeholder) invite ───────────────────────────────────────────────

    public static string QuickAccessInvite(
        string magicLink,
        string email,
        string productName,
        string projectName,
        string? extensionStoreUrl = null,
        string? workspaceName = null,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var encodedProjectName = EmailLayout.Html(projectName);
        var encodedEmail = EmailLayout.Html(email);

        var details = $"""
            <p style="margin:0 0 4px;"><strong>Project:</strong> {encodedProjectName}</p>
            <p style="margin:0 0 4px;"><strong>Invited:</strong> {encodedEmail}</p>
            {(
                workspaceName != null
                    ? $"<p style=\"margin:0;\"><strong>Workspace:</strong> {EmailLayout.Html(workspaceName)}</p>"
                    : string.Empty
            )}
            """;

        var extensionLink = !string.IsNullOrWhiteSpace(extensionStoreUrl)
            ? Paragraph(
                $"<a href=\"{EmailLayout.Html(extensionStoreUrl)}\" target=\"_blank\" style=\"color:{EmailLayout.NormalizeBrandColor(primaryColor)};font-weight:600;text-decoration:none;\">Get the Chrome extension &rarr;</a>"
            )
            : string.Empty;

        var content =
            Heading($"You're invited to review {EmailLayout.Html(productName)}")
            + Paragraph(
                $"You've been invited to leave feedback on <strong>{encodedProjectName}</strong>. Open the link below — it signs you in automatically, so there is no password to set or remember."
            )
            + EmailComponents.Button("Open project", magicLink, primaryColor ?? "")
            + EmailComponents.Callout(details)
            + extensionLink
            + Disclaimer(
                "Treat this link like a password — anyone with it can comment as you. If you weren't expecting this, you can ignore this email."
            );

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: $"You're invited to review {projectName}"
        );
    }

    // ── 9. User approved ───────────────────────────────────────────────────────────────────

    public static string UserApproved(
        string email,
        string productName,
        string appUrl,
        string? workspaceName = null,
        string? primaryColor = null
    )
    {
        var content =
            Heading("Your account is approved")
            + Paragraph(
                $"Your {EmailLayout.Html(productName)} account (<strong>{EmailLayout.Html(email)}</strong>) has been approved and is now active."
            )
            + WorkspaceCallout(
                workspaceName,
                ws => $"You now have access to the <strong>{ws}</strong> workspace."
            )
            + EmailComponents.Button($"Sign in to {productName}", appUrl, primaryColor ?? "");

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Your account is approved"
        );
    }

    // ── 10. User rejected ──────────────────────────────────────────────────────────────────

    public static string UserRejected(
        string email,
        string productName,
        string? workspaceName = null,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var content =
            Heading("Update on your account request")
            + Paragraph(
                $"Thanks for your interest in {EmailLayout.Html(productName)}. Unfortunately your account request for <strong>{EmailLayout.Html(email)}</strong> was not approved at this time."
            )
            + WorkspaceCallout(
                workspaceName,
                ws => $"This was for the <strong>{ws}</strong> workspace."
            );

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Update on your account request"
        );
    }

    // ── 11. Demo ready ─────────────────────────────────────────────────────────────────────

    public static string DemoReady(
        string login,
        string password,
        string projectKey,
        string serverUrl,
        DateTime expiresUtc,
        string productName,
        string? workspaceName = null,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var workspaceClause =
            workspaceName != null
                ? $", <strong>{EmailLayout.Html(workspaceName)}</strong>,"
                : string.Empty;
        var snippet =
            $"<script src=\"{serverUrl}/widget.js\" defer></script>\n"
            + $"<pointer-feedback project=\"{projectKey}\" server=\"{serverUrl}\"></pointer-feedback>";

        var content =
            Heading($"Your {EmailLayout.Html(productName)} demo is ready")
            + Paragraph(
                $"This demo workspace{workspaceClause} expires on {expiresUtc:yyyy-MM-dd HH:mm} UTC."
            )
            + EmailComponents.KeyValueTable(
                new[]
                {
                    ("Project key", projectKey, true),
                    ("Widget login", login, true),
                    ("Password", password, true),
                }
            )
            + Paragraph("Embed snippet (paste into your app's index.html):")
            + EmailComponents.CodeBlock(snippet)
            + Disclaimer("If you didn't request this, you can ignore this email.");

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Your demo is ready"
        );
    }

    // ── 12. Demo expiry warning ────────────────────────────────────────────────────────────

    public static string DemoExpiryWarning(
        string workspaceName,
        DateTime expiresUtc,
        string productName,
        string appUrl,
        int ttlHours,
        bool canExtend,
        string? primaryColor = null
    )
    {
        var encodedName = EmailLayout.Html(workspaceName);
        var encodedAppUrl = EmailLayout.Html(appUrl);
        var brandColor = EmailLayout.NormalizeBrandColor(primaryColor);
        // Only offer "Extend once" when it would actually work (the one extension is still unused).
        var extendSentence = canExtend
            ? $" Need a little more time? <strong>Extend once</strong> adds {ttlHours} hours."
            : string.Empty;

        var content =
            Heading($"Your {EmailLayout.Html(productName)} demo expires soon")
            + Paragraph(
                $"Your demo workspace, <strong>{encodedName}</strong>, expires on {expiresUtc:yyyy-MM-dd HH:mm} UTC. Everything in it — the project, its comments and screenshots — is deleted then. To keep it, open <a href=\"{encodedAppUrl}\" style=\"color:{brandColor};\">{encodedAppUrl}</a> and choose <strong>Keep this workspace</strong> (you pick your email and a password; nothing is lost).{extendSentence} If you did not start this demo, ignore this email."
            );

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Your demo expires soon"
        );
    }

    // ── 13. Account erase confirmation ─────────────────────────────────────────────────────

    public static string AccountEraseConfirm(
        string link,
        string productName,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var content =
            Heading("Confirm deleting your account")
            + Paragraph(
                "You asked to delete your account. Click the button below to confirm — it expires in 30 minutes."
            )
            + EmailComponents.Button("Delete my account", link, primaryColor ?? "")
            + EmailComponents.Callout(
                "Your feedback stays with the workspaces you commented in and is shown as &quot;Deleted user&quot;."
            )
            + Disclaimer("If you did not ask for this, ignore this email; nothing happens.");

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Confirm deleting your account"
        );
    }

    // ── 14. Suggestion submitted — admin review notice ─────────────────────────────────────

    public static string SuggestionReview(
        string projectName,
        string? workspaceName,
        string productName,
        string? appUrl = null,
        string? primaryColor = null
    )
    {
        var content =
            Heading("New predefined-prompt suggestion for review")
            + Paragraph(
                $"A stakeholder suggested a predefined prompt for project <strong>{EmailLayout.Html(projectName)}</strong>."
            )
            + WorkspaceCallout(workspaceName, ws => $"Workspace: <strong>{ws}</strong>")
            + Paragraph($"Review it in your {EmailLayout.Html(productName)} dashboard.");

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "New predefined-prompt suggestion for review"
        );
    }

    // ── 15. Impersonation notice ───────────────────────────────────────────────────────────

    public static string ImpersonationNotice(
        string adminDisplayName,
        string productName,
        string workspaceName,
        DateTime startedAt,
        int minutes,
        string reason,
        string? primaryColor = null,
        string? appUrl = null
    )
    {
        var content =
            Heading("Read-only operator session opened")
            + Paragraph(
                $"Hi {EmailLayout.Html(adminDisplayName)}, the {EmailLayout.Html(productName)} operator opened a read-only view of the <strong>{EmailLayout.Html(workspaceName)}</strong> workspace at {startedAt:u} for up to {minutes} minutes."
            )
            + EmailComponents.Callout($"Reason given: <em>{EmailLayout.Html(reason)}</em>")
            + Disclaimer(
                "This is logged in your Security log (Settings &rarr; Security log), where you will also see when it ended. If you did not expect this, reply to this email."
            );

        return EmailLayout.Wrap(
            content,
            productName,
            primaryColor,
            appUrl: appUrl,
            preheader: "Read-only operator session opened"
        );
    }

    // ── Shared fragments ───────────────────────────────────────────────────────────────────

    private static string Heading(string text) =>
        $"<h1 style=\"margin:0 0 16px;font-size:20px;line-height:1.3;font-weight:700;color:#0f172a;\">{text}</h1>";

    private static string Paragraph(string html) => $"<p style=\"margin:0 0 16px;\">{html}</p>";

    private static string Disclaimer(string html) =>
        $"<p style=\"margin:20px 0 0;font-size:13px;color:#64748b;\">{html}</p>";

    /// <summary>
    /// Renders the workspace-specific callout, or nothing at all when there is no workspace to
    /// name — a missing/placeholder name must never produce "the Workspace workspace".
    /// The name is encoded here, exactly once.
    /// </summary>
    private static string WorkspaceCallout(string? workspaceName, Func<string, string> html)
    {
        if (workspaceName is null)
            return string.Empty;
        return EmailComponents.Callout(html(EmailLayout.Html(workspaceName)));
    }
}
