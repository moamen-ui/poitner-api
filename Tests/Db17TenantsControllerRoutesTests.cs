using System.Reflection;
using Pointer.API.Auth;
using Pointer.API.Controllers.Admin;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-17 §3.3 (Opus #3): the `{id:int}` demo routes are kept for one release, marked
/// [Obsolete], and still audited; the new `{workspaceId:guid}` routes sit beside them.
/// </summary>
public class Db17TenantsControllerRoutesTests
{
    private static MethodInfo Method(string name, Type paramType) =>
        typeof(TenantsController)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance | BindingFlags.DeclaredOnly)
            .Single(m => m.Name == name && m.GetParameters()[0].ParameterType == paramType);

    [Fact]
    public void ExtendDemo_IntRoute_IsObsolete_AndAudited()
    {
        var m = Method("ExtendDemo", typeof(int));
        Assert.NotNull(m.GetCustomAttribute<ObsoleteAttribute>());
        Assert.NotNull(m.GetCustomAttribute<AuditedAttribute>());
    }

    [Fact]
    public void SetDemoConfig_IntRoute_IsObsolete_AndAudited()
    {
        var m = Method("SetDemoConfig", typeof(int));
        Assert.NotNull(m.GetCustomAttribute<ObsoleteAttribute>());
        Assert.NotNull(m.GetCustomAttribute<AuditedAttribute>());
    }

    [Fact]
    public void ExtendDemo_GuidRoute_IsNotObsolete_AndAudited()
    {
        var m = Method("ExtendDemo", typeof(Guid));
        Assert.Null(m.GetCustomAttribute<ObsoleteAttribute>());
        Assert.NotNull(m.GetCustomAttribute<AuditedAttribute>());
    }

    [Fact]
    public void SetDemoConfig_GuidRoute_IsNotObsolete_AndAudited()
    {
        var m = Method("SetDemoConfig", typeof(Guid));
        Assert.Null(m.GetCustomAttribute<ObsoleteAttribute>());
        Assert.NotNull(m.GetCustomAttribute<AuditedAttribute>());
    }
}
