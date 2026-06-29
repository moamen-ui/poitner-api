namespace Pointer.Application.Services.Interfaces;

public interface ISettingsService
{
    public const string ScopedAdminSignupEnabled = "scoped_admin_signup_enabled";

    Task<bool> GetBoolAsync(string key, bool fallback = false);
    Task SetBoolAsync(string key, bool value);
}
