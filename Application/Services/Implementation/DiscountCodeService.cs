using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.DiscountCode;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

/// <inheritdoc cref="IDiscountCodeService" />
public class DiscountCodeService(IUnitOfWork unitOfWork, IAuditWriter? audit = null)
    : IDiscountCodeService
{
    private readonly IAuditWriter _audit = audit ?? NoopAuditWriter.Instance;

    public async Task<Result<List<DiscountCodeResponse>>> ListAsync(string? sort, bool? active)
    {
        var query = unitOfWork.Repository<DiscountCode>().Query().Where(c => c.DeletedAt == null);
        if (active is bool isActive)
            query = query.Where(c => c.IsActive == isActive);

        var codes = await query.Include(c => c.PlanScopes).ToListAsync();
        var counts = await CountsAsync(codes.Select(c => c.Id));

        var responses = codes.Select(c => ToResponse(c, counts)).ToList();

        responses = sort switch
        {
            "most_used" => responses
                .OrderByDescending(r => r.AppliedCount + r.PendingCount)
                .ThenByDescending(r => r.Id)
                .ToList(),
            "code" => responses.OrderBy(r => r.Code, StringComparer.Ordinal).ToList(),
            _ => responses.OrderByDescending(r => r.Id).ToList(), // "newest" (default)
        };

        return Result<List<DiscountCodeResponse>>.Success(responses);
    }

    public async Task<Result<DiscountCodeResponse>> GetAsync(int id)
    {
        var code = await unitOfWork
            .Repository<DiscountCode>()
            .Query()
            .Include(c => c.PlanScopes)
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null);
        if (code == null)
            return Result<DiscountCodeResponse>.NotFound(MessageKeys.DiscountCode.NotFound);

        var counts = await CountsAsync(new[] { id });
        return Result<DiscountCodeResponse>.Success(ToResponse(code, counts));
    }

    public async Task<Result<DiscountCodeResponse>> CreateAsync(CreateDiscountCodeRequest request)
    {
        var normalized = request.Code.Trim().ToUpperInvariant();

        var taken = await unitOfWork
            .Repository<DiscountCode>()
            .Query()
            .AnyAsync(c => c.Code == normalized && c.DeletedAt == null);
        if (taken)
            return Result<DiscountCodeResponse>.Conflict(MessageKeys.DiscountCode.CodeTaken);

        var planIds = (request.PlanIds ?? new List<int>()).Distinct().ToList();
        if (planIds.Count > 0)
        {
            var validCount = await unitOfWork
                .Repository<Plan>()
                .Query()
                .CountAsync(p => planIds.Contains(p.Id) && p.DeletedAt == null);
            if (validCount != planIds.Count)
                return Result<DiscountCodeResponse>.Failure(MessageKeys.Plan.NotFound);
        }

        var code = new DiscountCode
        {
            Code = normalized,
            Label = request.Label?.Trim(),
            Note = request.Note?.Trim(),
            Kind = request.Kind,
            Value = BillingMath.Round(request.Value),
            Currency = request.Currency?.Trim().ToUpperInvariant(),
            Duration = request.Duration,
            ValidFrom = request.ValidFrom,
            ValidUntil = request.ValidUntil,
            MaxRedemptions = request.MaxRedemptions,
            IsActive = request.IsActive,
            PlanScopes = planIds.Select(pid => new DiscountCodePlan { PlanId = pid }).ToList(),
        };

        await unitOfWork.Repository<DiscountCode>().AddAsync(code);
        await unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.DiscountCodeCreated,
                AuditTargets.DiscountCode,
                code.Id.ToString(),
                null,
                After: new Dictionary<string, string>
                {
                    ["kind"] = code.Kind.ToString(),
                    ["discount_code_id"] = code.Id.ToString(),
                }
            )
        );

        return Result<DiscountCodeResponse>.Success(ToResponse(code, await CountsAsync(new[] { code.Id })));
    }

    public async Task<Result<DiscountCodeResponse>> UpdateAsync(int id, UpdateDiscountCodeRequest request)
    {
        var code = await unitOfWork
            .Repository<DiscountCode>()
            .Query()
            .Include(c => c.PlanScopes)
            .FirstOrDefaultAsync(c => c.Id == id && c.DeletedAt == null);
        if (code == null)
            return Result<DiscountCodeResponse>.NotFound(MessageKeys.DiscountCode.NotFound);

        var planIds = (request.PlanIds ?? new List<int>()).Distinct().ToList();
        if (planIds.Count > 0)
        {
            var validCount = await unitOfWork
                .Repository<Plan>()
                .Query()
                .CountAsync(p => planIds.Contains(p.Id) && p.DeletedAt == null);
            if (validCount != planIds.Count)
                return Result<DiscountCodeResponse>.Failure(MessageKeys.Plan.NotFound);
        }

        // Every field but Code (F-B9) — edits never touch existing redemptions' snapshots.
        code.Label = request.Label?.Trim();
        code.Note = request.Note?.Trim();
        code.Kind = request.Kind;
        code.Value = BillingMath.Round(request.Value);
        code.Currency = request.Currency?.Trim().ToUpperInvariant();
        code.Duration = request.Duration;
        code.ValidFrom = request.ValidFrom;
        code.ValidUntil = request.ValidUntil;
        code.MaxRedemptions = request.MaxRedemptions;
        code.IsActive = request.IsActive;

        code.PlanScopes.Clear();
        foreach (var pid in planIds)
            code.PlanScopes.Add(new DiscountCodePlan { DiscountCodeId = id, PlanId = pid });

        unitOfWork.Repository<DiscountCode>().Update(code);
        await unitOfWork.SaveChangesAsync();

        await _audit.WriteAsync(
            new AuditEntry(
                AuditActions.DiscountCodeUpdated,
                AuditTargets.DiscountCode,
                code.Id.ToString(),
                null,
                After: new Dictionary<string, string> { ["kind"] = code.Kind.ToString() }
            )
        );

        return Result<DiscountCodeResponse>.Success(ToResponse(code, await CountsAsync(new[] { id })));
    }

    public async Task<Result<List<DiscountRedemptionResponse>>> GetRedemptionsAsync(int id)
    {
        var exists = await unitOfWork
            .Repository<DiscountCode>()
            .Query()
            .AnyAsync(c => c.Id == id && c.DeletedAt == null);
        if (!exists)
            return Result<List<DiscountRedemptionResponse>>.NotFound(MessageKeys.DiscountCode.NotFound);

        var rows = await unitOfWork
            .DiscountRedemptions.IgnoreQueryFilters()
            .Where(r => r.DiscountCodeId == id)
            .OrderByDescending(r => r.Id)
            .ToListAsync();

        var planNames = await unitOfWork
            .Repository<Plan>()
            .Query()
            .AsNoTracking()
            .Where(p => rows.Select(r => r.PlanId).Distinct().Contains(p.Id))
            .ToDictionaryAsync(p => p.Id, p => p.Name);

        var workspaceIds = rows.Where(r => r.OwnerId != null).Select(r => r.OwnerId!.Value).Distinct().ToList();
        var workspaceNames =
            workspaceIds.Count == 0
                ? new Dictionary<Guid, string>()
                : await unitOfWork
                    .Workspaces.IgnoreQueryFilters()
                    .Where(w => workspaceIds.Contains(w.Id))
                    .ToDictionaryAsync(w => w.Id, w => w.Name);

        var responses = rows
            .Select(r => new DiscountRedemptionResponse
            {
                Id = r.Id,
                WorkspaceId = r.OwnerId,
                WorkspaceName =
                    r.OwnerId is Guid oid && workspaceNames.TryGetValue(oid, out var name)
                        ? name
                        : "Deleted workspace",
                PlanId = r.PlanId,
                PlanName = planNames.GetValueOrDefault(r.PlanId, string.Empty),
                Status = r.Status.ToString(),
                CodeSnapshot = r.CodeSnapshot,
                KindSnapshot = r.KindSnapshot.ToString(),
                DurationSnapshot = r.DurationSnapshot.ToString(),
                ValueSnapshot = r.ValueSnapshot,
                CurrencySnapshot = r.CurrencySnapshot,
                OriginalPrice = r.OriginalPrice,
                DiscountAmount = r.DiscountAmount,
                FinalPrice = r.FinalPrice,
                PriceCurrency = r.PriceCurrency,
                CreatedAt = r.CreatedAt,
                AppliedAt = r.AppliedAt,
                ReleasedAt = r.ReleasedAt,
                ReleaseReason = r.ReleaseReason?.ToString(),
            })
            .ToList();

        return Result<List<DiscountRedemptionResponse>>.Success(responses);
    }

    // ── Shared ────────────────────────────────────────────────────────────────────────────────

    private async Task<Dictionary<int, (int Applied, int Pending)>> CountsAsync(IEnumerable<int> codeIds)
    {
        var ids = codeIds.Distinct().ToList();
        if (ids.Count == 0)
            return new Dictionary<int, (int, int)>();

        var grouped = await unitOfWork
            .DiscountRedemptions.IgnoreQueryFilters()
            .Where(r => ids.Contains(r.DiscountCodeId))
            .GroupBy(r => new { r.DiscountCodeId, r.Status })
            .Select(g => new
            {
                g.Key.DiscountCodeId,
                g.Key.Status,
                Count = g.Count(),
            })
            .ToListAsync();

        var result = new Dictionary<int, (int Applied, int Pending)>();
        foreach (var id in ids)
            result[id] = (0, 0);
        foreach (var g in grouped)
        {
            var (applied, pending) = result[g.DiscountCodeId];
            if (g.Status == DiscountRedemptionStatus.Applied)
                applied = g.Count;
            else if (g.Status == DiscountRedemptionStatus.Pending)
                pending = g.Count;
            result[g.DiscountCodeId] = (applied, pending);
        }
        return result;
    }

    private static DiscountCodeResponse ToResponse(
        DiscountCode c,
        Dictionary<int, (int Applied, int Pending)> counts
    )
    {
        var (applied, pending) = counts.GetValueOrDefault(c.Id, (0, 0));
        return new DiscountCodeResponse
        {
            Id = c.Id,
            Code = c.Code,
            Label = c.Label,
            Note = c.Note,
            Kind = c.Kind.ToString(),
            Value = c.Value,
            Currency = c.Currency,
            Duration = c.Duration.ToString(),
            ValidFrom = c.ValidFrom,
            ValidUntil = c.ValidUntil,
            MaxRedemptions = c.MaxRedemptions,
            IsActive = c.IsActive,
            PlanIds = c.PlanScopes.Select(s => s.PlanId).ToList(),
            AppliedCount = applied,
            PendingCount = pending,
        };
    }
}
