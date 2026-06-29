using System.Text.RegularExpressions;
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class StatusAdminService(IUnitOfWork unitOfWork) : IStatusAdminService
{
    private readonly IUnitOfWork _unitOfWork = unitOfWork;

    public async Task<Result<List<StatusAdminItem>>> ListAsync()
    {
        var overrides = await _unitOfWork.Repository<StatusPresentation>().Query()
            .AsNoTracking().Where(s => s.DeletedAt == null).ToListAsync();

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

        var row = await _unitOfWork.Repository<StatusPresentation>().Query()
            .FirstOrDefaultAsync(s => s.StatusValue == value && s.DeletedAt == null);

        if (row == null)
        {
            row = new StatusPresentation { StatusValue = value };
            if (request.Label is not null) row.Label = request.Label;
            if (request.Color is not null) row.Color = request.Color;
            if (request.Order is not null) row.DisplayOrder = request.Order;
            await _unitOfWork.Repository<StatusPresentation>().AddAsync(row);
        }
        else
        {
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

        var row = await _unitOfWork.Repository<StatusPresentation>().Query()
            .FirstOrDefaultAsync(s => s.StatusValue == value && s.DeletedAt == null);

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
        IsOverridden = o != null,
    };
}
