namespace Pointer.Application.DTOs.Extension;

public class ExtensionActivateResponse
{
    /// <summary>The normalized origin that was recorded / matched.</summary>
    public string Origin { get; set; } = string.Empty;

    /// <summary>Distinct active extension sites for the tenant after this activation.</summary>
    public int SiteCount { get; set; }
}
