using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class StatusCatalogService(IUnitOfWork unitOfWork, ICurrentUser currentUser) : IStatusCatalogService
{
    private readonly IUnitOfWork _unitOfWork = unitOfWork;
    private readonly ICurrentUser _currentUser = currentUser;

    // THE single source of truth for comment-status presentation defaults.
    // Rename / recolor / reorder here → every client reflects it on next load.
    public static readonly List<StatusItem> Defaults = new()
    {
        new() { Value = (int)CommentStatus.Open,         Name = "Open",         Label = "Open",      Color = "#2563eb", Order = 1 },
        new() { Value = (int)CommentStatus.ReadyToApply, Name = "ReadyToApply", Label = "Ready",     Color = "#d97706", Order = 2 },
        new() { Value = (int)CommentStatus.Applied,      Name = "Applied",      Label = "Completed", Color = "#16a34a", Order = 3 },
        new() { Value = (int)CommentStatus.Archived,     Name = "Archived",     Label = "Archived",  Color = "#6b7280", Order = 4 },
    };

    public async Task<Result<List<StatusItem>>> GetAllAsync()
    {
        // anonymous + super both resolve scope=null → global+defaults.
        var scope = _currentUser.TenantId;

        // Bypass the global EF query filter and scope explicitly — super-admin would
        // otherwise see every tenant's overrides, and anonymous lacks a filter context.
        var overrides = await _unitOfWork.Repository<StatusPresentation>().Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.DeletedAt == null && (s.OwnerId == null || s.OwnerId == scope))
            .ToListAsync();

        var merged = Defaults.Select(d =>
        {
            // Prefer tenant-specific override (OwnerId == scope, scope != null) over global (OwnerId == null).
            var tenant = scope != null ? overrides.FirstOrDefault(x => x.StatusValue == d.Value && x.OwnerId == scope) : null;
            var global = overrides.FirstOrDefault(x => x.StatusValue == d.Value && x.OwnerId == null);
            var o = tenant ?? global;
            return new StatusItem
            {
                Value = d.Value,
                Name = d.Name,
                Label = o?.Label ?? d.Label,
                Color = o?.Color ?? d.Color,
                Order = o?.DisplayOrder ?? d.Order,
            };
        }).OrderBy(s => s.Order).ToList();

        return Result<List<StatusItem>>.Success(merged);
    }
}
