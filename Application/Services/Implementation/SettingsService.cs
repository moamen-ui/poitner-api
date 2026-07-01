using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

public class SettingsService(IUnitOfWork unitOfWork) : ISettingsService
{
    public async Task<bool> GetBoolAsync(string key, bool fallback = false)
    {
        var setting = await unitOfWork.Repository<AppSetting>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == key);

        if (setting == null) return fallback;
        return setting.Value.Equals("true", StringComparison.OrdinalIgnoreCase);
    }

    public async Task SetBoolAsync(string key, bool value)
    {
        var setting = await unitOfWork.Repository<AppSetting>()
            .Query()
            .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == key);

        if (setting == null)
        {
            await unitOfWork.Repository<AppSetting>().AddAsync(new AppSetting
            {
                Key = key,
                Value = value ? "true" : "false"
            });
        }
        else
        {
            setting.Value = value ? "true" : "false";
            unitOfWork.Repository<AppSetting>().Update(setting);
        }

        await unitOfWork.SaveChangesAsync();
    }

    public async Task<string> GetStringAsync(string key, string fallback = "")
    {
        var setting = await unitOfWork.Repository<AppSetting>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == key);

        return string.IsNullOrWhiteSpace(setting?.Value) ? fallback : setting!.Value;
    }

    public Task SetStringAsync(string key, string value) => UpsertAsync(key, value ?? string.Empty);

    public async Task<int> GetIntAsync(string key, int fallback = 0)
    {
        var setting = await unitOfWork.Repository<AppSetting>()
            .Query()
            .AsNoTracking()
            .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == key);

        return int.TryParse(setting?.Value, out var n) ? n : fallback;
    }

    public Task SetIntAsync(string key, int value) => UpsertAsync(key, value.ToString());

    private async Task UpsertAsync(string key, string value)
    {
        var setting = await unitOfWork.Repository<AppSetting>()
            .Query()
            .FirstOrDefaultAsync(s => s.DeletedAt == null && s.Key == key);

        if (setting == null)
            await unitOfWork.Repository<AppSetting>().AddAsync(new AppSetting { Key = key, Value = value });
        else
        {
            setting.Value = value;
            unitOfWork.Repository<AppSetting>().Update(setting);
        }

        await unitOfWork.SaveChangesAsync();
    }
}
