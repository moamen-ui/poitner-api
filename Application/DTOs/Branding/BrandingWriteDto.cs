namespace Pointer.Application.DTOs.Branding;

public class BrandingUrlsWriteDto
{
    public string? App { get; set; }
    public string? Demo { get; set; }
    public string? Docs { get; set; }
    public string? Landing { get; set; }
}

public class BrandingWriteDto
{
    public string? ProductName { get; set; }
    public string? Tagline { get; set; }
    public string? PrimaryColor { get; set; }
    public BrandingUrlsWriteDto? Urls { get; set; }
}
