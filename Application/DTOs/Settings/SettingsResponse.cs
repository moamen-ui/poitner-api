namespace Pointer.Application.DTOs.Settings;

public class SettingsResponse
{
    public bool ScopedAdminSignupEnabled { get; set; }

    // Email (super-admin editable). The API key itself is never returned — only whether one is set.
    public bool EmailEnabled { get; set; }
    public string EmailFromEmail { get; set; } = string.Empty;
    public string EmailFromName { get; set; } = string.Empty;
    public int EmailDailyCap { get; set; }
    public bool EmailApiKeyConfigured { get; set; }

    // Demo (super-admin editable).
    public int DemoMaxActive { get; set; }
    public int DemoTtlHours { get; set; }
    public int DemoPerEmailPerDay { get; set; }
    public int DemoCommentCap { get; set; }
}
