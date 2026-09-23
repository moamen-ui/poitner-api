using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Common.Email;
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
    private const bool DefaultConvertRequiresVerification = false;
    private const int DefaultConvertVerifyHours = 72;
    private const string WorkspaceAdminRoleName = "Workspace Admin";

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;
    private readonly IEmailService _emailService;
    private readonly ISettingsService _settings;
    private readonly IBrandingService _branding;
    private readonly IMembershipService _memberships;
    private readonly IAuditWriter _audit;
    private readonly IEmailVerificationService _emailVerification;
    private readonly IConfiguration? _config;

    public DemoService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ITokenService tokenService,
        IEmailService emailService,
        ISettingsService settings,
        IBrandingService branding,
        IMembershipService memberships,
        IAuditWriter? audit = null,
        IEmailVerificationService? emailVerification = null,
        // DB-17 §3.4: nullable-with-default, last — every existing hand-rolled test construction
        // compiles unchanged and gets the flag OFF (D17.5 default) since no configuration is wired.
        IConfiguration? config = null
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
        _emailVerification = emailVerification ?? NoopEmailVerification.Instance;
        _config = config;
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
        // DB-17 §3.6/R14: the throttle key is a hashed pseudonym of the NORMALISED address, never
        // the raw address — lower-casing an e-mail by hand is the one thing this must never do
        // again (EmailNormalizer is the only normaliser; the audit-row pseudonym hasher is reused
        // here for the same reason: never write a raw address where it cannot be erased with the
        // row that carries it).
        var emailNormalized = EmailNormalizer.NormalizeRequired(recipientEmail);
        var throttleKey =
            $"demo_email_{PseudonymHasher.EmailHash(emailNormalized)}_{DateTime.UtcNow:yyyyMMdd}";
        var throttle = await _unitOfWork
            .Repository<AppSetting>()
            .Query()
            .FirstOrDefaultAsync(x => x.DeletedAt == null && x.Key == throttleKey);
        var usedToday = int.TryParse(throttle?.Value, out var used) ? used : 0;
        if (usedToday >= perEmailPerDay)
            return Result<DemoSessionResponse>.Failure(
                "You've reached today's demo limit for this email. Please try again tomorrow."
            );

        // a. Active cap check — DB-17 §3.6: counted on WORKSPACES now (the workspace is the TTL
        // authority), never `users`.
        var now0 = DateTime.UtcNow;
        var active = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .CountAsync(w =>
                w.DeletedAt == null && w.DemoExpiresAt != null && w.DemoExpiresAt > now0
            );

        if (active >= maxActive)
            return Result<DemoSessionResponse>.Failure(
                "Demo is at capacity, please try again shortly."
            );

        // b. Resolve the "Workspace Admin" role
        var role = await _unitOfWork
            .Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Name == WorkspaceAdminRoleName && r.DeletedAt == null);

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
        // DB-17 §3.3: one instant, used for the user, the workspace and the response.
        var expiresAt = DateTime.UtcNow.AddHours(ttlHours);

        var demoUser = new User
        {
            PublicId = publicId,
            Email = email,
            PasswordHash = _passwordHasher.Hash(password),
            DisplayName = "Demo User",
            RoleId = role.Id,
            // Legacy (DB-11a/DB-17) — written once at creation, never read after DB-17; the
            // pre-DB-17 cleanup and a rollback still depend on it (Opus DB-17 #5), and the R3
            // backfill keys on it.
            OwnerId = workspaceId,
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            IsDemo = true,
            RecipientEmail = recipientEmail,
        };

        await _unitOfWork.Workspaces.AddAsync(
            new Workspace
            {
                Id = workspaceId,
                Name = demoWorkspaceName,
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceId,
                DemoExpiresAt = expiresAt,
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

        // DB-15: the funnel's first step. One demo_started row per provision — a fresh workspace is
        // minted above, so no uniqueness guard is needed. Analytics must never fail a demo.
        try
        {
            _unitOfWork.UsageEvents.Add(
                new UsageEvent
                {
                    Type = UsageEventTypes.DemoStarted,
                    Source = "api",
                    OwnerId = workspaceId,
                    ProjectId = project.Id,
                    UserId = publicId,
                    CreatedAt = DateTime.UtcNow,
                }
            );
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Exception)
        {
            // Analytics must never fail a demo — but the failed UsageEvent would otherwise stay
            // tracked as Added on this (request-scoped) context and be resubmitted (and re-fail)
            // by the AuditWriter's SaveChangesAsync that follows, poisoning the audit write.
            _unitOfWork.ClearChangeTracker();
        }

        // e. Issue token. Role populated AFTER every SaveChangesAsync above has run, so EF never
        // tries to re-insert the already-existing role row.
        demoUser.Role = role;
        demoMembership.Role = role;
        var token = _tokenService.Issue(demoUser, demoMembership);

        // f. Email the credentials to the requester. On success we blank the password in the
        //    response (they read it from their inbox); on failure/cap we fall back to inline creds
        //    so the demo is never blocked.
        var demoBrand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var demoProductName = demoBrand.ProductName;
        var emailSent = await _emailService.SendAsync(
            recipientEmail,
            $"Your {demoProductName} demo is ready",
            EmailTemplateBuilder.DemoReady(
                email,
                password,
                project.Key,
                serverUrl,
                expiresAt,
                demoProductName,
                demoWorkspaceName,
                demoBrand.PrimaryColor,
                demoBrand.Urls.App.TrimEnd('/')
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
        Guid workspaceId,
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

        // 3. Guard: only demo identities may upgrade (identity-level fact, unchanged).
        if (!user.IsDemo)
            return Result<UpgradeDemoResponse>.Forbidden(MessageKeys.Demo.NotDemoUser);

        // 3a. DB-17 §3.3 (Gemini Pro #3): the SESSION's workspace, never "the one demo the identity
        // belongs to" — an identity may administer more than one demo. The caller must hold a live
        // Workspace Admin membership in exactly this workspace.
        var membership = await _memberships.GetMembershipAsync(user.Id, workspaceId);
        if (
            membership == null
            || !membership.IsActive
            || membership.ApprovalStatus != ApprovalStatus.Approved
            || membership.Role?.Name != WorkspaceAdminRoleName
        )
            return Result<UpgradeDemoResponse>.Forbidden(MessageKeys.Demo.NotDemoUser);

        var workspace = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (workspace?.DemoExpiresAt == null)
            return Result<UpgradeDemoResponse>.Forbidden(MessageKeys.Demo.NotDemoUser);

        // 4. Guard: an already-expired demo cannot be salvaged. The WORKSPACE is the only authority (DB-17; the users columns were dropped by DB-11e).
        if (workspace.DemoExpiresAt < DateTime.UtcNow)
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
        user.Email = emailNormalized;
        // DB-14 §3.2: this is exactly where the address becomes real (F4) — reset to null (never
        // by anything else) so the convert step re-earns verification.
        user.EmailVerifiedAt = null;
        user.PasswordHash = _passwordHasher.Hash(request.Password);
        user.DisplayName = string.IsNullOrWhiteSpace(request.DisplayName)
            ? user.DisplayName
            : request.DisplayName!.Trim();
        user.RecipientEmail = null;
        // H1: the email+password just changed — bump the stamp so the old demo token is revoked.
        // The fresh token issued below carries the new stamp, so the upgraded user stays signed in.
        user.SecurityStamp = Guid.NewGuid();

        _unitOfWork.Repository<User>().Update(user);

        // DB-17 §3.3/§3.4: the workspace mutation, in the SAME SaveChangesAsync as the user above.
        // Read via the indexer + manual parse (no Configuration.Binder package reference) so a
        // missing/unparsable value falls back to the D17.5 default (flag off).
        var convertRequiresVerification = bool.TryParse(
            _config?["Demo:ConvertRequiresVerification"],
            out var crv
        )
            ? crv
            : DefaultConvertRequiresVerification;
        var convertVerifyHours = int.TryParse(_config?["Demo:ConvertVerifyHours"], out var cvh)
            ? cvh
            : DefaultConvertVerifyHours;

        var convertedAt = DateTime.UtcNow;
        workspace.DemoConvertedAt = convertedAt;
        if (!convertRequiresVerification)
        {
            workspace.DemoExpiresAt = null;
        }
        else
        {
            workspace.DemoExpiresAt = convertedAt.AddHours(convertVerifyHours);
            workspace.DemoExpiryWarnedAt = null; // a fresh warning window for the extended TTL
        }
        // DemoExtendedAt is kept (history — §3.1). The caps are reset: nobody is a demo any more.
        workspace.DemoCommentCapOverride = null;
        workspace.DemoTtlHoursOverride = null;
        workspace.Name = string.IsNullOrWhiteSpace(request.WorkspaceName)
            ? Workspace.PlaceholderName
            : request.WorkspaceName!.Trim();
        workspace.UpdatedAt = convertedAt;
        workspace.UpdatedBy = callerPublicId;
        _unitOfWork.Workspaces.Update(workspace);

        try
        {
            await _unitOfWork.SaveChangesAsync();
        }
        catch (DbUpdateException)
        {
            return Result<UpgradeDemoResponse>.Conflict(MessageKeys.Demo.EmailTaken);
        }

        // Review finding #3 (security-direction — must): the gate's cache may still hold "verified"
        // from when this identity was IsDemo (exempt from the gate outright); now that IsDemo is
        // false and EmailVerifiedAt is null, that stale cache entry would let an unverified upgrade
        // act as an admin for up to the 60s TTL. Invalidate right after the flip is persisted.
        _emailVerification.InvalidateGate(user.PublicId);

        // DB-14 §3.2: the address just became real — send the verification link.
        await _emailVerification.SendAsync(user);

        // DB-15: the funnel's second step. The !IsDemo guard above makes a second emission
        // impossible — a converted workspace is no longer a demo. Analytics must never fail the
        // upgrade it records.
        try
        {
            _unitOfWork.UsageEvents.Add(
                new UsageEvent
                {
                    Type = UsageEventTypes.WorkspaceConverted,
                    Source = "api",
                    OwnerId = workspaceId,
                    UserId = callerPublicId,
                    CreatedAt = DateTime.UtcNow,
                }
            );
            await _unitOfWork.SaveChangesAsync();
        }
        catch (Exception)
        {
            // Analytics must never fail the upgrade — but the failed UsageEvent would otherwise
            // stay tracked as Added on this (request-scoped) context and be resubmitted (and
            // re-fail) by the AuditWriter's SaveChangesAsync that follows, poisoning the audit write.
            _unitOfWork.ClearChangeTracker();
        }

        // 9-10. Role navigation is already loaded above; issue a fresh token with the real email.
        var token = _tokenService.Issue(user, membership);

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.AuthDemoUpgraded,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string> { ["source"] = "demo" }
            )
        );

        // 11. Return token + MeResponse in the same shape as a successful login.
        return Result<UpgradeDemoResponse>.Success(
            new UpgradeDemoResponse
            {
                Token = token,
                User = UserMapper.ToMeResponse(user, membership.Role, workspace: workspace),
            },
            MessageKeys.Demo.UpgradeSuccess
        );
    }

    public async Task<int> WarnExpiringAsync(DateTime nowUtc, TimeSpan warnWindow)
    {
        var horizon = nowUtc + warnWindow;
        var expiring = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .Where(w =>
                w.DeletedAt == null
                && w.DemoExpiresAt != null
                && w.DemoExpiresAt > nowUtc
                && w.DemoExpiresAt <= horizon
                && w.DemoExpiryWarnedAt == null
            )
            .ToListAsync();

        if (expiring.Count == 0)
            return 0;

        var demoBrand = await _branding.BuildResponseAsync("", new HashSet<string>());
        var productName = demoBrand.ProductName;
        var appUrl = demoBrand.Urls.App;
        var ttlHours = await _settings.GetIntAsync(ISettingsService.DemoTtlHours, DefaultTtlHours);

        var stamped = 0;
        foreach (var w in expiring)
        {
            try
            {
                var admin = await _memberships.CurrentAdminAsync(w.Id);
                // A converted-but-unverified workspace (Demo:ConvertRequiresVerification mode) has no
                // RecipientEmail any more (cleared on convert) — the reminder then goes to the new
                // (unverified) address instead.
                var to =
                    admin?.User.RecipientEmail
                    ?? (w.DemoConvertedAt != null ? admin?.User.Email : null);

                if (!string.IsNullOrWhiteSpace(to))
                {
                    try
                    {
                        // DB-17 review finding #8 (LOW): per-workspace TTL override, not the global
                        // default, since that is what an actual "Extend once" on THIS workspace adds.
                        var workspaceTtlHours = w.DemoTtlHoursOverride ?? ttlHours;
                        await _emailService.SendAsync(
                            to,
                            $"Your {productName} demo expires in about two hours",
                            EmailTemplateBuilder.DemoExpiryWarning(
                                w.Name,
                                w.DemoExpiresAt!.Value,
                                productName,
                                appUrl,
                                workspaceTtlHours,
                                // DB-17 review finding #8 (LOW): the one extension was already used
                                // — don't dangle an action that will just fail.
                                canExtend: w.DemoExtendedAt == null,
                                primaryColor: demoBrand.PrimaryColor
                            )
                        );
                    }
                    catch (Exception)
                    {
                        // Best-effort — the stamp below still records the one attempt (D17.6) whether or
                        // not the send actually succeeded.
                    }
                }

                w.DemoExpiryWarnedAt = nowUtc;
                _unitOfWork.Workspaces.Update(w);
                await _unitOfWork.SaveChangesAsync();
                stamped++;
            }
            catch (Exception)
            {
                // DB-17 review finding #8 (LOW): one workspace's failure (membership lookup, the
                // stamp's SaveChangesAsync, …) must not stop the T-2h warning from reaching every
                // OTHER expiring demo in this pass — mirrors the per-item isolation in the expiry
                // sweep (§3.5).
            }
        }

        return stamped;
    }

    public async Task<Result<DemoStatusResponse>> ExtendAsync(Guid callerPublicId, Guid workspaceId)
    {
        var user = await _memberships.FindIdentityByPublicIdAsync(callerPublicId);
        if (user == null)
            return Result<DemoStatusResponse>.NotFound(MessageKeys.User.NotFound);

        // DB-17 §3.6 (Gemini Pro #3): the SESSION's workspace only, never a search across the
        // identity's memberships.
        var membership = await _memberships.GetMembershipAsync(user.Id, workspaceId);
        if (
            membership == null
            || !membership.IsActive
            || membership.ApprovalStatus != ApprovalStatus.Approved
            || membership.Role?.Name != WorkspaceAdminRoleName
        )
            return Result<DemoStatusResponse>.Forbidden(MessageKeys.Demo.NotDemoUser);

        var workspace = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .FirstOrDefaultAsync(w => w.Id == workspaceId && w.DeletedAt == null);
        if (workspace?.DemoExpiresAt == null)
            return Result<DemoStatusResponse>.Forbidden(MessageKeys.Demo.NotDemoUser);

        // DB-17 review finding #6 (LOW): with Demo:ConvertRequiresVerification on, a converted
        // workspace can still have a non-null DemoExpiresAt (the 72h re-verification grace) — that
        // is not "still a demo" for extension purposes, it is already upgraded.
        if (workspace.DemoConvertedAt != null)
            return Result<DemoStatusResponse>.Failure(MessageKeys.Demo.AlreadyUpgraded);

        if (workspace.DemoExpiresAt < DateTime.UtcNow)
            return Result<DemoStatusResponse>.Failure(MessageKeys.Demo.DemoExpired);

        if (workspace.DemoExtendedAt != null)
            return Result<DemoStatusResponse>.Failure(MessageKeys.Demo.AlreadyExtended);

        var ttlHours =
            workspace.DemoTtlHoursOverride
            ?? await _settings.GetIntAsync(ISettingsService.DemoTtlHours, DefaultTtlHours);

        var now = DateTime.UtcNow;
        var anchor = workspace.DemoExpiresAt > now ? workspace.DemoExpiresAt.Value : now;
        workspace.DemoExpiresAt = anchor.AddHours(ttlHours);
        workspace.DemoExtendedAt = now;
        _unitOfWork.Workspaces.Update(workspace);

        await _unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.DemoExtended,
                AuditTargets.Workspace,
                workspaceId.ToString(),
                workspaceId,
                After: new Dictionary<string, string>
                {
                    ["expires_at"] = workspace.DemoExpiresAt.Value.ToString("O"),
                }
            )
        );

        return Result<DemoStatusResponse>.Success(
            new DemoStatusResponse
            {
                ExpiresAt = workspace.DemoExpiresAt.Value,
                ExtendedAt = workspace.DemoExtendedAt,
                CanExtend = false,
            },
            string.Format(MessageKeys.Demo.Extended, workspace.DemoExpiresAt.Value.ToString("u"))
        );
    }

    public async Task OnEmailVerifiedAsync(User identity)
    {
        // DB-17 §3.4 (Opus #4): pre-wired for Demo:ConvertRequiresVerification — a no-op with the
        // flag off (a converted workspace has no TTL by then, so this query finds nothing).
        var adminWorkspaceIds = (await _memberships.ListForIdentityAsync(identity.Id))
            .Where(m =>
                m.IsActive
                && m.ApprovalStatus == ApprovalStatus.Approved
                && m.Role?.Name == WorkspaceAdminRoleName
            )
            .Select(m => m.OwnerId)
            .Distinct()
            .ToList();

        if (adminWorkspaceIds.Count == 0)
            return;

        var workspaces = await _unitOfWork
            .Workspaces.IgnoreQueryFilters()
            .Where(w =>
                w.DemoConvertedAt != null
                && w.DemoExpiresAt != null
                && adminWorkspaceIds.Contains(w.Id)
            )
            .ToListAsync();

        if (workspaces.Count == 0)
            return;

        foreach (var w in workspaces)
            w.DemoExpiresAt = null;

        await _unitOfWork.SaveChangesAsync();
    }

    public async Task<int> SweepThrottleRowsAsync(DateTime nowUtc)
    {
        var cutoff = nowUtc.AddDays(-2);
        var stale = await _unitOfWork
            .Repository<AppSetting>()
            .Query()
            .IgnoreQueryFilters()
            .Where(s => s.Key.StartsWith("demo_email_") && s.CreatedAt < cutoff)
            .ToListAsync();

        if (stale.Count == 0)
            return 0;

        _unitOfWork.Repository<AppSetting>().RemoveRange(stale);
        await _unitOfWork.SaveChangesAsync();
        return stale.Count;
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
}
