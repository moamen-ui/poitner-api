namespace Pointer.Application.DTOs.Project;

/// <summary>
/// Posted by pointer-init.md (once, at detection time) or skill.md's self-healing branch (once, if
/// init never ran). `frontend`/`backend` are write-once-if-empty; `aiTool` is append-if-new to the
/// growing AiToolsUsed set — see ProjectService.SetStackAsync for exact semantics.
/// </summary>
public class SetProjectStackRequest
{
    public List<string>? Frontend { get; set; }
    public List<string>? Backend { get; set; }

    /// <summary>Self-identified by the calling AI tool from a fixed vocabulary documented in
    /// skill.md (e.g. "claude-code", "opencode-glm") — there's no reliable runtime signal to
    /// detect this automatically, so it's an honor-system field, same as an HTTP User-Agent.</summary>
    public string? AiTool { get; set; }
}
