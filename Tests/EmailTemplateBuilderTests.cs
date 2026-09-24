using Pointer.Application.Common.Email;

namespace Pointer.Tests;

/// <summary>
/// EmailTemplateBuilder: every template renders into the shared layout, HTML-encodes user input
/// exactly once, and honours the e-mail contract invariants (workspace-keyword omission, the
/// password-change phrasing, no "Password:" line in invites, no emoji, brand colour plumbing).
/// </summary>
public class EmailTemplateBuilderTests
{
    private const string Link = "https://app.example.com/reset?token=abc.def-ghi_jkl";

    [Fact]
    public void Layout_Shell_RendersCardFooterAndDoctype()
    {
        var html = EmailTemplateBuilder.VerifyEmail(Link, "user@acme.com", "Pointer");

        Assert.StartsWith("<!DOCTYPE html>", html);
        Assert.Contains("<html lang=\"en\">", html);
        Assert.Contains("max-width:580px", html);
        Assert.Contains("background-color:#ffffff", html);
        Assert.Contains("height:4px", html);
        Assert.Contains("Sent by <strong>Pointer</strong>", html);
        // The shell must stay workspace-agnostic boilerplate.
        Assert.DoesNotContain("workspace", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Layout_Wrap_DefaultLang_IsEnglishByteForByte()
    {
        var contentHtml = "<p>hello</p>";
        var withoutLang = EmailLayout.Wrap(contentHtml, "Pointer");
        var withExplicitEn = EmailLayout.Wrap(contentHtml, "Pointer", lang: "en");

        Assert.Equal(withoutLang, withExplicitEn);
        Assert.Contains("<html lang=\"en\">", withoutLang);
        Assert.DoesNotContain("dir=\"rtl\"", withoutLang);
    }

    [Fact]
    public void Layout_Wrap_ArabicLang_RendersRtlShell()
    {
        var html = EmailLayout.Wrap("<p>مرحبا</p>", "Pointer", lang: "ar");

        Assert.Contains("<html lang=\"ar\" dir=\"rtl\">", html);
        Assert.Contains("class=\"email-content\" dir=\"rtl\"", html);
        Assert.Contains("text-align:right;", html);
        // The card shell itself is unchanged — only lang/dir and text alignment differ.
        Assert.Contains("max-width:580px", html);
    }

    [Fact]
    public void Layout_DefaultsToBrandBlue_WhenNoPrimaryColorGiven()
    {
        var html = EmailTemplateBuilder.VerifyEmail(Link, "user@acme.com", "Pointer");

        Assert.Contains(EmailLayout.DefaultBrandColor, html);
        Assert.Contains($"background-color:{EmailLayout.DefaultBrandColor};", html);
    }

    [Fact]
    public void Layout_UsesPrimaryColor_ForAccentBarAndButton()
    {
        var html = EmailTemplateBuilder.VerifyEmail(
            Link,
            "user@acme.com",
            "Pointer",
            primaryColor: "#0ea5e9"
        );

        Assert.Contains("background-color:#0ea5e9;", html);
        Assert.DoesNotContain(EmailLayout.DefaultBrandColor, html);
    }

    [Fact]
    public void Button_PreservesTokenLinkInCleanHref()
    {
        var html = EmailTemplateBuilder.VerifyEmail(Link, "user@acme.com", "Pointer");

        Assert.Contains($"<a href=\"{Link}\" target=\"_blank\"", html);
    }

    // ── HTML encoding ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("<b>Acme</b>", "&lt;b&gt;Acme&lt;/b&gt;")]
    public void WorkspaceName_IsHtmlEncoded(string raw, string encoded)
    {
        var html = EmailTemplateBuilder.PasswordReset(Link, "Pointer", workspaceName: raw);

        Assert.Contains(encoded, html);
        Assert.DoesNotContain($"<b>Acme</b> workspace", html);
    }

    [Fact]
    public void EmailAddress_IsHtmlEncoded()
    {
        var html = EmailTemplateBuilder.VerifyEmail(Link, "user+\"<script>\"@acme.com", "Pointer");

        Assert.Contains(System.Net.WebUtility.HtmlEncode("user+\"<script>\"@acme.com"), html);
        Assert.DoesNotContain("<script>", html);
    }

    [Fact]
    public void DisplayName_IsHtmlEncoded()
    {
        var html = EmailTemplateBuilder.PasswordChanged(
            "<i>Ada</i>",
            "Pointer",
            workspaceName: "Acme"
        );

        Assert.Contains("&lt;i&gt;Ada&lt;/i&gt;", html);
        Assert.DoesNotContain("<i>Ada</i>", html);
    }

    [Fact]
    public void ProjectName_IsHtmlEncoded()
    {
        var html = EmailTemplateBuilder.QuickAccessInvite(
            "https://client.example.com/?pointer_invite=tok",
            "client@acme.com",
            "Pointer",
            "<b>Marketing</b> site"
        );

        Assert.Contains("&lt;b&gt;Marketing&lt;/b&gt;", html);
        Assert.DoesNotContain("<b>Marketing</b>", html);
    }

    [Fact]
    public void ImpersonationReason_IsHtmlEncoded()
    {
        var html = EmailTemplateBuilder.ImpersonationNotice(
            "Ada",
            "Pointer",
            "Acme",
            DateTime.Parse("2026-09-24T10:00:00Z").ToUniversalTime(),
            15,
            "<em>support ticket</em>",
            appUrl: null
        );

        Assert.Contains("&lt;em&gt;support ticket&lt;/em&gt;", html);
        Assert.DoesNotContain("<em>support ticket</em>", html);
    }

    // ── Workspace-keyword invariant ────────────────────────────────────────────────────────

    [Fact]
    public void Templates_WithoutWorkspaceName_NeverMentionWorkspace()
    {
        var subjects = new[]
        {
            EmailTemplateBuilder.VerifyEmail(Link, "user@acme.com", "Pointer"),
            EmailTemplateBuilder.PasswordReset(Link, "Pointer"),
            EmailTemplateBuilder.EmailChangeNoticeOld("new@acme.com", "Pointer"),
            EmailTemplateBuilder.EmailChangeConfirmNew(Link, "Pointer"),
            EmailTemplateBuilder.WorkspaceInvite(Link, null, "Pointer", DateTime.UtcNow),
            EmailTemplateBuilder.QuickAccessInvite(
                "https://client.example.com/?pointer_invite=tok",
                "client@acme.com",
                "Pointer",
                "Site"
            ),
            EmailTemplateBuilder.UserApproved(
                "user@acme.com",
                "Pointer",
                "https://app.example.com"
            ),
            EmailTemplateBuilder.UserRejected("user@acme.com", "Pointer"),
            EmailTemplateBuilder.SuggestionReview("Site", null, "Pointer"),
        };

        foreach (var html in subjects)
            Assert.DoesNotContain("workspace", html, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WorkspaceInvite_NamedWorkspace_MentionsIt()
    {
        var html = EmailTemplateBuilder.WorkspaceInvite(
            Link,
            "Developer",
            "Pointer",
            DateTime.UtcNow,
            workspaceName: "Acme Inc"
        );

        Assert.Contains(
            "join the <strong>Acme Inc</strong> workspace as <strong>Developer</strong>",
            html
        );
    }

    [Fact]
    public void WorkspaceInvite_NewWorkspace_ExplainsPasswordChoice()
    {
        var html = EmailTemplateBuilder.WorkspaceInvite(
            Link,
            null,
            "Pointer",
            DateTime.UtcNow,
            isNewWorkspace: true
        );

        Assert.Contains("invited to create a workspace", html);
        Assert.Contains("choose your own password", html);
    }

    // ── Password-change phrasing ───────────────────────────────────────────────────────────

    [Fact]
    public void PasswordChanged_ContainsExactPhrasing()
    {
        var html = EmailTemplateBuilder.PasswordChanged("Ada", "Pointer", workspaceName: null);

        Assert.Contains("account password was just changed", html);
        Assert.Contains("signed out of all devices", html);
    }

    [Fact]
    public void PasswordChanged_NamedWorkspace_InsertsClause()
    {
        var html = EmailTemplateBuilder.PasswordChanged(
            "Ada",
            "Pointer",
            workspaceName: "Acme Inc"
        );

        Assert.Contains(
            "account password in the <strong>Acme Inc</strong> workspace was just changed",
            html
        );
    }

    // ── Invite hygiene ─────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void InviteEmails_NeverContainPasswordLine(bool withWorkspace)
    {
        var html = EmailTemplateBuilder.WorkspaceInvite(
            Link,
            "Developer",
            "Pointer",
            DateTime.UtcNow,
            workspaceName: withWorkspace ? "Acme Inc" : null
        );

        Assert.DoesNotContain("Password:", html);
    }

    [Fact]
    public void QuickAccessInvite_NeverContainsPasswordLine()
    {
        var html = EmailTemplateBuilder.QuickAccessInvite(
            "https://client.example.com/?pointer_invite=tok",
            "client@acme.com",
            "Pointer",
            "Site",
            extensionStoreUrl: "https://chromewebstore.google.com/pointer"
        );

        Assert.DoesNotContain("Password:", html);
        Assert.Contains("pointer_invite=tok", html);
        Assert.Contains("Get the Chrome extension", html);
    }

    [Fact]
    public void QuickAccessInvite_OmitsExtensionLink_WhenNoStoreUrl()
    {
        var html = EmailTemplateBuilder.QuickAccessInvite(
            "https://client.example.com/?pointer_invite=tok",
            "client@acme.com",
            "Pointer",
            "Site"
        );

        Assert.DoesNotContain("Get the Chrome extension", html);
    }

    // ── Enterprise tone ────────────────────────────────────────────────────────────────────

    [Fact]
    public void Templates_ContainNoEmoji()
    {
        var all = new[]
        {
            EmailTemplateBuilder.VerifyEmail(Link, "user@acme.com", "Pointer"),
            EmailTemplateBuilder.PasswordReset(Link, "Pointer", "Acme"),
            EmailTemplateBuilder.PasswordChanged("Ada", "Pointer", "Acme"),
            EmailTemplateBuilder.EmailChangeNoticeOld("new@acme.com", "Pointer"),
            EmailTemplateBuilder.EmailChangeConfirmNew(Link, "Pointer", "Acme"),
            EmailTemplateBuilder.EmailChangeCompletedOld("new@acme.com", "Pointer"),
            EmailTemplateBuilder.WorkspaceInvite(Link, "Dev", "Pointer", DateTime.UtcNow),
            EmailTemplateBuilder.QuickAccessInvite(
                "https://c.example.com?t=1",
                "c@a.com",
                "P",
                "S"
            ),
            EmailTemplateBuilder.UserApproved(
                "user@acme.com",
                "Pointer",
                "https://app.example.com"
            ),
            EmailTemplateBuilder.UserRejected("user@acme.com", "Pointer"),
            EmailTemplateBuilder.DemoReady(
                "demo-1234@demo.invalid",
                "pw",
                "demo-1234",
                "https://demo.example.com",
                DateTime.UtcNow.AddDays(2),
                "Pointer"
            ),
            EmailTemplateBuilder.DemoExpiryWarning(
                "Demo Workspace",
                DateTime.UtcNow.AddHours(2),
                "Pointer",
                "https://app.example.com",
                48,
                canExtend: true
            ),
            EmailTemplateBuilder.AccountEraseConfirm(Link, "Pointer"),
            EmailTemplateBuilder.SuggestionReview("Site", "Acme", "Pointer"),
            EmailTemplateBuilder.ImpersonationNotice(
                "Ada",
                "Pointer",
                "Acme",
                DateTime.UtcNow,
                15,
                "ticket"
            ),
        };

        foreach (var html in all)
            Assert.DoesNotContain("🐕", html);
    }

    // ── Template-specific content ──────────────────────────────────────────────────────────

    [Fact]
    public void VerifyEmail_BodyAndDisclaimer()
    {
        var html = EmailTemplateBuilder.VerifyEmail(Link, "user@acme.com", "Pointer");

        Assert.Contains("Verify your email address", html);
        Assert.Contains("<strong>user@acme.com</strong> is yours to unlock admin actions", html);
        Assert.Contains("expires in 30 minutes", html);
        Assert.Contains("Verify email address &rarr;", html);
        Assert.Contains("If you did not sign up, ignore this email.", html);
    }

    [Fact]
    public void PasswordReset_CalloutAndButton()
    {
        var html = EmailTemplateBuilder.PasswordReset(Link, "Pointer", workspaceName: "Acme");

        Assert.Contains("This is for your account in the <strong>Acme</strong> workspace.", html);
        Assert.Contains("Reset password &rarr;", html);
        Assert.Contains("safely ignore this email", html);
    }

    [Fact]
    public void EmailChangeConfirmNew_ButtonAndDisclaimer()
    {
        var html = EmailTemplateBuilder.EmailChangeConfirmNew(Link, "Pointer");

        Assert.Contains("Confirm my new email &rarr;", html);
        Assert.Contains("If you did not ask for this, ignore this email; nothing changes.", html);
    }

    [Fact]
    public void UserApproved_ButtonSignsIntoProduct()
    {
        var html = EmailTemplateBuilder.UserApproved(
            "user@acme.com",
            "Pointer",
            "https://app.example.com"
        );

        Assert.Contains("Sign in to Pointer &rarr;", html);
        Assert.Contains("href=\"https://app.example.com\"", html);
    }

    [Fact]
    public void DemoReady_ShowsCredentialsAndEncodedEmbedSnippet()
    {
        var html = EmailTemplateBuilder.DemoReady(
            "demo-1234@demo.invalid",
            "s3cret-pw",
            "demo-1234",
            "https://demo.example.com",
            DateTime.Parse("2026-09-26T12:00:00Z").ToUniversalTime(),
            "Pointer",
            workspaceName: "Demo Workspace"
        );

        Assert.Contains("s3cret-pw", html);
        Assert.Contains("demo-1234", html);
        Assert.Contains("Demo Workspace", html);
        Assert.Contains("2026-09-26 12:00 UTC", html);
        // The embed snippet is displayed as text, never as live markup.
        Assert.Contains(
            "&lt;script src=&quot;https://demo.example.com/widget.js&quot; defer&gt;&lt;/script&gt;",
            html
        );
        Assert.Contains(
            "&lt;pointer-feedback project=&quot;demo-1234&quot; server=&quot;https://demo.example.com&quot;&gt;&lt;/pointer-feedback&gt;",
            html
        );
        Assert.DoesNotContain("<pointer-feedback", html, StringComparison.Ordinal);
    }

    [Fact]
    public void DemoExpiryWarning_IncludesKeepAndExtendSentences()
    {
        var html = EmailTemplateBuilder.DemoExpiryWarning(
            "Demo Workspace",
            DateTime.UtcNow.AddHours(2),
            "Pointer",
            "https://app.example.com",
            48,
            canExtend: true
        );

        Assert.Contains("Keep this workspace", html);
        Assert.Contains("<strong>Extend once</strong> adds 48 hours", html);
    }

    [Fact]
    public void DemoExpiryWarning_CannotExtend_OmitsExtendSentence()
    {
        var html = EmailTemplateBuilder.DemoExpiryWarning(
            "Demo Workspace",
            DateTime.UtcNow.AddHours(2),
            "Pointer",
            "https://app.example.com",
            48,
            canExtend: false
        );

        Assert.DoesNotContain("Extend once", html);
    }

    [Fact]
    public void AccountEraseConfirm_ButtonAndDeletedUserNote()
    {
        var html = EmailTemplateBuilder.AccountEraseConfirm(Link, "Pointer");

        Assert.Contains("Delete my account &rarr;", html);
        Assert.Contains("&quot;Deleted user&quot;", html);
        Assert.Contains("nothing happens", html);
    }

    [Fact]
    public void SuggestionReview_CalloutOnlyWithWorkspace()
    {
        var with = EmailTemplateBuilder.SuggestionReview("Site", "Acme", "Pointer");
        Assert.Contains("Workspace: <strong>Acme</strong>", with);

        var without = EmailTemplateBuilder.SuggestionReview("Site", null, "Pointer");
        Assert.DoesNotContain("Workspace:", without);
    }

    [Fact]
    public void ImpersonationNotice_BodyReasonAndSecurityLogLine()
    {
        var startedAt = DateTime.Parse("2026-09-24T10:00:00Z").ToUniversalTime();
        var html = EmailTemplateBuilder.ImpersonationNotice(
            "Ada",
            "Pointer",
            "Acme",
            startedAt,
            15,
            "investigating a support ticket"
        );

        Assert.Contains("read-only view of the <strong>Acme</strong> workspace", html);
        Assert.Contains(startedAt.ToString("u").TrimEnd('Z'), html);
        Assert.Contains("for up to 15 minutes", html);
        Assert.Contains("Reason given: <em>investigating a support ticket</em>", html);
        Assert.Contains("Settings &rarr; Security log", html);
    }
}
