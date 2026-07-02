namespace Pointer.Application.DTOs.Branding;

public class BrandingUrlsResponse
{
    public string App { get; set; } = string.Empty;
    public string Demo { get; set; } = string.Empty;
    public string Docs { get; set; } = string.Empty;
    public string Landing { get; set; } = string.Empty;
}

public class BrandingAssetsResponse
{
    public string? Logo { get; set; }
    public string? IconSquare { get; set; }
    public string? Favicon { get; set; }
    public string? AppleTouch { get; set; }
    public string? Pwa192 { get; set; }
    public string? Pwa512 { get; set; }
}

public class BrandingResponse
{
    public string ProductName { get; set; } = string.Empty;
    public string Tagline { get; set; } = string.Empty;
    public string PrimaryColor { get; set; } = string.Empty;
    public BrandingUrlsResponse Urls { get; set; } = new();
    public BrandingAssetsResponse Assets { get; set; } = new();
    public int Version { get; set; }
}
