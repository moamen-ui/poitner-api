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
    public void NineEnforcedLevers_AreFlagged()
    {
        var enforced = EntitlementCatalog.Enforced.Select(s => s.Key).ToHashSet();
        Assert.Equal(9, enforced.Count);
        Assert.Contains(EntitlementCatalog.MaxProjects, enforced);
        Assert.Contains(EntitlementCatalog.MaxSeats, enforced);
        Assert.Contains(EntitlementCatalog.MaxCommentsPerMonth, enforced);
        Assert.Contains(EntitlementCatalog.ExtensionEnabled, enforced);
        Assert.Contains(EntitlementCatalog.MaxExtensionSites, enforced);
        Assert.Contains(EntitlementCatalog.MaxPredefinedActionsPerProject, enforced);
        Assert.Contains(EntitlementCatalog.MaxTenantWidePredefinedActions, enforced);
        // DB-19 §3.1 — the two new-workspace levers are enforced.
        Assert.Contains(EntitlementCatalog.MaxOwnedWorkspaces, enforced);
        Assert.Contains(EntitlementCatalog.NewWorkspaceRequiresApproval, enforced);
    }

    /// <summary>DB-19 D19.1: catalog defaults for the two new-workspace levers.</summary>
    [Fact]
    public void NewLevers_Defaults()
    {
        var empty = new PlanEntitlements(); // keys absent — every existing plans row
        Assert.Equal(
            1,
            EntitlementCatalog.ResolveInt(empty, EntitlementCatalog.MaxOwnedWorkspaces)
        );
        Assert.True(
            EntitlementCatalog.ResolveBool(empty, EntitlementCatalog.NewWorkspaceRequiresApproval)
        );
    }
}
