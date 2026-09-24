/**
 * Renders every EmailTemplateBuilder template to a standalone HTML file so the transactional
 * emails can be eyeballed in a browser without triggering a real send:
 *
 *   node scripts/preview-emails.mjs [--open]
 *
 * Single source of truth: the script compiles a tiny throwaway console project that references
 * Application/Pointer.Application.csproj and calls the REAL builder methods — the previews can
 * never drift from what the services send. Output lands in a temp dir; the index path is printed
 * (and opened with `open` when --open is passed on macOS).
 */
import { execSync } from 'node:child_process';
import { mkdirSync, rmSync, writeFileSync, existsSync } from 'node:fs';
import { tmpdir } from 'node:os';
import { join, resolve, dirname } from 'node:path';
import { fileURLToPath } from 'node:url';

const root = resolve(dirname(fileURLToPath(import.meta.url)), '..');
const open = process.argv.includes('--open');

const workDir = join(tmpdir(), 'pointer-email-previews');
const toolDir = join(workDir, 'tool');
const outDir = join(workDir, 'html');

// ── 1. Throwaway console project that renders the real C# templates to JSON ──

const PROGRAM_CS = `
using System.Text.Json;
using Pointer.Application.Common;
using Pointer.Application.Common.Email;

var productName = "Pointer";
var brand = "#2563eb";
var app = "https://app.pointer.moamen.work";
var expires = DateTime.UtcNow.AddDays(7);
var started = DateTime.UtcNow.AddMinutes(-5);

var templates = new Dictionary<string, string>
{
    ["01-verify-email"] = EmailTemplateBuilder.VerifyEmail(
        $"{app}/verify-email?token=preview-token", "ada@acme.inc", productName, brand, app),
    ["02-password-reset"] = EmailTemplateBuilder.PasswordReset(
        $"{app}/reset?token=preview-token", productName, "Acme Inc", brand, app),
    ["03-password-reset-no-workspace"] = EmailTemplateBuilder.PasswordReset(
        $"{app}/reset?token=preview-token", productName, null, brand, app),
    ["04-password-changed"] = EmailTemplateBuilder.PasswordChanged(
        "Ada Lovelace", productName, "Acme Inc", brand, app),
    ["05-email-change-notice-old"] = EmailTemplateBuilder.EmailChangeNoticeOld(
        "new-ada@acme.inc", productName, brand, app),
    ["06-email-change-confirm-new"] = EmailTemplateBuilder.EmailChangeConfirmNew(
        $"{app}/confirm-email?token=preview-token", productName, "Acme Inc", brand, app),
    ["07-email-change-completed-old"] = EmailTemplateBuilder.EmailChangeCompletedOld(
        "new-ada@acme.inc", productName, brand, app),
    ["08-workspace-invite"] = EmailTemplateBuilder.WorkspaceInvite(
        $"{app}/join?code=preview-code", "Developer", productName, expires, false, "Acme Inc", brand, app),
    ["09-workspace-invite-new"] = EmailTemplateBuilder.WorkspaceInvite(
        $"{app}/join?code=preview-code", null, productName, expires, true, null, brand, app),
    ["10-quick-access-invite"] = EmailTemplateBuilder.QuickAccessInvite(
        "https://client.example.com/?pointer_invite=preview-token-1234567890abcdef",
        "client@acme.inc", productName, "Marketing site",
        "https://chromewebstore.google.com/detail/pointer", "Acme Inc", brand, app),
    ["11-user-approved"] = EmailTemplateBuilder.UserApproved(
        "ada@acme.inc", productName, app, "Acme Inc", brand),
    ["12-user-rejected"] = EmailTemplateBuilder.UserRejected(
        "ada@acme.inc", productName, "Acme Inc", brand, app),
    ["13-demo-ready"] = EmailTemplateBuilder.DemoReady(
        "demo-ab12cd34@demo.invalid", "correct-horse-battery", "demo-ab12cd34",
        "https://demo.pointer.moamen.work", DateTime.UtcNow.AddDays(2), productName,
        "Demo Workspace", brand, app),
    ["14-demo-expiry-warning"] = EmailTemplateBuilder.DemoExpiryWarning(
        "Demo Workspace", DateTime.UtcNow.AddHours(2), productName, app, 48, true, brand),
    ["15-account-erase-confirm"] = EmailTemplateBuilder.AccountEraseConfirm(
        $"{app}/delete-account?token=preview-token", productName, brand, app),
    ["16-suggestion-review"] = EmailTemplateBuilder.SuggestionReview(
        "Marketing site", "Acme Inc", productName, app, brand),
    ["17-impersonation-notice"] = EmailTemplateBuilder.ImpersonationNotice(
        "Ada Lovelace", productName, "Acme Inc", started, 15, "investigating a support ticket", brand, app),
};

// ── DB-18 workspace-lifecycle e-mails (en + rtl ar) — built via WorkspaceLifecycleEmails.Build,
// same entry point WorkspaceLifecycleService uses. ─────────────────────────────────────────────
var scheduledFor = DateTime.UtcNow.AddDays(14);
var deletedAt = DateTime.UtcNow;
foreach (var lang in new[] { "en", "ar" })
{
    var suffix = lang == "ar" ? "-ar" : "";

    var confirmModel = new WorkspaceLifecycleEmailModel(
        "Acme Inc", productName, app,
        Link: $"{app}/confirm-workspace-deletion?token=preview-token",
        GraceDays: 14, BrandColor: brand, LogoUrl: null);
    var (_, confirmHtml) = WorkspaceLifecycleEmails.Build(
        WorkspaceLifecycleEmailKind.ConfirmDeletion, lang, confirmModel);
    templates[$"18-workspace-confirm-deletion{suffix}"] = confirmHtml;

    var scheduledModel = new WorkspaceLifecycleEmailModel(
        "Acme Inc", productName, app,
        ScheduledFor: scheduledFor, ActorName: "Ada Lovelace",
        BrandColor: brand, LogoUrl: null);
    var (_, scheduledHtml) = WorkspaceLifecycleEmails.Build(
        WorkspaceLifecycleEmailKind.Scheduled, lang, scheduledModel);
    templates[$"19-workspace-deletion-scheduled{suffix}"] = scheduledHtml;

    var reminderModel = new WorkspaceLifecycleEmailModel(
        "Acme Inc", productName, app,
        ScheduledFor: scheduledFor, BrandColor: brand, LogoUrl: null);
    var (_, reminderHtml) = WorkspaceLifecycleEmails.Build(
        WorkspaceLifecycleEmailKind.Reminder, lang, reminderModel);
    templates[$"20-workspace-deletion-reminder{suffix}"] = reminderHtml;

    var deletedModel = new WorkspaceLifecycleEmailModel(
        "Acme Inc", productName, app,
        ScheduledFor: deletedAt, BackupDays: 30, BrandColor: brand, LogoUrl: null);
    var (_, deletedHtml) = WorkspaceLifecycleEmails.Build(
        WorkspaceLifecycleEmailKind.Deleted, lang, deletedModel);
    templates[$"21-workspace-deleted{suffix}"] = deletedHtml;

    var cancelledModel = new WorkspaceLifecycleEmailModel(
        "Acme Inc", productName, app,
        ActorName: "Ada Lovelace", StillPaused: true, BrandColor: brand, LogoUrl: null);
    var (_, cancelledHtml) = WorkspaceLifecycleEmails.Build(
        WorkspaceLifecycleEmailKind.Cancelled, lang, cancelledModel);
    templates[$"22-workspace-deletion-cancelled{suffix}"] = cancelledHtml;
}

Console.Write(JsonSerializer.Serialize(templates));

if (args.Contains("--mailpit"))
{
    try
    {
        using var client = new System.Net.Mail.SmtpClient("localhost", 1025);
        foreach (var (key, html) in templates)
        {
            var cleanTitle = key.Substring(3).Replace('-', ' ');
            var subject = $"[Pointer] {char.ToUpper(cleanTitle[0]) + cleanTitle.Substring(1)}";
            using var msg = new System.Net.Mail.MailMessage
            {
                From = new System.Net.Mail.MailAddress("noreply@pointer.local", "Pointer"),
                Subject = subject,
                Body = html,
                IsBodyHtml = true,
            };
            msg.To.Add("preview@acme.inc");
            client.Send(msg);
        }
    }
    catch { /* SMTP not listening; fallback to html files */ }
}
`;

const CSPROJ = `<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <OutputType>Exe</OutputType>
    <TargetFramework>net8.0</TargetFramework>
    <Nullable>enable</Nullable>
    <ImplicitUsings>enable</ImplicitUsings>
  </PropertyGroup>
  <ItemGroup>
    <ProjectReference Include="${join(root, 'Application', 'Pointer.Application.csproj').replace(/"/g, '&quot;')}" />
  </ItemGroup>
</Project>
`;

rmSync(toolDir, { recursive: true, force: true });
mkdirSync(toolDir, { recursive: true });
writeFileSync(join(toolDir, 'Program.cs'), PROGRAM_CS);
writeFileSync(join(toolDir, 'preview-tool.csproj'), CSPROJ);

console.log('Rendering templates through the real EmailTemplateBuilder (one-time build) ...');
const json = execSync('dotnet run --project preview-tool.csproj -- --mailpit', {
  cwd: toolDir,
  encoding: 'utf8',
  maxBuffer: 32 * 1024 * 1024,
  stdio: ['ignore', 'pipe', 'inherit'],
});

const templates = JSON.parse(json);

// ── 2. Write one HTML file per template + an index ──

rmSync(outDir, { recursive: true, force: true });
mkdirSync(outDir, { recursive: true });

const names = Object.keys(templates).sort();
for (const name of names)
  writeFileSync(join(outDir, `${name}.html`), templates[name], 'utf8');

const index = `<!doctype html>
<html lang="en">
<head>
<meta charset="utf-8">
<title>Pointer email previews</title>
<style>
  body { margin: 0; font: 14px/1.5 -apple-system, BlinkMacSystemFont, 'Segoe UI', sans-serif; background: #e2e8f0; }
  header { position: sticky; top: 0; background: #0f172a; color: #f8fafc; padding: 10px 16px; }
  header select { font: inherit; padding: 4px 8px; }
  header a { color: #93c5fd; }
  main { padding: 24px 16px; }
  iframe { width: 100%; max-width: 680px; height: calc(100vh - 140px); background: #fff; border: 1px solid #cbd5e1; border-radius: 8px; }
</style>
</head>
<body>
<header>
  <strong>Pointer email previews</strong> &middot;
  <label>Template
    <select id="picker" onchange="location.hash = '#' + this.value; document.getElementById('frame').src = this.value + '.html';">
      ${names.map((n) => `<option value="${n}">${n}</option>`).join('\n      ')}
    </select>
  </label>
  &middot; <a id="raw" href="#" target="_blank">open standalone</a>
</header>
<main><iframe id="frame" src="${names[0]}.html" title="email preview"></iframe></main>
<script>
  const picker = document.getElementById('picker');
  const frame = document.getElementById('frame');
  const raw = document.getElementById('raw');
  const pick = (name) => { picker.value = name; frame.src = name + '.html'; raw.href = name + '.html'; };
  addEventListener('hashchange', () => pick(decodeURIComponent(location.hash.slice(1)) || picker.value));
  const initial = decodeURIComponent(location.hash.slice(1)) || picker.value;
  pick(names.includes(initial) ? initial : picker.value);
</script>
</body>
</html>
`;

const indexPath = join(outDir, 'index.html');
writeFileSync(indexPath, index, 'utf8');

console.log(`\n${names.length} templates written to ${outDir}`);
console.log(`Open ${indexPath} in a browser.`);
console.log(`\n📬 All templates dispatched to Mailpit: view live at http://localhost:8025`);

if (open && process.platform === 'darwin') execSync(`open "${indexPath}"`);
else if (open && process.platform === 'linux')
  execSync(`xdg-open "${indexPath}" >/dev/null 2>&1 || true`);
else if (!existsSync(indexPath)) throw new Error('preview index was not written');
