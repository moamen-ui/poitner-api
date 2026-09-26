using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.DiscountCode;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>DB-20 §3.6h / §6: discount code CRUD — create (immutable code), update (every other
/// field), deactivate instead of delete, list with usage counts + sort, redemption drill-down.</summary>
public class Db20DiscountCodeTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true;
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private static AppDbContext Ctx(string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(db).Options,
            new FakeCurrentUser(),
            new ConfigurationBuilder().Build()
        );

    private static DiscountCodeService Service(AppDbContext db, IAuditWriter? audit = null) =>
        new(new UnitOfWork(db), audit);

    [Fact]
    public async Task Create_NormalizesCode_StoresUpperCase()
    {
        var db = Guid.NewGuid().ToString();
        using var ctx = Ctx(db);
        var svc = Service(ctx);

        var result = await svc.CreateAsync(
            new CreateDiscountCodeRequest
            {
                Code = "  save10  ",
                Kind = DiscountKind.Percent,
                Value = 10m,
            }
        );
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("SAVE10", result.Data!.Code);
        Assert.Equal(0, result.Data.AppliedCount);
        Assert.Equal(0, result.Data.PendingCount);
    }

    [Fact]
    public async Task Create_DuplicateCode_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        using (var ctx1 = Ctx(db))
            Assert.True(
                (
                    await Service(ctx1)
                        .CreateAsync(
                            new CreateDiscountCodeRequest
                            {
                                Code = "DUP1",
                                Kind = DiscountKind.Percent,
                                Value = 10m,
                            }
                        )
                ).IsSuccess
            );

        using var ctx = Ctx(db);
        var result = await Service(ctx)
            .CreateAsync(
                new CreateDiscountCodeRequest
                {
                    Code = "dup1",
                    Kind = DiscountKind.Percent,
                    Value = 5m,
                }
            );
        Assert.True(result.IsConflict);
        Assert.Equal(MessageKeys.DiscountCode.CodeTaken, result.Message);
    }

    [Fact]
    public async Task Update_EveryFieldButCode_NeverTouchesCode()
    {
        var db = Guid.NewGuid().ToString();
        int id;
        using (var ctx1 = Ctx(db))
        {
            var created = await Service(ctx1)
                .CreateAsync(
                    new CreateDiscountCodeRequest
                    {
                        Code = "KEEP",
                        Kind = DiscountKind.Percent,
                        Value = 10m,
                        Label = "Old label",
                    }
                );
            id = created.Data!.Id;
        }

        using var ctx = Ctx(db);
        var result = await Service(ctx)
            .UpdateAsync(
                id,
                new UpdateDiscountCodeRequest
                {
                    Label = "New label",
                    Kind = DiscountKind.FixedAmount,
                    Value = 5m,
                    Currency = "USD",
                    IsActive = false,
                }
            );
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal("KEEP", result.Data!.Code); // immutable
        Assert.Equal("New label", result.Data.Label);
        Assert.False(result.Data.IsActive);
    }

    [Fact]
    public async Task List_MostUsedSort_OrdersByAppliedPlusPending()
    {
        var db = Guid.NewGuid().ToString();
        int lowUsageId,
            highUsageId;
        using (var ctx1 = Ctx(db))
        {
            var svc = Service(ctx1);
            var low = await svc.CreateAsync(
                new CreateDiscountCodeRequest
                {
                    Code = "LOW",
                    Kind = DiscountKind.Percent,
                    Value = 10m,
                }
            );
            var high = await svc.CreateAsync(
                new CreateDiscountCodeRequest
                {
                    Code = "HIGH",
                    Kind = DiscountKind.Percent,
                    Value = 10m,
                }
            );
            lowUsageId = low.Data!.Id;
            highUsageId = high.Data!.Id;

            ctx1.DiscountRedemptions.AddRange(
                new DiscountRedemption
                {
                    OwnerId = Guid.NewGuid(),
                    DiscountCodeId = highUsageId,
                    PlanId = 1,
                    Status = DiscountRedemptionStatus.Applied,
                    CodeSnapshot = "HIGH",
                    OriginalPrice = 10,
                    FinalPrice = 9,
                    DiscountAmount = 1,
                    PriceCurrency = "USD",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                    AppliedAt = DateTime.UtcNow,
                },
                new DiscountRedemption
                {
                    OwnerId = Guid.NewGuid(),
                    DiscountCodeId = highUsageId,
                    PlanId = 1,
                    Status = DiscountRedemptionStatus.Pending,
                    CodeSnapshot = "HIGH",
                    OriginalPrice = 10,
                    FinalPrice = 9,
                    DiscountAmount = 1,
                    PriceCurrency = "USD",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                },
                new DiscountRedemption
                {
                    OwnerId = Guid.NewGuid(),
                    DiscountCodeId = lowUsageId,
                    PlanId = 1,
                    Status = DiscountRedemptionStatus.Pending,
                    CodeSnapshot = "LOW",
                    OriginalPrice = 10,
                    FinalPrice = 9,
                    DiscountAmount = 1,
                    PriceCurrency = "USD",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                }
            );
            ctx1.SaveChanges();
        }

        using var ctx = Ctx(db);
        var result = await Service(ctx).ListAsync("most_used", null);
        Assert.True(result.IsSuccess);
        Assert.Equal(highUsageId, result.Data![0].Id);
        Assert.Equal(2, result.Data[0].AppliedCount + result.Data[0].PendingCount);
        Assert.Equal(lowUsageId, result.Data[1].Id);
    }

    [Fact]
    public async Task List_ActiveFilter_ExcludesDeactivated()
    {
        var db = Guid.NewGuid().ToString();
        using (var ctx1 = Ctx(db))
        {
            var svc = Service(ctx1);
            var active = await svc.CreateAsync(
                new CreateDiscountCodeRequest
                {
                    Code = "ACTIVE1",
                    Kind = DiscountKind.Percent,
                    Value = 10m,
                    IsActive = true,
                }
            );
            await svc.UpdateAsync(
                (
                    await svc.CreateAsync(
                        new CreateDiscountCodeRequest
                        {
                            Code = "INACTIVE1",
                            Kind = DiscountKind.Percent,
                            Value = 10m,
                        }
                    )
                ).Data!.Id,
                new UpdateDiscountCodeRequest
                {
                    Kind = DiscountKind.Percent,
                    Value = 10m,
                    IsActive = false,
                }
            );
        }

        using var ctx = Ctx(db);
        var result = await Service(ctx).ListAsync(null, active: true);
        Assert.True(result.IsSuccess);
        Assert.Single(result.Data!);
        Assert.Equal("ACTIVE1", result.Data![0].Code);
    }

    [Fact]
    public async Task GetRedemptions_LeftJoinsWorkspaceName_DeletedWorkspaceFallback()
    {
        var db = Guid.NewGuid().ToString();
        int codeId;
        var liveWorkspace = Guid.NewGuid();
        using (var ctx1 = Ctx(db))
        {
            ctx1.Workspaces.Add(
                new Workspace
                {
                    Id = liveWorkspace,
                    Name = "Acme",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = liveWorkspace,
                }
            );
            ctx1.Plans.Add(
                new Plan
                {
                    Name = "Pro",
                    Slug = "pro",
                    Entitlements = new PlanEntitlements(),
                }
            );
            ctx1.SaveChanges();
            var planId = ctx1.Plans.Single().Id;

            var created = await Service(ctx1)
                .CreateAsync(
                    new CreateDiscountCodeRequest
                    {
                        Code = "DRILL",
                        Kind = DiscountKind.Percent,
                        Value = 10m,
                    }
                );
            codeId = created.Data!.Id;

            ctx1.DiscountRedemptions.AddRange(
                new DiscountRedemption
                {
                    OwnerId = liveWorkspace,
                    DiscountCodeId = codeId,
                    PlanId = planId,
                    Status = DiscountRedemptionStatus.Applied,
                    CodeSnapshot = "DRILL",
                    OriginalPrice = 10,
                    FinalPrice = 9,
                    DiscountAmount = 1,
                    PriceCurrency = "USD",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                    AppliedAt = DateTime.UtcNow,
                },
                new DiscountRedemption
                {
                    OwnerId = null, // workspace was hard-deleted
                    DiscountCodeId = codeId,
                    PlanId = planId,
                    Status = DiscountRedemptionStatus.Released,
                    CodeSnapshot = "DRILL",
                    OriginalPrice = 10,
                    FinalPrice = 9,
                    DiscountAmount = 1,
                    PriceCurrency = "USD",
                    CreatedAt = DateTime.UtcNow,
                    CreatedBy = Guid.NewGuid(),
                    ReleasedAt = DateTime.UtcNow,
                    ReleaseReason = RedemptionReleaseReason.WorkspaceDeleted,
                }
            );
            ctx1.SaveChanges();
        }

        using var ctx = Ctx(db);
        var result = await Service(ctx).GetRedemptionsAsync(codeId);
        Assert.True(result.IsSuccess, result.Message);
        Assert.Equal(2, result.Data!.Count);
        Assert.Contains(result.Data, r => r.WorkspaceName == "Acme");
        Assert.Contains(
            result.Data,
            r => r.WorkspaceName == "Deleted workspace" && r.WorkspaceId == null
        );
    }
}
