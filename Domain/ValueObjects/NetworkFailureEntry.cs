namespace Pointer.Domain.ValueObjects;

/// <summary>
/// One failed (4xx/5xx/network-error) or notably slow request captured by the widget. Metadata only —
/// never headers or request/response bodies — and the URL has its query string stripped before it's
/// ever sent, since query params can carry tokens/session ids.
/// </summary>
public class NetworkFailureEntry
{
    public string Method { get; set; } = string.Empty;
    public string Url { get; set; } = string.Empty;

    /// <summary>Null means a network-level failure (no response at all), not an HTTP status.</summary>
    public int? StatusCode { get; set; }
    public int DurationMs { get; set; }
    public DateTime OccurredAt { get; set; }
}
