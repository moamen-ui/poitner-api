using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Application.Validators;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.Services.Implementation;

public class DemoService : IDemoService
{
    private const int DefaultMaxActive = 100;
    private const int DefaultTtlHours = 24;
    private const int DefaultPerEmailPerDay = 3;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly ISettingsService _settings;
    private readonly IBrandingService _branding;
    private readonly IMembershipService _memberships;
    private readonly IAuditWriter _audit;

    public DemoService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        IEmailService emailService,
        ISettingsService settings,
        IBrandingService branding,
        IMembershipService memberships,
        IAuditWriter? audit = null
    )
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
        _emailService = emailService;
        _settings = settings;
        _branding = branding;
        _memberships = memberships;
        _audit = audit ?? NoopAuditWriter.Instance;
    }

    public async Task<Result<DemoSessionResponse>> ProvisionAsync(
        string serverUrl,
        string recipientEmail
    )
    {
        // Super-admin-tunable limits (config page) with safe defaults.
        var maxActive = await _settings.GetIntAsync(
            ISettingsService.DemoMaxActive,
            DefaultMaxActive
        );
        var ttlHours = await _settings.GetIntAsync(ISettingsService.DemoTtlHours, DefaultTtlHours);
        var perEmailPerDay = await _settings.GetIntAsync(
            ISettingsService.DemoPerEmailPerDay,
            DefaultPerEmailPerDay
        );

        // Validate the recipient email (email-gated demo — a real inbox is required).
        recipientEmail = (recipientEmail ?? string.Empty).Trim();
        if (recipientEmail.Length == 0 || !IsValidEmail(recipientEmail))
            return Result<DemoSessionResponse>.Failure(
                "A valid email is required to start a demo."
            );

        // Per-email daily limit (in addition to the per-IP rate limit + global active cap).
        var throttleKey =
            $"demo_email_{recipientEmail.ToLowerInvariant()}_{DateTime.UtcNow:yyyyMMdd}";
        var throttle = await _unitOfWork
            .Repository<AppSetting>()
            .Query()
            .FirstOrDefaultAsync(x => x.DeletedAt == null && x.Key == throttleKey);
        var usedToday = int.TryParse(throttle?.Value, out var used) ? used : 0;
        if (usedToday >= perEmailPerDay)
            return Result<DemoSessionResponse>.Failure(
                "You've reached today's demo limit for this email. Please try again tomorrow."
            );

        // a. Active cap check
        var active = await _unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(u => u.IsDemo && u.DeletedAt == null && u.ExpiresAt > DateTime.UtcNow);

        if (active >= maxActive)
            return Result<DemoSessionResponse>.Failure(
                "Demo is at capacity, please try again shortly."
            );

        // b. Resolve the "Workspace Admin" role
        var role = await _unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Name == "Workspace Admin" && r.DeletedAt == null);

        if (role == null)
            return Result<DemoSessionResponse>.Failure(
                "Workspace Admin role not found. Please contact the administrator."
            );

        // c. Build demo user. DB-11a: workspaces.id no longer equals anyone's public_id — mint a
        // fresh workspace id and make the demo admin the first membership.
        var slug = Guid.NewGuid().ToString("N")[..8];
        var publicId = Guid.NewGuid();
        var workspaceId = Guid.NewGuid();
        var email = $"demo-{slug}@demo.pointer";
        var password = Guid.NewGuid().ToString("N")[..12] + "Aa1!";
        // Minted fresh right below — not the DB-03 placeholder, so it always names the workspace in
        // the ready-email (no extra lookup needed: we already hold the name we just chose for it).
        const string demoWorkspaceName = "Demo Workspace";

        var demoUser = new User
        {
            PublicId = publicId,
            Email = email,
            PasswordHash = _passwordHasher.Hash(password),
            DisplayName = "Demo User",
            RoleId = role.Id,
            OwnerId = workspaceId,
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            IsDemo = true,
            ExpiresAt = DateTime.UtcNow.AddHours(ttlHours),
            RecipientEmail = recipientEmail,
        };

        await _unitOfWork.Workspaces.AddAsync(
            new Workspace
            {
                Id = workspaceId,
                Name = demoWorkspaceName,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
            }
        );
        await _unitOfWork.Repository<User>().AddAsync(demoUser);

        // d. Seed tenant data — Project first (needs SaveChanges to get Id)
        var project = new Project
        {
            Key = $"demo-{slug}",
            Name = "Demo Project",
            OwnerId = workspaceId,
        };

        await _unitOfWork.Repository<Project>().AddAsync(project);
        await _unitOfWork.SaveChangesAsync();

        // Seed ~3 sample Comments on the project
        var comments = new[]
        {
            new Comment
            {
                ProjectId = project.Id,
                OwnerId = workspaceId,
                AuthorId = publicId,
                Environment = EnvironmentTag.Staging,
                Status = CommentStatus.Open,
                Body = "Sample: tighten this heading — font-size feels too large on mobile.",
                IsPrivate = false,
                Element = new ElementCapture
                {
                    Selector = "h1.hero-title",
                    Snapshot = "<h1 class=\"hero-title\">Welcome to the Demo</h1>",
                },
            },
            new Comment
            {
                ProjectId = project.Id,
                OwnerId = workspaceId,
                AuthorId = publicId,
                Environment = EnvironmentTag.Staging,
                Status = CommentStatus.ReadyToApply,
                Body = "Sample: button colour should match the brand palette (#1a73e8).",
                IsPrivate = false,
                Element = new ElementCapture
                {
                    Selector = "button.cta-primary",
                    Snapshot = "<button class=\"cta-primary\">Get Started</button>",
                },
            },
            new Comment
            {
                ProjectId = project.Id,
                OwnerId = workspaceId,
                AuthorId = publicId,
                Environment = EnvironmentTag.Staging,
                Status = CommentStatus.Applied,
                Body = "Sample: nav link spacing was too tight — fixed.",
                IsPrivate = false,
                Element = new ElementCapture
                {
                    Selector = "nav a",
                    Snapshot = "<a href=\"/about\">About</a>",
                },
            },
        };

        foreach (var comment in comments)
            await _unitOfWork.Repository<Comment>().AddAsync(comment);

        await _unitOfWork.SaveChangesAsync();

        // DB-11a: the demo admin's presence in its own workspace is a membership, not owner_id.
        var demoMembership = await _memberships.JoinAsync(
            demoUser,
            workspaceId,
            role,
            ApprovalStatus.Approved,
            isActive: true,
            inviteId: null
        );
        await _unitOfWork.SaveChangesAsync();

        // g. Record one demo against this email for today's per-email limit. Done BEFORE the Role
        // navigation is populated below, so this SaveChangesAsync never sees a detached Role
        // reference on the tracked membership (which EF would otherwise try to re-insert).
        if (throttle == null)
            await _unitOfWork
                .Repository<AppSetting>()
                .AddAsync(new AppSetting { Key = throttleKey, Value = "1" });
        else
        {
            throttle.Value = (usedToday + 1).ToString();
            _unitOfWork.Repository<AppSetting>().Update(throttle);
        }
        await _unitOfWork.SaveChangesAsync();

        // e. Issue token. Role populated AFTER every SaveChangesAsync above has run, so EF never
        // tries to re-insert the already-existing role row.
        demoUser.Role = role;
        demoMembership.Role = role;
        var token = _tokenService.Issue(demoUser, demoMembership);

        // f. Email the credentials to the requester. On success we blank the password in the
        //    response (they read it from their inbox); on failure/cap we fall back to inline creds
        //    so the demo is never blocked.
        var expiresAt = demoUser.ExpiresAt!.Value;
        var demoBrand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var demoProductName = demoBrand.ProductName;
        var emailSent = await _emailService.SendAsync(
            recipientEmail,
            $"Your {demoProductName} demo is ready",
            BuildDemoEmailHtml(
                email,
                password,
                project.Key,
                serverUrl,
                expiresAt,
                demoProductName,
                demoWorkspaceName
            )
        );

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthDemoProvisioned,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["source"] = "demo" }
            )
        );
        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.WorkspaceCreated,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["source"] = "demo" }
            )
        );

        // h. Return response
        return Result<DemoSessionResponse>.Success(
            new DemoSessionResponse
            {
                Token = token,
                Email = email,
                Password = emailSent ? string.Empty : password,
                ProjectKey = project.Key,
                ExpiresAt = expiresAt,
                ServerUrl = serverUrl,
                EmailSent = emailSent,
            }
        );
    }

    public async Task<Result<UpgradeDemoResponse>> UpgradeAsync(
        Guid callerPublicId,
        UpgradeDemoRequest request
    )
    {
        // 1. Validate the request inline (this is the only consumer).
        var validation = new UpgradeDemoValidator().Validate(request);
        if (!validation.IsValid)
            return Result<UpgradeDemoResponse>.Failure(validation.Errors[0].ErrorMessage);

        var emailNormalized = EmailNormalizer.NormalizeRequired(request.Email);

        // 2. Load the caller, bypassing the tenant query filter (the JWT carries the tenant claim
        //    but resolving by PublicId + DeletedAt is authoritative).
        var user = await _unitOfWork
            .Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .Include(u => u.Role)
            .FirstOrDefaultAsync(u => u.DeletedAt == null && u.PublicId == callerPublicId);

        if (user == null)
            return Result<UpgradeDemoResponse>.NotFound(MessageKeys.User.NotFound);

        // 3. Guard: only demo accounts may upgrade.
        if (!user.IsDemo)
            return Result<UpgradeDemoResponse>.Forbidden(MessageKeys.Demo.NotDemoUser);

        // 4. Guard: an already-expired demo cannot be salvaged.
        if (user.ExpiresAt != null && user.ExpiresAt < DateTime.UtcNow)
            return Result<UpgradeDemoResponse>.Failure(MessageKeys.Demo.DemoExpired);

        // 5. DB-11a (D7): e-mail uniqueness is now GLOBAL — one identity per e-mail. A demo
        //    upgrading to an address that already belongs to a live identity is a Conflict; there is
        //    no automatic merge (D7 default: no).
        var existingIdentity = await _memberships.FindIdentityByEmailAsync(emailNormalized);
        if (existingIdentity != null && existingIdentity.PublicId != callerPublicId)
            return Result<UpgradeDemoResponse>.Conflict(MessageKeys.Demo.EmailTaken);

        // 6-8. Mutate the user entity in place, then persist. A concurrent upgrade racing past
        //      the uniqueness check will trip the DB unique index here → treat as EmailTaken.
        user.IsDemo = false;
        user.ExpiresAt = null;
        user.DemoExtended = false;
        user.DemoCommentCapOverride = null;
        user.DemoTtlHoursOverride = null;
        user.Email = emailNormalized;
        user.PasswordHash = _passwordHasher.Hash(request.Password);
        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? user.DisplayName
            : request.DisplayName!.Trim();
        user.RecipientEmail = null;
        // H1: the email+password just changed — bump the stamp so the old demo token is revoked.
        // The fresh token issued below carries the new stamp, so the upgraded user stays signed in.
        user.SecurityStamp = Guid.NewGuid();

        _unitOfWork.Repository<User>().Update(user);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Result<UpgradeDemoResponse>.Conflict(MessageKeys.Demo.EmailTaken);
        }

        // 9-10. Role navigation is already loaded above; issue a fresh token with the real email.
        // DB-11a: the demo admin's own membership (in its own workspace) carries the role/tenant now.
        var upgradeMembership =
            user.OwnerId is Guid demoOwnerId
                ? await _memberships.GetMembershipAsync(user.Id, demoOwnerId)
                : null;
        var token = _tokenService.Issue(user, upgradeMembership);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthDemoUpgraded,
                AuditTargets.Workspace,
                user.OwnerId?.ToString(),
                user.OwnerId
            )
        );

        // 11. Return token + MeResponse in the same shape as a successful login.
        return Result<UpgradeDemoResponse>.Success(
            new UpgradeDemoResponse
            {
                Token = token,
                User = UserMapper.ToMeResponse(user, upgradeMembership?.Role ?? user.Role),
            },
            MessageKeys.Demo.UpgradeSuccess
        );
    }

    private static bool IsValidEmail(string email)
    {
        try
        {
            return new System.Net.Mail.MailAddress(email).Address == email;
        }
        catch
        {
            return false;
        }
    }

    private static string BuildDemoEmailHtml(
        string login,
        string password,
        string projectKey,
        string serverUrl,
        DateTime expiresUtc,
        string productName,
        string? workspaceName = null
    )
    {
        var snippet =
            $"&lt;script src=\"{serverUrl}/widget.js\" defer&gt;&lt;/script&gt;<br/>"
            + $"&lt;pointer-feedback project=\"{projectKey}\" server=\"{serverUrl}\"&gt;&lt;/pointer-feedback&gt;";
        // workspaceName is RAW (not yet encoded) — null (never the case for the current mint point,
        // which always names it "Demo Workspace") falls back to the pre-existing wording.
        var workspaceClause =
            workspaceName != null
                ? $", <b>{System.Net.WebUtility.HtmlEncode(workspaceName)}</b>,"
                : string.Empty;
        return $@"<div style=""font-family:system-ui,Segoe UI,Roboto,sans-serif;color:#0f172a;line-height:1.6"">
  <h2 style=""margin:0 0 8px"">Your {productName} demo is ready 🐕</h2>
  <p style=""color:#475569;margin:0 0 16px"">This demo workspace{workspaceClause} expires on {expiresUtc:yyyy-MM-dd HH:mm} UTC.</p>
  <table style=""border-collapse:collapse;font-size:14px"">
    <tr><td style=""padding:4px 12px 4px 0;color:#475569"">Project key</td><td><code>{projectKey}</code></td></tr>
    <tr><td style=""padding:4px 12px 4px 0;color:#475569"">Widget login</td><td><code>{login}</code></td></tr>
    <tr><td style=""padding:4px 12px 4px 0;color:#475569"">Password</td><td><code>{password}</code></td></tr>
  </table>
  <p style=""color:#475569;margin:16px 0 6px"">Embed snippet (paste into your app's index.html):</p>
  <pre style=""background:#f1f5f9;padding:12px;border-radius:8px;font-size:13px;white-space:pre-wrap"">{snippet}</pre>
  <p style=""color:#94a3b8;font-size:12px;margin-top:16px"">If you didn't request this, you can ignore this email.</p>
</div>";
    }
}
