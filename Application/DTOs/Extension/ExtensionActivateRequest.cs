namespace Pointer.Application.DTOs.Extension;

public class ExtensionActivateRequest
{
    /// <summary>The project key the extension is activating for (resolved like the widget).</summary>
    public string ProjectKey { get; set; } = string.Empty;

    /// <summary>The origin (scheme + host[:port]) the extension is running on. Normalized server-side.</summary>
    public string Origin { get; set; } = string.Empty;
}
