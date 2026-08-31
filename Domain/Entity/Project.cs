namespace Pointer.Domain.Entity;

public class Project : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;

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

    public Guid? OwnerId { get; set; }
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}
