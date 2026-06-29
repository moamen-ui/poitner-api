using System.Text.RegularExpressions;
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

        // Inline validation
        if (request.Label is not null)
        {
            if (string.IsNullOrWhiteSpace(request.Label))
                return Result<StatusAdminItem>.Failure("Label must not be empty.");
            if (request.Label.Length > 64)
                return Result<StatusAdminItem>.Failure("Label must be 64 characters or fewer.");
        }

        if (request.Color is not null)
        {
            if (!Regex.IsMatch(request.Color, "^#[0-9a-fA-F]{6}$"))
                return Result<StatusAdminItem>.Failure("Color must be a valid hex color (e.g. #0ea5e9).");
        }

        if (request.Order is not null && request.Order < 0)
            return Result<StatusAdminItem>.Failure("Order must be 0 or greater.");

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
