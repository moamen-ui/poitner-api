using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Plan;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>PlanService CRUD: create/update, delete-Free / delete-in-use → Conflict, public hides Hidden.</summary>
public class PlanServiceTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; } = true;
        public Guid? TenantId { get; set; }
    }

    private static AppDbContext Ctx(string db) =>
        new(new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(db).Options, new FakeCurrentUser());

    private static PlanService Svc(AppDbContext db) => new(new UnitOfWork(db));

    private static PlanWriteDto Write(string slug, string name) => new()
    {
        Name = name,
        Slug = slug,
        PriceMonthly = 9.99m,
        Currency = "USD",
        Interval = BillingInterval.Monthly,
        SortOrder = 1,
        IsActive = true,
        DisplayState = PlanDisplayState.Visible,
        FeatureBullets = new() { "bullet" },
        Entitlements = new PlanEntitlementsDto { MaxProjects = 10 }
    };

    [Fact]
    public async Task Create_Then_List_RoundTrips_Entitlements()
    {
        var db = Guid.NewGuid().ToString();
        var svc = Svc(Ctx(db));
        var created = await svc.CreateAsync(Write("pro", "Pro"));
        Assert.True(created.IsSuccess);
        Assert.Equal(10, created.Data!.Entitlements.MaxProjects);

        var list = await Svc(Ctx(db)).ListAsync();
        Assert.Contains(list.Data!, p => p.Slug == "pro" && p.Entitlements.MaxProjects == 10);
    }

    [Fact]
    public async Task Delete_Free_IsBlocked_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        using (var seed = Ctx(db))
        {
            seed.Plans.Add(new Plan { Name = "Free", Slug = "free", Entitlements = new PlanEntitlements() });
            seed.SaveChanges();
        }
        var freeId = Ctx(db).Plans.Single(p => p.Slug == "free").Id;
        var res = await Svc(Ctx(db)).DeleteAsync(freeId);
        Assert.True(res.IsConflict);
    }

    [Fact]
    public async Task Delete_PlanWithActiveSubscriptions_IsBlocked_Conflict()
    {
        var db = Guid.NewGuid().ToString();
        int proId;
        using (var seed = Ctx(db))
        {
            var pro = new Plan { Name = "Pro", Slug = "pro", Entitlements = new PlanEntitlements() };
            seed.Plans.Add(pro);
            seed.SaveChanges();
            proId = pro.Id;
            seed.Subscriptions.Add(new Subscription { OwnerId = Guid.NewGuid(), PlanId = proId, Status = SubscriptionStatus.Active });
            seed.SaveChanges();
        }

        var res = await Svc(Ctx(db)).DeleteAsync(proId);
        Assert.True(res.IsConflict);
    }

    [Fact]
    public async Task Delete_UnusedNonFreePlan_Succeeds_SoftDelete()
    {
        var db = Guid.NewGuid().ToString();
        int proId;
        using (var seed = Ctx(db))
        {
            var pro = new Plan { Name = "Pro", Slug = "pro", Entitlements = new PlanEntitlements() };
            seed.Plans.Add(pro);
            seed.SaveChanges();
            proId = pro.Id;
        }

        var res = await Svc(Ctx(db)).DeleteAsync(proId);
        Assert.True(res.IsSuccess);
        Assert.NotNull(Ctx(db).Plans.IgnoreQueryFilters().Single(p => p.Id == proId).DeletedAt);
    }

    [Fact]
    public async Task Public_HidesHidden_And_SoftDeleted_OrdersBySortOrder()
    {
        var db = Guid.NewGuid().ToString();
        using (var seed = Ctx(db))
        {
            seed.Plans.Add(new Plan { Name = "Free", Slug = "free", SortOrder = 0, DisplayState = PlanDisplayState.Visible, Entitlements = new PlanEntitlements() });
            seed.Plans.Add(new Plan { Name = "Coming", Slug = "coming", SortOrder = 1, DisplayState = PlanDisplayState.ComingSoon, Entitlements = new PlanEntitlements() });
            seed.Plans.Add(new Plan { Name = "Secret", Slug = "secret", SortOrder = 2, DisplayState = PlanDisplayState.Hidden, Entitlements = new PlanEntitlements() });
            seed.Plans.Add(new Plan { Name = "Gone", Slug = "gone", SortOrder = 3, DisplayState = PlanDisplayState.Visible, DeletedAt = DateTime.UtcNow, Entitlements = new PlanEntitlements() });
            seed.SaveChanges();
        }

        var res = await Svc(Ctx(db)).ListPublicAsync();
        Assert.True(res.IsSuccess);
        var slugs = res.Data!.Select(p => p.Slug).ToList();
        Assert.Equal(new[] { "free", "coming" }, slugs); // Hidden + soft-deleted excluded, ordered by SortOrder
        // Public projection carries no entitlement data (marketing fields only).
        Assert.DoesNotContain("MaxProjects", System.Text.Json.JsonSerializer.Serialize(res.Data));
    }
}
