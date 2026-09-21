using Pointer.Domain.Enums;

namespace Pointer.Domain.ValueObjects;

/// <summary>
/// One admin-defined comment field (R4-01). Stored as a JSON list on <c>WorkspaceSetting</c>
/// (never its own table), and validated by <c>CommentFieldService.ValidateDefinitions</c> — the
/// single source of truth for the A1 rules. <see cref="Label"/> is single-line by regex because
/// it is printed OUTSIDE the untrusted fence in the CLI apply prompt; stakeholder VALUES are the
/// untrusted part and always stay inside the fence.
/// </summary>
public class CommentFieldDefinition
{
    /// <summary>Permanent machine key, `^[a-z][a-z0-9_]{1,31}$` — never renamed after creation.</summary>
    public string Key { get; set; } = string.Empty;

    /// <summary>Admin-editable display label, 1–40 chars, single line.</summary>
    public string Label { get; set; } = string.Empty;

    public CommentFieldType Type { get; set; } = CommentFieldType.Text;

    /// <summary>Select only (1–20 entries, each 1–40 chars, distinct); empty otherwise.</summary>
    public List<string> Options { get; set; } = new();

    /// <summary>Url only, 0–10 lower-case patterns (`*.example.com` or `example.com`); empty = any host.</summary>
    public List<string> AllowedHosts { get; set; } = new();

    /// <summary>≤ 40 chars, `^[a-z0-9][a-z0-9-]*$` — a hint for the AI (e.g. "atlassian"), never an instruction.</summary>
    public string? SuggestedTool { get; set; }

    /// <summary>≤ 120 chars — placeholder/helper text shown under the widget input.</summary>
    public string? Hint { get; set; }

    /// <summary>Disabled fields are not offered by the widget and rejected on write; stored values remain visible.</summary>
    public bool Enabled { get; set; } = true;

    /// <summary>Ascending display order (normalised to 0..n-1 on save).</summary>
    public int SortOrder { get; set; }
}
