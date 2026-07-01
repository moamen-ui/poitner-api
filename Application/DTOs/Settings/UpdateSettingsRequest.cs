namespace Pointer.Application.DTOs.Settings;

public class UpdateSettingsRequest
{
    public bool ScopedAdminSignupEnabled { get; set; }

    // Email (editable; the API key is set via env, not here).
    public bool EmailEnabled { get; set; }
    public string EmailFromEmail { get; set; } = string.Empty;
    public string EmailFromName { get; set; } = string.Empty;
    public int EmailDailyCap { get; set; }

    // Demo (editable).
    public int DemoMaxActive { get; set; }
    public int DemoTtlHours { get; set; }
    public int DemoPerEmailPerDay { get; set; }
    public int DemoCommentCap { get; set; }
}
