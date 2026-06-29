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
}
