using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Tenant;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// The super-admin workspace-invitation surface. The case that justifies this service existing at
/// all is <see cref="Revoke_Refuses_An_Invite_Belonging_To_A_Tenant"/>: the underlying invite
/// service lets a super admin load any invite in the system, so without a guard a "workspace
/// invitations" screen could cancel a customer's own staff invitation.
/// </summary>
public class TenantInviteServiceTests
{
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
    }

    private static AppDbContext Ctx(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());

    /// <summary>Stands in for IInviteService: records calls, never actually mutates.</summary>
    private sealed class SpyInviteService : IInviteService
    {
        public List<int> Revoked { get; } = new();
        public List<(int Id, bool Rotate)> Resent { get; } = new();

        public List<bool> CreateWriteAudit { get; } = new();
        public List<bool> RevokeWriteAudit { get; } = new();
        public List<bool> ResendWriteAudit { get; } = new();

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Invite.InviteResponse>> CreateAsync(
            Pointer.Application.DTOs.Invite.CreateInviteRequest request, bool writeAudit = true)
        {
            CreateWriteAudit.Add(writeAudit);
            throw new NotSupportedException("not exercised here");
        }

        public Task<Pointer.Application.Response.Result<List<Pointer.Application.DTOs.Invite.InviteResponse>>> ListAsync() =>
            throw new NotSupportedException();

        public Task<Pointer.Application.Response.Result> RevokeAsync(int id, bool writeAudit = true)
        {
            Revoked.Add(id);
            RevokeWriteAudit.Add(writeAudit);
            return Task.FromResult(Pointer.Application.Response.Result.Success());
        }

        // Not part of what this spy exercises: these tests cover the tenant-scoping decorator, and
        // rotation has no separate scoping path — it resolves the invite through the same
        // LoadOwnAsync the decorator already guards.
        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Invite.InviteResponse>> RotateQuickLinkAsync(int id) =>
            throw new NotSupportedException();

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Invite.InviteResponse>> ResendAsync(int id, bool rotate = false, bool writeAudit = true)
        {
            Resent.Add((id, rotate));
            ResendWriteAudit.Add(writeAudit);
            return Task.FromResult(
                Pointer.Application.Response.Result<Pointer.Application.DTOs.Invite.InviteResponse>.Success(
                    new Pointer.Application.DTOs.Invite.InviteResponse { Id = id, Url = "https://app.test/join?code=x" }));
        }

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Invite.InvitePreviewResponse>> GetPreviewAsync(string code) =>
            throw new NotSupportedException();

        public Task<Pointer.Application.Response.Result<Pointer.Application.DTOs.Auth.LoginResponse>> AcceptAsync(
            Pointer.Application.DTOs.Invite.AcceptInviteRequest request) => throw new NotSupportedException();
    }

    /// <summary>Seeds one workspace invite (null owner) and one tenant-owned invite.</summary>
    private static (int workspaceInviteId, int tenantInviteId) Seed(string dbName)
    {
        using var db = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, dbName);

        var workspace = new Invite
        {
            OwnerId = null,
            Code = "ws-code",
            Email = "owner@new.test",
            DisplayName = "New Workspace",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            MaxUses = 1,
            Uses = 0,
            CreatedAt = DateTime.UtcNow,
        };
        var tenantOwned = new Invite
        {
            OwnerId = Guid.NewGuid(),
            Code = "tenant-code",
            Email = "member@acme.test",
            ExpiresAt = DateTime.UtcNow.AddDays(7),
            MaxUses = 1,
            Uses = 0,
            CreatedAt = DateTime.UtcNow,
        };

        db.Invites.AddRange(workspace, tenantOwned);
        db.SaveChanges();

        return (workspace.Id, tenantOwned.Id);
    }

    private static (TenantInviteService Service, SpyInviteService Spy) Build(AppDbContext db, ICurrentUser user)
    {
        var spy = new SpyInviteService();
        return (new TenantInviteService(new UnitOfWork(db), spy, user, new FakeAuditWriter()), spy);
    }

    [Fact]
    public async Task Revoke_Refuses_An_Invite_Belonging_To_A_Tenant()
    {
        var name = nameof(Revoke_Refuses_An_Invite_Belonging_To_A_Tenant);
        var (_, tenantInviteId) = Seed(name);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = Ctx(superAdmin, name);
        var (svc, spy) = Build(db, superAdmin);

        var result = await svc.RevokeAsync(tenantInviteId);

        Assert.True(result.IsNotFound);
        Assert.Empty(spy.Revoked); // never reached the underlying service
    }

    [Fact]
    public async Task Revoke_Allows_A_Workspace_Invite()
    {
        var name = nameof(Revoke_Allows_A_Workspace_Invite);
        var (workspaceInviteId, _) = Seed(name);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = Ctx(superAdmin, name);
        var (svc, spy) = Build(db, superAdmin);

        var result = await svc.RevokeAsync(workspaceInviteId);

        Assert.True(result.IsSuccess);
        Assert.Equal(new[] { workspaceInviteId }, spy.Revoked);
        // Review finding #3: the underlying InviteService must not write its own invite.revoked row
        // — TenantInviteService writes tenant_invite.revoked itself, once.
        Assert.Equal(new[] { false }, spy.RevokeWriteAudit);
    }

    [Fact]
    public async Task Resend_Refuses_An_Invite_Belonging_To_A_Tenant()
    {
        var name = nameof(Resend_Refuses_An_Invite_Belonging_To_A_Tenant);
        var (_, tenantInviteId) = Seed(name);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = Ctx(superAdmin, name);
        var (svc, spy) = Build(db, superAdmin);

        var result = await svc.ResendAsync(tenantInviteId, rotate: false);

        Assert.True(result.IsNotFound);
        Assert.Empty(spy.Resent);
    }

    [Fact]
    public async Task List_Shows_Only_Workspace_Invites()
    {
        var name = nameof(List_Shows_Only_Workspace_Invites);
        var (workspaceInviteId, _) = Seed(name);
        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = Ctx(superAdmin, name);
        var (svc, _) = Build(db, superAdmin);

        var result = await svc.ListAsync();

        Assert.True(result.IsSuccess);
        var row = Assert.Single(result.Data!);
        Assert.Equal(workspaceInviteId, row.Id);
        Assert.Equal("owner@new.test", row.Email);
        Assert.Equal("New Workspace", row.DisplayName);
        // The code is a bearer credential; a list endpoint is the wrong place to hand it out.
        Assert.Null(row.Url);
        // Unknown on a list row — a non-nullable false would make the UI warn on every invitation.
        Assert.Null(row.EmailSent);
    }

    [Fact]
    public async Task List_Includes_Invites_Created_Before_Single_Use_Was_Enforced()
    {
        // MaxUses is null on every pre-change invite. `Uses < MaxUses` is NULL in SQL, so a naive
        // filter would hide rows that are still perfectly valid and acceptable.
        var name = nameof(List_Includes_Invites_Created_Before_Single_Use_Was_Enforced);
        Seed(name);

        using (var seed = Ctx(new FakeCurrentUser { IsSuperAdmin = true }, name))
        {
            seed.Invites.Add(new Invite
            {
                OwnerId = null,
                Code = "legacy",
                Email = "legacy@old.test",
                ExpiresAt = DateTime.UtcNow.AddDays(3),
                MaxUses = null,
                Uses = 0,
                CreatedAt = DateTime.UtcNow.AddDays(-1),
            });
            seed.SaveChanges();
        }

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        using var db = Ctx(superAdmin, name);
        var (svc, _) = Build(db, superAdmin);

        var result = await svc.ListAsync();

        Assert.Contains(result.Data!, r => r.Email == "legacy@old.test");
    }

    [Fact]
    public async Task A_Tenant_Admin_Cannot_Reach_The_Surface()
    {
        var name = nameof(A_Tenant_Admin_Cannot_Reach_The_Surface);
        var (workspaceInviteId, _) = Seed(name);
        var tenantAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsAdmin = true, TenantId = Guid.NewGuid() };
        using var db = Ctx(tenantAdmin, name);
        var (svc, spy) = Build(db, tenantAdmin);

        Assert.True((await svc.ListAsync()).IsForbidden);
        Assert.True((await svc.RevokeAsync(workspaceInviteId)).IsForbidden);
        Assert.True((await svc.ResendAsync(workspaceInviteId, false)).IsForbidden);
        Assert.True((await svc.CreateAsync(new CreateTenantInviteRequest { Email = "x@y.test" })).IsForbidden);
        Assert.Empty(spy.Revoked);
        Assert.Empty(spy.Resent);
    }
}
