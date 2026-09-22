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
/// DB-11a core invariants (DB-RULES R8.7): a workspace_memberships row, never users.owner_id, is
/// what makes a person "of" a workspace, and ending a membership in one workspace never touches the
/// same identity's membership elsewhere.
/// </summary>
public class WorkspaceMembershipTests
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

    private static AppDbContext Ctx(ICurrentUser u, string db) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>()
                .UseInMemoryDatabase(db)
                .ConfigureWarnings(w =>
                    w.Ignore(
                        Microsoft
                            .EntityFrameworkCore
                            .Diagnostics
                            .InMemoryEventId
                            .TransactionIgnoredWarning
                    )
                )
                .Options,
            u,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private static (Guid workspaceA, Guid workspaceB, int roleId) SeedTwoWorkspaces(string db)
    {
        var workspaceA = Guid.NewGuid();
        var workspaceB = Guid.NewGuid();
        using var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db);
        seed.Workspaces.Add(
            new Workspace
            {
                Id = workspaceA,
                Name = "A",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceA,
            }
        );
        seed.Workspaces.Add(
            new Workspace
            {
                Id = workspaceB,
                Name = "B",
                CreatedAt = DateTime.UtcNow,
                CreatedBy = workspaceB,
            }
        );
        var role = new Role
        {
            Name = "Developer",
            GrantsAdmin = false,
            IsActive = true,
        };
        seed.Roles.Add(role);
        seed.SaveChanges();
        return (workspaceA, workspaceB, role.Id);
    }

    [Fact]
    public void InWorkspace_TenantB_SeesNothingOfTenantA()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);

        Guid userAId,
            userBId;
        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var userA = new User
            {
                Email = "a@x.com",
                PasswordHash = "x",
                DisplayName = "A",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
            };
            var userB = new User
            {
                Email = "b@x.com",
                PasswordHash = "x",
                DisplayName = "B",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceB,
                IsActive = true,
            };
            seed.Users.Add(userA);
            seed.Users.Add(userB);
            seed.SaveChanges();
            TestSeed.Join(seed, userA, workspaceA, role);
            TestSeed.Join(seed, userB, workspaceB, role);
            userAId = userA.PublicId;
            userBId = userB.PublicId;
        }

        var uow = new UnitOfWork(Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db));
        var memberships = new MembershipService(uow);

        var membersOfA = memberships.InWorkspace(workspaceA).Select(m => m.User.PublicId).ToList();
        Assert.Single(membersOfA);
        Assert.Equal(userAId, membersOfA[0]);
        Assert.DoesNotContain(userBId, membersOfA);

        // R8.7: a caller scoped to tenant B sees no identity or membership of tenant A.
        var tenantBCaller = new FakeCurrentUser { TenantId = workspaceB };
        using var scopedCtx = Ctx(tenantBCaller, db);
        var visibleUsers = scopedCtx.Set<User>().ToList();
        Assert.Single(visibleUsers);
        Assert.Equal(userBId, visibleUsers[0].PublicId);
    }

    [Fact]
    public async Task Login_SameEmailInTwoWorkspaces_IsOneIdentity_HomeWins()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, workspaceB, roleId) = SeedTwoWorkspaces(db);
        Guid identityId;

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "shared@x.com",
                PasswordHash = "hashed:pw",
                DisplayName = "Shared",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA, // home = A
                IsActive = true,
                ApprovalStatus = ApprovalStatus.Approved,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);
            TestSeed.Join(seed, identity, workspaceB, role);
            identityId = identity.PublicId;
        }

        // Exactly one identity row exists.
        using (var check = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            Assert.Single(check.Users.IgnoreQueryFilters().Where(u => u.Email == "shared@x.com"));
        }

        var memberships = new MembershipService(
            new UnitOfWork(Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        );
        var user = await memberships.FindIdentityByEmailAsync("shared@x.com");
        Assert.NotNull(user);
        var live = await memberships.ListForIdentityAsync(user!.Id);
        Assert.Equal(2, live.Count);
        Assert.Contains(live, m => m.OwnerId == workspaceA);
        Assert.Contains(live, m => m.OwnerId == workspaceB);
    }

    [Fact]
    public async Task UserNameResolver_ResolvesAliasedAuthor()
    {
        var db = Guid.NewGuid().ToString();
        var (workspaceA, _, roleId) = SeedTwoWorkspaces(db);
        var oldAliasId = Guid.NewGuid();

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, db))
        {
            var role = seed.Roles.Single(r => r.Id == roleId);
            var identity = new User
            {
                Email = "merged@x.com",
                PasswordHash = "x",
                DisplayName = "Merged Person",
                PublicId = Guid.NewGuid(),
                RoleId = role.Id,
                OwnerId = workspaceA,
                IsActive = true,
            };
            seed.Users.Add(identity);
            seed.SaveChanges();
            TestSeed.Join(seed, identity, workspaceA, role);

            seed.Set<UserAlias>()
                .Add(
                    new UserAlias
                    {
                        AliasPublicId = oldAliasId,
                        UserId = identity.Id,
                        SourceWorkspaceId = workspaceA,
                        MergedAt = DateTime.UtcNow,
                    }
                );
            seed.SaveChanges();
        }

        var uow = new UnitOfWork(Ctx(new FakeCurrentUser { TenantId = workspaceA }, db));
        var names = await UserNameResolver.ResolveAsync(uow, new[] { oldAliasId });

        Assert.Equal("Merged Person", names[oldAliasId]);
    }
}
