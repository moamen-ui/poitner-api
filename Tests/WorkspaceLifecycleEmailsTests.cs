using Pointer.Application.Common;
using Pointer.Application.Common.Email;

namespace Pointer.Tests;

/// <summary>
/// DB-18 workspace-lifecycle e-mails (ConfirmDeletion, Scheduled, Reminder, Deleted, Cancelled),
/// now rendered through the shared branded layout (<see cref="EmailLayout"/> /
/// <see cref="EmailTemplateBuilder"/>) instead of hand-rolled inline-style HTML: every kind, in
/// both languages, must still carry the layout shell, HTML-encode user-controlled values exactly
/// once, and render Arabic as <c>lang="ar" dir="rtl"</c> end to end.
/// </summary>
public class WorkspaceLifecycleEmailsTests
{
    private static readonly WorkspaceLifecycleEmailKind[] AllKinds =
    [
        WorkspaceLifecycleEmailKind.ConfirmDeletion,
        WorkspaceLifecycleEmailKind.Scheduled,
        WorkspaceLifecycleEmailKind.Reminder,
        WorkspaceLifecycleEmailKind.Deleted,
        WorkspaceLifecycleEmailKind.Cancelled,
    ];

    private static WorkspaceLifecycleEmailModel Model(string workspaceName = "Acme Inc") =>
        new(
            WorkspaceName: workspaceName,
            ProductName: "Pointer",
            AppUrl: "https://app.example.com",
            Link: "https://app.example.com/confirm-workspace-deletion?token=abc",
            GraceDays: 14,
            ScheduledFor: DateTime.Parse("2026-10-01T12:00:00Z").ToUniversalTime(),
            ActorName: "Ada Lovelace",
            StillPaused: true,
            BackupDays: 30,
            BrandColor: "#0ea5e9",
            LogoUrl: "https://app.example.com/logo.png"
        );

    // ── Layout adoption ─────────────────────────────────────────────────────────────────────

    [Theory]
    [CombinatorialLangKind]
    public void AllKinds_BothLanguages_RenderThroughSharedLayout(
        WorkspaceLifecycleEmailKind kind,
        string lang
    )
    {
        var (subject, html) = WorkspaceLifecycleEmails.Build(kind, lang, Model());

        Assert.False(string.IsNullOrWhiteSpace(subject));
        Assert.StartsWith("<!DOCTYPE html>", html);
        // The 580px card shell + accent bar are the layout's fingerprint — every template renders
        // through it now, not the old hand-rolled <div style=...>.
        Assert.Contains("max-width:580px", html);
        Assert.Contains("background-color:#ffffff", html);
        Assert.Contains("Sent by <strong>Pointer</strong>", html);
    }

    // ── Confirm-deletion button ─────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("en")]
    [InlineData("ar")]
    public void ConfirmDeletion_LinkIsAButton_InBrandColor(string lang)
    {
        var model = Model();
        var (_, html) = WorkspaceLifecycleEmails.Build(
            WorkspaceLifecycleEmailKind.ConfirmDeletion,
            lang,
            model
        );

        // EmailComponents.Button: a table-cell background in the brand colour wrapping an <a> to
        // the confirm link — not a bare inline <a href> like the old template.
        Assert.Contains($"background-color:{model.BrandColor};border-radius:8px;", html);
        Assert.Contains($"<a href=\"{model.Link}\" target=\"_blank\"", html);
    }

    // ── Arabic direction/lang, English absence thereof ─────────────────────────────────────

    [Theory]
    [MemberData(nameof(KindsData))]
    public void Arabic_RendersLangArAndRtl(WorkspaceLifecycleEmailKind kind)
    {
        var (_, html) = WorkspaceLifecycleEmails.Build(kind, "ar", Model());

        Assert.Contains("<html lang=\"ar\">", html);
        Assert.DoesNotContain("<html lang=\"ar\" dir=", html);
        Assert.Contains("class=\"email-content\" dir=\"rtl\"", html);
        Assert.Contains("text-align:right;", html);
    }

    [Fact]
    public void Arabic_ConfirmDeletion_ButtonArrowAndCalloutBorderAreMirrored()
    {
        var (_, ar) = WorkspaceLifecycleEmails.Build(WorkspaceLifecycleEmailKind.ConfirmDeletion, "ar", Model());
        var (_, en) = WorkspaceLifecycleEmails.Build(WorkspaceLifecycleEmailKind.ConfirmDeletion, "en", Model());

        Assert.Contains("&larr;</a>", ar);
        Assert.DoesNotContain("&rarr;</a>", ar);
        Assert.Contains("border-right:4px solid", ar);
        Assert.Contains("&rarr;</a>", en);
        Assert.Contains("border-left:4px solid", en);
    }

    [Theory]
    [MemberData(nameof(KindsData))]
    public void Subject_UsesRawWorkspaceName_HtmlStaysEncoded(WorkspaceLifecycleEmailKind kind)
    {
        foreach (var lang in new[] { "en", "ar" })
        {
            var (subject, html) = WorkspaceLifecycleEmails.Build(kind, lang, Model() with { WorkspaceName = "R&D <Team>" });

            // Subjects are plain-text headers: an HTML entity would show literally in the inbox.
            Assert.Contains("R&D <Team>", subject);
            Assert.DoesNotContain("&amp;", subject);
            // The body (heading, preheader, paragraphs) is HTML: encoded exactly once.
            Assert.Contains("R&amp;D &lt;Team&gt;", html);
            Assert.DoesNotContain("&amp;amp;", html);
            Assert.DoesNotContain("<Team>", html);
        }
    }

    [Theory]
    [MemberData(nameof(KindsData))]
    public void English_NeverRendersRtlOrArabicLang(WorkspaceLifecycleEmailKind kind)
    {
        var (_, html) = WorkspaceLifecycleEmails.Build(kind, "en", Model());

        Assert.Contains("<html lang=\"en\">", html);
        Assert.DoesNotContain("dir=\"rtl\"", html);
        Assert.DoesNotContain("text-align:right;", html);
    }

    [Theory]
    [MemberData(nameof(KindsData))]
    public void NullLanguage_FallsBackToEnglish(WorkspaceLifecycleEmailKind kind)
    {
        var (_, html) = WorkspaceLifecycleEmails.Build(kind, null, Model());

        Assert.Contains("<html lang=\"en\">", html);
        Assert.DoesNotContain("dir=\"rtl\"", html);
    }

    // ── HTML encoding ───────────────────────────────────────────────────────────────────────

    [Theory]
    [CombinatorialLangKind]
    public void WorkspaceName_IsHtmlEncoded(WorkspaceLifecycleEmailKind kind, string lang)
    {
        var model = Model("<script>alert(1)</script>");
        var (_, html) = WorkspaceLifecycleEmails.Build(kind, lang, model);

        Assert.Contains("&lt;script&gt;alert(1)&lt;/script&gt;", html);
        Assert.DoesNotContain("<script>alert(1)</script>", html);
    }

    // ── Copy fix (DB-18 review NIT) ─────────────────────────────────────────────────────────

    [Theory]
    [CombinatorialLangKind]
    public void NeverClaims_PausingKeepsEverything(WorkspaceLifecycleEmailKind kind, string lang)
    {
        var (_, html) = WorkspaceLifecycleEmails.Build(kind, lang, Model());

        Assert.DoesNotContain("keeps everything", html, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("كل شيء", html);
    }

    [Fact]
    public void ConfirmDeletion_English_StatesWhatPausingKeeps()
    {
        var (_, html) = WorkspaceLifecycleEmails.Build(
            WorkspaceLifecycleEmailKind.ConfirmDeletion,
            "en",
            Model()
        );

        Assert.Contains("pausing keeps your projects, comments and settings", html);
    }

    [Fact]
    public void ConfirmDeletion_Arabic_StatesWhatPausingKeeps()
    {
        var (_, html) = WorkspaceLifecycleEmails.Build(
            WorkspaceLifecycleEmailKind.ConfirmDeletion,
            "ar",
            Model()
        );

        Assert.Contains("يحتفظ بمشاريعك وتعليقاتك وإعداداتك", html);
    }

    public static IEnumerable<object[]> KindsData() => AllKinds.Select(k => new object[] { k });
}

/// <summary>Combines every <see cref="WorkspaceLifecycleEmailKind"/> with both languages.</summary>
public sealed class CombinatorialLangKindAttribute : Xunit.Sdk.DataAttribute
{
    public override IEnumerable<object[]> GetData(System.Reflection.MethodInfo testMethod)
    {
        foreach (
            var kind in new[]
            {
                WorkspaceLifecycleEmailKind.ConfirmDeletion,
                WorkspaceLifecycleEmailKind.Scheduled,
                WorkspaceLifecycleEmailKind.Reminder,
                WorkspaceLifecycleEmailKind.Deleted,
                WorkspaceLifecycleEmailKind.Cancelled,
            }
        )
        foreach (var lang in new[] { "en", "ar" })
            yield return [kind, lang];
    }
}
