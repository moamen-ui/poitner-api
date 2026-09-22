using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Key lifecycle. Note what these tests can and cannot prove: EF InMemory enforces neither the
/// unique <c>hash</c> index nor the partial "one active key per user" index, so the one-active-row
/// invariant is asserted *through the service*; the database-level guarantee is Postgres-only and is
/// verified by the acceptance SQL in R1-06, not here.
/// </summary>
public class ApiKeyServiceTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
    }

    private static AppDbContext Ctx(string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeCurrentUser { IsSuperAdmin = true },
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    private static (Guid publicId, Guid tenant) SeedUser(string dbName)
    {
        var publicId = Guid.NewGuid();
        var tenant = Guid.NewGuid();
        using var db = Ctx(dbName);

        var role = new Role { Name = "Developer", IsActive = true, OwnerId = tenant };
        db.Roles.Add(role);
        db.SaveChanges();

        db.Users.Add(
            new User
            {
                Email = "dev@example.com",
                PasswordHash = "x",
                DisplayName = "Dev",
                RoleId = role.Id,
                PublicId = publicId,
                ApprovalStatus = ApprovalStatus.Approved,
                IsActive = true,
                OwnerId = tenant,
            });
        db.SaveChanges();

        return (publicId, tenant);
    }

    private static ApiKeyService Service(AppDbContext db, TestApiKeyProtector? protector = null) =>
        new(new UnitOfWork(db), protector ?? new TestApiKeyProtector());

    [Fact]
    public async Task GetOrCreate_Mints_Once_And_Is_Stable()
    {
        var name = nameof(GetOrCreate_Mints_Once_And_Is_Stable);
        var (publicId, tenant) = SeedUser(name);
        using var db = Ctx(name);
        var svc = Service(db);

        var first = await svc.GetOrCreateAsync(publicId, tenant);
        var second = await svc.GetOrCreateAsync(publicId, tenant);

        Assert.True(first.Found);
        Assert.StartsWith("ptr_", first.RawKey);
        Assert.Equal(first.RawKey, second.RawKey); // idempotent — does not mint on every read
        Assert.Equal(12, first.Prefix.Length);
        Assert.Equal(1, await db.ApiKeys.IgnoreQueryFilters().CountAsync());
        Assert.Equal(tenant, (await db.ApiKeys.IgnoreQueryFilters().SingleAsync()).OwnerId);
    }

    [Fact]
    public async Task Stores_No_Plaintext()
    {
        var name = nameof(Stores_No_Plaintext);
        var (publicId, tenant) = SeedUser(name);
        using var db = Ctx(name);

        var result = await Service(db).GetOrCreateAsync(publicId, tenant);
        var row = await db.ApiKeys.IgnoreQueryFilters().SingleAsync();

        Assert.NotEqual(result.RawKey, row.Hash);
        Assert.NotEqual(result.RawKey, row.Encrypted);
        Assert.DoesNotContain(result.RawKey![4..], row.Hash); // the random half never appears verbatim
    }

    [Fact]
    public async Task Regenerate_Revokes_The_Old_Key_And_The_Old_Key_Stops_Resolving()
    {
        var name = nameof(Regenerate_Revokes_The_Old_Key_And_The_Old_Key_Stops_Resolving);
        var (publicId, tenant) = SeedUser(name);
        using var db = Ctx(name);
        var svc = Service(db);

        var original = await svc.GetOrCreateAsync(publicId, tenant);
        var replacement = await svc.RegenerateAsync(publicId, tenant);

        Assert.NotEqual(original.RawKey, replacement.RawKey);
        Assert.Null(await svc.ResolveAsync(original.RawKey!));
        Assert.NotNull(await svc.ResolveAsync(replacement.RawKey!));

        // One active row, and the old one is kept (revoked) rather than deleted.
        Assert.Equal(2, await db.ApiKeys.IgnoreQueryFilters().CountAsync());
        Assert.Equal(1, await db.ApiKeys.IgnoreQueryFilters().CountAsync(k => k.RevokedAt == null));
    }

    [Fact]
    public async Task Resolve_Returns_The_User_And_Ignores_Unknown_Keys()
    {
        var name = nameof(Resolve_Returns_The_User_And_Ignores_Unknown_Keys);
        var (publicId, tenant) = SeedUser(name);
        using var db = Ctx(name);
        var svc = Service(db);

        var minted = await svc.GetOrCreateAsync(publicId, tenant);

        var resolved = await svc.ResolveAsync(minted.RawKey!);
        Assert.NotNull(resolved);
        Assert.Equal("dev@example.com", resolved!.User.Email);

        Assert.Null(await svc.ResolveAsync("ptr_not_a_real_key"));
        Assert.Null(await svc.ResolveAsync(""));
    }

    [Fact]
    public async Task An_Undecryptable_Key_Is_Reported_Not_Replaced()
    {
        // The rotated-encryption-key case. Regenerating here would silently invalidate every
        // developer's stored key the moment they opened their profile page.
        var name = nameof(An_Undecryptable_Key_Is_Reported_Not_Replaced);
        var (publicId, tenant) = SeedUser(name);
        using var db = Ctx(name);
        var protector = new TestApiKeyProtector();
        var svc = Service(db, protector);

        var original = await svc.GetOrCreateAsync(publicId, tenant);
        protector.FailDecrypt = true;

        var afterRotation = await svc.GetOrCreateAsync(publicId, tenant);

        Assert.True(afterRotation.Found); // exists…
        Assert.Null(afterRotation.RawKey); // …but cannot be displayed
        Assert.Equal(1, await db.ApiKeys.IgnoreQueryFilters().CountAsync()); // nothing minted
        Assert.Equal(0, await db.ApiKeys.IgnoreQueryFilters().CountAsync(k => k.RevokedAt != null)); // nothing revoked

        // And the key still works for login, because that path never needed decryption.
        protector.FailDecrypt = false;
        Assert.NotNull(await svc.ResolveAsync(original.RawKey!));
    }

    [Fact]
    public async Task TouchLastUsed_Is_Throttled()
    {
        var name = nameof(TouchLastUsed_Is_Throttled);
        var (publicId, tenant) = SeedUser(name);
        using var db = Ctx(name);
        var svc = Service(db);

        await svc.GetOrCreateAsync(publicId, tenant);
        var key = await db.ApiKeys.IgnoreQueryFilters().SingleAsync();

        await svc.TouchLastUsedAsync(key.Id);
        var first = (await db.ApiKeys.IgnoreQueryFilters().SingleAsync()).LastUsedAt;
        Assert.NotNull(first);

        await svc.TouchLastUsedAsync(key.Id);
        Assert.Equal(first, (await db.ApiKeys.IgnoreQueryFilters().SingleAsync()).LastUsedAt);
    }

    [Fact]
    public async Task Unknown_User_Is_NotFound_Rather_Than_A_Minted_Key()
    {
        var name = nameof(Unknown_User_Is_NotFound_Rather_Than_A_Minted_Key);
        SeedUser(name);
        using var db = Ctx(name);

        var result = await Service(db).GetOrCreateAsync(Guid.NewGuid(), null);

        Assert.False(result.Found);
        Assert.Null(result.RawKey);
        Assert.Equal(0, await db.ApiKeys.IgnoreQueryFilters().CountAsync());
    }
}
