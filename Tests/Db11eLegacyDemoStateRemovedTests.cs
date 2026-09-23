using System.Reflection;
using Microsoft.AspNetCore.Mvc.Routing;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Pointer.API.Auth;
using Pointer.API.Controllers.Admin;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-11e: the `{id:int}` tenant demo routes, `ResolveWorkspaceIdFromUserIdAsync`, and the four
/// legacy demo columns on `users` are gone — the workspace is the only demo authority. Replaces
/// the deleted <c>Db17TenantsControllerRoutesTests</c> (its Guid-route facts move here).
/// </summary>
public class Db11eLegacyDemoStateRemovedTests
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

    private static AppDbContext Ctx(string dbName, IConfiguration? config = null) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            new FakeCurrentUser(),
            config ?? new ConfigurationBuilder().Build()
        );

    private static MethodInfo Method(string name, Type paramType) =>
        typeof(TenantsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == name && m.GetParameters()[0].ParameterType == paramType);

    [Fact]
    public void TenantsController_GuidDemoRoutes_Present_NotObsolete_AndAudited()
    {
        var extend = Method("ExtendDemo", typeof(Guid));
        Assert.Null(extend.GetCustomAttribute<ObsoleteAttribute>());
        Assert.NotNull(extend.GetCustomAttribute<AuditedAttribute>());

        var setConfig = Method("SetDemoConfig", typeof(Guid));
        Assert.Null(setConfig.GetCustomAttribute<ObsoleteAttribute>());
        Assert.NotNull(setConfig.GetCustomAttribute<AuditedAttribute>());
    }

    [Fact]
    public void TenantsController_HasNoIntKeyedDemoRoutes()
    {
        var methods = typeof(TenantsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .ToList();

        Assert.DoesNotContain(
            methods,
            m =>
                (m.Name == "ExtendDemo" || m.Name == "SetDemoConfig")
                && m.GetParameters()[0].ParameterType == typeof(int)
        );

        Assert.DoesNotContain(
            methods,
            m =>
                m.GetCustomAttributes()
                    .OfType<IRouteTemplateProvider>()
                    .Any(a =>
                        a.Template != null
                        && (
                            a.Template.Contains("{id:int}/extend")
                            || a.Template.Contains("{id:int}/demo-config")
                        )
                    )
        );

        Assert.DoesNotContain(methods, m => m.GetCustomAttribute<ObsoleteAttribute>() != null);

        Assert.Null(
            typeof(TenantsController).GetMethod(
                "ResolveWorkspaceIdFromUserIdAsync",
                BindingFlags.NonPublic | BindingFlags.Instance
            )
        );
    }

    [Fact]
    public void User_HasNoLegacyDemoMembers()
    {
        using var db = Ctx(nameof(User_HasNoLegacyDemoMembers));
        var entityType = db.Model.FindEntityType(typeof(User))!;

        foreach (
            var name in new[]
            {
                "ExpiresAt",
                "DemoExtended",
                "DemoCommentCapOverride",
                "DemoTtlHoursOverride",
            }
        )
        {
            Assert.Null(typeof(User).GetProperty(name));
            Assert.Null(entityType.FindProperty(name));
        }

        Assert.DoesNotContain(
            entityType.GetIndexes(),
            ix => ix.Properties.Any(p => p.Name == "ExpiresAt")
        );

        // Guards over-deletion — these two stay.
        Assert.NotNull(typeof(User).GetProperty("IsDemo"));
        Assert.NotNull(typeof(User).GetProperty("RecipientEmail"));
    }
}
