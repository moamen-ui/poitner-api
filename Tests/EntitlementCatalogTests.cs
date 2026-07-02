using Pointer.Application.Common;
using Pointer.Domain.ValueObjects;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Keeps <see cref="EntitlementCatalog"/> and <see cref="PlanEntitlements"/> in sync: the catalog key
/// set MUST equal the VO property set. Adding a lever = a new VO property + a catalog entry; this test
/// fails loudly if only one side was edited.
/// </summary>
public class EntitlementCatalogTests
{
    [Fact]
    public void CatalogKeys_Equal_VoPropertyNames()
    {
        var catalogKeys = EntitlementCatalog.All.Keys.ToHashSet();
        var voProps = EntitlementCatalog.VoPropertyNames.ToHashSet();

        Assert.Equal(voProps, catalogKeys);
    }

    [Fact]
    public void MissingIntKey_ResolvesTo_CatalogDefault_NotZero()
    {
        var empty = new PlanEntitlements(); // all null
        // maxProjects default is 3 in the catalog, must NOT resolve to 0.
        var resolved = EntitlementCatalog.ResolveInt(empty, EntitlementCatalog.MaxProjects);
        Assert.Equal(3, resolved);
        Assert.NotEqual(0, resolved);
    }

    [Fact]
    public void MissingBoolKey_ResolvesTo_CatalogDefault()
    {
        var empty = new PlanEntitlements();
        Assert.False(EntitlementCatalog.ResolveBool(empty, EntitlementCatalog.ExtensionEnabled));
    }

    [Fact]
    public void StoredValue_Wins_OverDefault()
    {
        var e = new PlanEntitlements { MaxProjects = 42, ExtensionEnabled = true };
        Assert.Equal(42, EntitlementCatalog.ResolveInt(e, EntitlementCatalog.MaxProjects));
        Assert.True(EntitlementCatalog.ResolveBool(e, EntitlementCatalog.ExtensionEnabled));
    }

    [Fact]
    public void SevenEnforcedLevers_AreFlagged()
    {
        var enforced = EntitlementCatalog.Enforced.Select(s => s.Key).ToHashSet();
        Assert.Equal(7, enforced.Count);
        Assert.Contains(EntitlementCatalog.MaxProjects, enforced);
        Assert.Contains(EntitlementCatalog.MaxSeats, enforced);
        Assert.Contains(EntitlementCatalog.MaxCommentsPerMonth, enforced);
        Assert.Contains(EntitlementCatalog.ExtensionEnabled, enforced);
        Assert.Contains(EntitlementCatalog.MaxExtensionSites, enforced);
        Assert.Contains(EntitlementCatalog.MaxPredefinedActionsPerProject, enforced);
        Assert.Contains(EntitlementCatalog.MaxTenantWidePredefinedActions, enforced);
    }
}
