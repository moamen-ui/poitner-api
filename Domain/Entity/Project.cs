using Pointer.Domain.Enums;

namespace Pointer.Domain.Entity;

public class Project : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;

    // Per-environment activation (keyed by the fixed EnvironmentTag enum — NOT the tenant-defined
    // AppEnvironment catalog used by ProjectAppUrl, a different concept). All 3 off = fully
    // inactive (parity with the old single IsActive=false); see ProjectService.EnsureAsync.
    public bool IsActiveLocal { get; set; } = true;
    public bool IsActiveStaging { get; set; } = true;
    public bool IsActiveProduction { get; set; } = true;

    // Opt-in, default off: whether the widget may buffer console errors/warnings and failed/slow
    // network requests for this project's bug-flagged comments. See
    // docs/superpowers/specs/2026-08-25-page-context-capture-design.md.
    public bool PageContextCaptureEnabled { get; set; } = false;

    // Where this project's widget is embedded — set by the admin so a quick-access client invite
    // (see Role.QuickAccess) knows where to send the invitee. Optional for ordinary projects.
    public string? AppUrl { get; set; }

    // Serialized JSON of {"frontend":["react","tailwind"],"backend":["dotnet","postgres"]},
    // detected once by pointer-init.md (or self-healed by skill.md's first apply run) and never
    // re-detected afterward. Write-once-if-empty — see ProjectService.SetStackAsync.
    public string? TechStack { get; set; }

    // Serialized JSON array of AI coding tools that have registered against this project, e.g.
    // ["claude-code","opencode-glm"] — unlike TechStack, this GROWS over the project's lifetime
    // (more than one tool can legitimately touch the same project) rather than being write-once.
    public string? AiToolsUsed { get; set; }

    // Serialized JSON array of Role.Id — which roles see the widget's environment switcher
    // (the toolbar's Local/Staging/Production select). Null (the default, unconfigured) means
    // "everyone except Client (Role.QuickAccess)" — see ProjectService.ShowEnvironmentSelectorFor.
    public string? EnvironmentSelectorRoleIds { get; set; }

    // Whether the AI apply flow (skill.md's Step 5) bundles every applied comment into one commit,
    // or commits each one separately with its own Comment.CommitUrl. Only an admin or the
    // project's creator may change this (ProjectService.UpdateAsync, same gate as every other
    // project-level setting).
    public CommitStyle CommitStyle { get; set; } = CommitStyle.Single;

    // When true, a comment or reply is only accepted from an origin matching one of this project's
    // active ProjectAppUrl rows (exact or wildcard — see OriginNormalizer). Default false, so every
    // existing project behaves exactly as before. Note what this is and is not: comment creation
    // already requires a JWT, so this is not an anonymous-abuse control — it stops a signed-in
    // stakeholder posting from the wrong site, and is deliberately bypassable by a staff token with
    // no Origin header (that is how the CLI and AI agents post). Flooding is the rate limiter's job.
    public bool EnforceAllowedOrigins { get; set; } = false;

    // When false, the widget emits no text content in the DOM snapshot for any element and sets
    // pageTitle to •••; server-side sanitization enforces this defense-in-depth on comment creation.
    // Default true (additive column).
    public bool CaptureTextContent { get; set; } = true;

    public Guid? OwnerId { get; set; }
    public ICollection<ProjectAppUrl> ProjectAppUrls { get; set; } = new List<ProjectAppUrl>();
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}
