using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class StatusAdminService(IUnitOfWork unitOfWork, ICurrentUser currentUser) : IStatusAdminService
{
    private readonly IUnitOfWork _unitOfWork = unitOfWork;
    private readonly ICurrentUser _currentUser = currentUser;

    public async Task<Result<List<StatusAdminItem>>> ListAsync()
    {
        // Each admin sees ONLY their own layer's overrides (super→global, scoped→their tenant).
        var owner = TenantStamp.OwnerFor(_currentUser);

        var overrides = await _unitOfWork.Repository<StatusPresentation>().Query()
            .IgnoreQueryFilters()
            .AsNoTracking()
            .Where(s => s.DeletedAt == null && s.OwnerId == owner)
            .ToListAsync();

        var items = StatusCatalogService.Defaults.Select(d => Merge(d, overrides.FirstOrDefault(o => o.StatusValue == d.Value))).ToList();
        return Result<List<StatusAdminItem>>.Success(items);
    }

    public async Task<Result<StatusAdminItem>> UpsertAsync(int value, UpdateStatusPresentationRequest request)
    {
        if (!Enum.IsDefined(typeof(CommentStatus), value))
            return Result<StatusAdminItem>.NotFound("Unknown status");

        // Label/Color/Order format is enforced upfront by UpdateStatusPresentationValidator
        // (FluentValidation auto-validation) — not re-checked here.

        var owner = TenantStamp.OwnerFor(_currentUser);

        // Intentionally ignore soft-delete so a previously reset row is revived
        // rather than causing a unique-constraint violation on status_value + owner.
        var row = await _unitOfWork.Repository<StatusPresentation>().Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.StatusValue == value && s.OwnerId == owner);

        if (row == null)
        {
            row = new StatusPresentation { StatusValue = value, OwnerId = owner };
            if (request.Label is not null) row.Label = request.Label;
            if (request.Color is not null) row.Color = request.Color;
            if (request.Order is not null) row.DisplayOrder = request.Order;
            await _unitOfWork.Repository<StatusPresentation>().AddAsync(row);
        }
        else
        {
            // Revive if previously soft-deleted
            if (row.DeletedAt != null)
            {
                row.DeletedAt = null;
                row.DeletedBy = null;
            }
            if (request.Label is not null) row.Label = request.Label;
            if (request.Color is not null) row.Color = request.Color;
            if (request.Order is not null) row.DisplayOrder = request.Order;
            _unitOfWork.Repository<StatusPresentation>().Update(row);
        }

        await _unitOfWork.SaveChangesAsync();

        var def = StatusCatalogService.Defaults.First(d => d.Value == value);
        return Result<StatusAdminItem>.Success(Merge(def, row));
    }

    public async Task<Result> ResetAsync(int value)
    {
        if (!Enum.IsDefined(typeof(CommentStatus), value))
            return Result.NotFound("Unknown status");

        var owner = TenantStamp.OwnerFor(_currentUser);

        var row = await _unitOfWork.Repository<StatusPresentation>().Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(s => s.StatusValue == value && s.OwnerId == owner && s.DeletedAt == null);

        if (row == null)
            return Result.Success();

        row.DeletedAt = DateTime.UtcNow;
        _unitOfWork.Repository<StatusPresentation>().Update(row);
        await _unitOfWork.SaveChangesAsync();

        return Result.Success();
    }

    private static StatusAdminItem Merge(StatusItem def, StatusPresentation? o) => new()
    {
        Value = def.Value,
        Name = def.Name,
        DefaultLabel = def.Label,
        DefaultColor = def.Color,
        DefaultOrder = def.Order,
        Label = o?.Label ?? def.Label,
        Color = o?.Color ?? def.Color,
        Order = o?.DisplayOrder ?? def.Order,
        IsOverridden = o != null && o.DeletedAt == null,
    };
}
