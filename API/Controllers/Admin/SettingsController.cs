using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Pointer.API.Auth;
using Pointer.Application.DTOs.Settings;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;

namespace Pointer.API.Controllers.Admin;

[ApiController]
[Route("api/admin/settings")]
[Authorize(Policy = Policies.SuperAdmin)]
[Tags("Settings")]
public class SettingsController(ISettingsService settingsService, IConfiguration configuration) : ControllerBase
{
    private const int DefaultDailyCap = 250;
    private const int DefaultDemoMaxActive = 100;
    private const int DefaultDemoTtlHours = 24;
    private const int DefaultDemoPerEmailPerDay = 3;
    private const int DefaultDemoCommentCap = 10;

    [HttpGet]
    [ProducesResponseType(typeof(Result<SettingsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Get()
    {
        return Ok(Result<SettingsResponse>.Success(await BuildResponseAsync()));
    }

    [HttpPut]
    [ProducesResponseType(typeof(Result<SettingsResponse>), StatusCodes.Status200OK)]
    public async Task<IActionResult> Update([FromBody] UpdateSettingsRequest request)
    {
        await settingsService.SetBoolAsync(ISettingsService.ScopedAdminSignupEnabled, request.ScopedAdminSignupEnabled);

        // Email
        await settingsService.SetBoolAsync(ISettingsService.EmailEnabled, request.EmailEnabled);
        await settingsService.SetStringAsync(ISettingsService.EmailFromEmail, request.EmailFromEmail?.Trim() ?? string.Empty);
        await settingsService.SetStringAsync(ISettingsService.EmailFromName, request.EmailFromName?.Trim() ?? string.Empty);
        await settingsService.SetIntAsync(ISettingsService.EmailDailyCap, request.EmailDailyCap > 0 ? request.EmailDailyCap : DefaultDailyCap);

        // Demo (clamp to sane minimums so a bad value can't disable the demo entirely).
        await settingsService.SetIntAsync(ISettingsService.DemoMaxActive, request.DemoMaxActive > 0 ? request.DemoMaxActive : DefaultDemoMaxActive);
        await settingsService.SetIntAsync(ISettingsService.DemoTtlHours, request.DemoTtlHours > 0 ? request.DemoTtlHours : DefaultDemoTtlHours);
        await settingsService.SetIntAsync(ISettingsService.DemoPerEmailPerDay, request.DemoPerEmailPerDay > 0 ? request.DemoPerEmailPerDay : DefaultDemoPerEmailPerDay);
        await settingsService.SetIntAsync(ISettingsService.DemoCommentCap, request.DemoCommentCap > 0 ? request.DemoCommentCap : DefaultDemoCommentCap);

        return Ok(Result<SettingsResponse>.Success(await BuildResponseAsync()));
    }

    private async Task<SettingsResponse> BuildResponseAsync()
    {
        // From-email/name fall back to the env config when not yet overridden in the DB, so the
        // page shows the effective value. The API key is a secret — only its presence is reported.
        return new SettingsResponse
        {
            ScopedAdminSignupEnabled = await settingsService.GetBoolAsync(ISettingsService.ScopedAdminSignupEnabled),
            EmailEnabled = await settingsService.GetBoolAsync(ISettingsService.EmailEnabled),
            EmailFromEmail = await settingsService.GetStringAsync(ISettingsService.EmailFromEmail, configuration["Email:FromEmail"] ?? string.Empty),
            EmailFromName = await settingsService.GetStringAsync(ISettingsService.EmailFromName, configuration["Email:FromName"] ?? "Pointer"),
            EmailDailyCap = await settingsService.GetIntAsync(ISettingsService.EmailDailyCap, DefaultDailyCap),
            EmailApiKeyConfigured = !string.IsNullOrWhiteSpace(configuration["Email:ApiKey"]),
            DemoMaxActive = await settingsService.GetIntAsync(ISettingsService.DemoMaxActive, DefaultDemoMaxActive),
            DemoTtlHours = await settingsService.GetIntAsync(ISettingsService.DemoTtlHours, DefaultDemoTtlHours),
            DemoPerEmailPerDay = await settingsService.GetIntAsync(ISettingsService.DemoPerEmailPerDay, DefaultDemoPerEmailPerDay),
            DemoCommentCap = await settingsService.GetIntAsync(ISettingsService.DemoCommentCap, DefaultDemoCommentCap),
        };
    }
}
