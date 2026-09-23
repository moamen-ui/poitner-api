using System.Text.Json;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Audit;
using Pointer.Application.Resources;
using Pointer.Application.Services.Implementation;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;

namespace Pointer.Tests;

/// <summary>
/// DB-12 §6 test 5 — tenant isolation of the audit read API (copy of TenantQueryFilterTests'
/// shape) plus the D12.5 operator redaction: the workspace view NEVER names the operator.
/// SECURITY-CRITICAL: a failing isolation assertion means a cross-tenant audit-row leak.
/// </summary>
public class AuditQueryFilterTests
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
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private static AppDbContext BuildContext(FakeCurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new ConfigurationBuilder().Build()
        );

    private static AuditEvent Row(
        Guid? ownerId,
        Guid? actorUserId = null,
        AuditActorKind actorKind = AuditActorKind.User,
        string action = AuditActions.WorkspaceRenamed
    ) =>
        new()
        {
            OccurredAt = DateTime.UtcNow,
            OwnerId = ownerId,
            ActorUserId = actorUserId,
            ActorKind = actorKind,
            Action = action,
            TargetType = AuditTargets.Workspace,
        };

    [Fact]
    public void TenantB_SeesNoAuditRowOfA()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.AuditEvents.AddRange(Row(tenantA), Row(tenantA), Row(tenantB), Row(null));
            seed.SaveChanges();
        }

        using var ctx = BuildContext(
            new FakeCurrentUser { TenantId = tenantB, IsSuperAdmin = false },
            dbName
        );
        var results = ctx.AuditEvents.ToList();

        Assert.Single(results);
        Assert.Equal(tenantB, results[0].OwnerId);
    }

    [Fact]
    public async Task ListForWorkspace_TenantBWithWorkspaceIdOfA_StillReturnsOnlyOwnRows()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.AuditEvents.AddRange(Row(tenantA), Row(tenantB));
            seed.SaveChanges();
        }

        var callerB = new FakeCurrentUser { TenantId = tenantB, IsSuperAdmin = false };
        using var ctx = BuildContext(callerB, dbName);
        var svc = new AuditQueryService(new UnitOfWork(ctx), callerB);

        var result = await svc.ListForWorkspaceAsync(new AuditQuery { WorkspaceId = tenantA });

        Assert.True(result.IsSuccess, result.Message ?? "");
        Assert.Single(result.Data!.Items);
        Assert.All(result.Data.Items, dto => Assert.Equal(tenantB, dto.WorkspaceId));
    }

    [Fact]
    public async Task ListAll_SuperAdmin_ReturnsEverythingIncludingNullOwnerRows()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var tenantB = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.AuditEvents.AddRange(Row(tenantA), Row(tenantB), Row(null));
            seed.SaveChanges();
        }

        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = BuildContext(superAdmin, dbName);
        var svc = new AuditQueryService(new UnitOfWork(ctx), superAdmin);

        var result = await svc.ListAllAsync(new AuditQuery());

        Assert.True(result.IsSuccess, result.Message ?? "");
        Assert.Equal(3, result.Data!.Items.Count);
        Assert.Equal(3, result.Data.Pagination.TotalItems);
    }

    [Fact]
    public async Task ListForWorkspace_SuperAdmin_IsForbidden()
    {
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = BuildContext(superAdmin, Guid.NewGuid().ToString());
        var svc = new AuditQueryService(new UnitOfWork(ctx), superAdmin);

        var result = await svc.ListForWorkspaceAsync(new AuditQuery());

        Assert.True(result.IsForbidden);
        Assert.Equal(MessageKeys.Common.Forbidden, result.Message);
    }

    [Fact]
    public async Task ListForWorkspace_PageSizeOver200_Fails()
    {
        var tenantA = Guid.NewGuid();
        var callerA = new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false };
        using var ctx = BuildContext(callerA, Guid.NewGuid().ToString());
        var svc = new AuditQueryService(new UnitOfWork(ctx), callerA);

        var result = await svc.ListForWorkspaceAsync(new AuditQuery { PageSize = 201 });

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Audit.PageSizeTooLarge, result.Message);
    }

    /// <summary>Review finding #9 (LOW): q.Action feeds an unescaped LIKE-prefix — reject anything
    /// that isn't a plain dotted lower-case action prefix before it reaches the query.</summary>
    [Theory]
    [InlineData("member.%")] // SQL LIKE multi-char wildcard — the exact injection this closes
    [InlineData("Member.")] // upper-case — every real action string is lower-case
    [InlineData("member.created; drop table")]
    [InlineData("member.created'")]
    public async Task ListForWorkspace_InvalidActionPrefix_Fails(string action)
    {
        var tenantA = Guid.NewGuid();
        var callerA = new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false };
        using var ctx = BuildContext(callerA, Guid.NewGuid().ToString());
        var svc = new AuditQueryService(new UnitOfWork(ctx), callerA);

        var result = await svc.ListForWorkspaceAsync(new AuditQuery { Action = action });

        Assert.False(result.IsSuccess);
        Assert.Equal(MessageKeys.Audit.InvalidActionPrefix, result.Message);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("member.")]
    [InlineData("member")]
    [InlineData("member._")] // underscore IS in the mandated charset ^[a-z_.]{1,64}$ (e.g. with_password)
    public async Task ListForWorkspace_ValidOrAbsentActionPrefix_Succeeds(string? action)
    {
        var tenantA = Guid.NewGuid();
        var callerA = new FakeCurrentUser { TenantId = tenantA, IsSuperAdmin = false };
        using var ctx = BuildContext(callerA, Guid.NewGuid().ToString());
        var svc = new AuditQueryService(new UnitOfWork(ctx), callerA);

        var result = await svc.ListForWorkspaceAsync(new AuditQuery { Action = action });

        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Redaction_WorkspaceView_NeverNamesTheOperator_AllViewDoes()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();
        var operatorPublicId = Guid.NewGuid();
        var memberPublicId = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            var member = new User
            {
                PublicId = memberPublicId,
                Email = "member@a.com",
                PasswordHash = "h",
                DisplayName = "Ann Member",
                RoleId = 1,
                OwnerId = tenantA,
            };
            seed.Users.Add(member);
            seed.SaveChanges();
            TestSeed.Join(seed, member, tenantA, new Role { Id = 1 });

            seed.Users.Add(
                new User
                {
                    PublicId = operatorPublicId,
                    Email = "op@pointer.io",
                    PasswordHash = "h",
                    DisplayName = "Op Person",
                    RoleId = 1,
                    OwnerId = null, // the operator is not a member of any workspace
                }
            );
            seed.AuditEvents.AddRange(
                Row(
                    tenantA,
                    operatorPublicId,
                    AuditActorKind.Impersonation,
                    AuditActions.TenantStatusChanged
                ),
                Row(
                    tenantA,
                    operatorPublicId,
                    AuditActorKind.SuperAdmin,
                    AuditActions.TenantPlanChanged
                ),
                Row(tenantA, memberPublicId, AuditActorKind.User, AuditActions.MemberUpdated)
            );
            seed.SaveChanges();
        }

        // Workspace view as A's admin.
        var callerA = new FakeCurrentUser
        {
            Id = memberPublicId,
            TenantId = tenantA,
            IsSuperAdmin = false,
        };
        string json;
        using (var ctx = BuildContext(callerA, dbName))
        {
            var svc = new AuditQueryService(new UnitOfWork(ctx), callerA);
            var result = await svc.ListForWorkspaceAsync(new AuditQuery());

            Assert.True(result.IsSuccess, result.Message ?? "");
            var dtos = result.Data!.Items;
            Assert.Equal(3, dtos.Count);

            var operatorRows = dtos.Where(d =>
                    d.ActorKind
                        is nameof(AuditActorKind.SuperAdmin)
                            or nameof(AuditActorKind.Impersonation)
                )
                .ToList();
            Assert.Equal(2, operatorRows.Count);
            Assert.All(
                operatorRows,
                d =>
                {
                    Assert.Null(d.ActorUserId); // the operator's uuid is withheld (D12.5)
                    Assert.Equal(AuditEventDto.OperatorLabel, d.ActorName);
                    Assert.Null(d.UserAgent);
                    Assert.Null(d.IpHash);
                }
            );

            var userRow = dtos.Single(d => d.ActorKind == nameof(AuditActorKind.User));
            Assert.Equal(memberPublicId, userRow.ActorUserId);
            Assert.Equal("Ann Member", userRow.ActorName);

            // The serialised DTO contains neither the operator's uuid nor the operator's name.
            json = JsonSerializer.Serialize(result.Data);
            Assert.DoesNotContain(operatorPublicId.ToString(), json);
            Assert.DoesNotContain("Op Person", json);
        }

        // /all as super admin — the full identity exists only here.
        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using (var ctx = BuildContext(superAdmin, dbName))
        {
            var svc = new AuditQueryService(new UnitOfWork(ctx), superAdmin);
            var result = await svc.ListAllAsync(new AuditQuery { WorkspaceId = tenantA });

            Assert.True(result.IsSuccess, result.Message ?? "");
            Assert.Equal(3, result.Data!.Items.Count);
            Assert.Contains(result.Data.Items, d => d.ActorUserId == operatorPublicId);
            Assert.Contains(result.Data.Items, d => d.ActorName == "Op Person");
        }
    }

    [Fact]
    public async Task ListAll_ActionPrefixFilter_ScopesResults()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenantA = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.AuditEvents.AddRange(
                Row(tenantA, action: AuditActions.MemberCreated),
                Row(tenantA, action: AuditActions.MemberUpdated),
                Row(tenantA, action: AuditActions.WorkspaceRenamed)
            );
            seed.SaveChanges();
        }

        var superAdmin = new FakeCurrentUser { IsSuperAdmin = true };
        using var ctx = BuildContext(superAdmin, dbName);
        var svc = new AuditQueryService(new UnitOfWork(ctx), superAdmin);

        var result = await svc.ListAllAsync(new AuditQuery { Action = "member." });

        Assert.True(result.IsSuccess, result.Message ?? "");
        Assert.Equal(2, result.Data!.Items.Count);
        Assert.All(result.Data.Items, d => Assert.StartsWith("member.", d.Action));
    }
}
