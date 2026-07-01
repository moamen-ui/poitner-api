namespace Pointer.Application.DTOs.PredefinedAction;

/// <summary>
/// Widget-facing effective-set item. Deliberately carries NO prompt — the prompt is
/// admin/LLM-only and must never reach the browser.
/// </summary>
public class PredefinedActionOption
{
    public int Id { get; set; }
    public string Text { get; set; } = string.Empty;
}
