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

    public Guid? OwnerId { get; set; }
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}
