namespace Pointer.Domain.ValueObjects;

/// <summary>
/// One (deduplicated) console.error/console.warn entry captured by the widget while a visitor is on
/// a page. Only ever recorded when a comment is submitted with IsBugReport=true and the owning
/// project has PageContextCaptureEnabled — see PageContextSnapshot.
/// </summary>
public class ConsoleLogEntry
{
    public string Level { get; set; } = "error";
    public string Message { get; set; } = string.Empty;
    public string? Stack { get; set; }

    /// <summary>Consecutive-duplicate collapsing: how many times this (level, message) pair fired.</summary>
    public int Count { get; set; } = 1;
    public DateTime OccurredAt { get; set; }
}
