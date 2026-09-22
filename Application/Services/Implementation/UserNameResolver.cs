using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;

namespace Pointer.Application.Services.Implementation;

/// <summary>
/// One shared batched lookup for resolving a display name (and optionally an e-mail) from a
/// <c>public_id</c> that some content column (<c>author_id</c>, <c>actor_id</c>, …) still carries.
/// Falls back to <see cref="UserAlias"/> for a <c>public_id</c> that belonged to a row merged away by
/// the DB-11a same-e-mail merge, so historical attributions still resolve to the current identity's
/// name (DB-RULES R14). Precedent for a static resolver: <see cref="WorkspaceNameResolver"/>.
/// </summary>
public static class UserNameResolver
{
    /// <summary>
    /// Resolves display names for <paramref name="ids"/>. Missing ids simply have no entry (callers
    /// fall back gracefully). <paramref name="ignoreQueryFilters"/> preserves each call site's
    /// pre-existing filter behaviour (some run outside any tenant context).
    /// </summary>
    public static async Task<Dictionary<Guid, string>> ResolveAsync(
        IUnitOfWork unitOfWork,
        IEnumerable<Guid> ids,
        bool ignoreQueryFilters = false
    )
    {
        var withEmail = await ResolveWithEmailAsync(unitOfWork, ids, ignoreQueryFilters);
        return withEmail.ToDictionary(kv => kv.Key, kv => kv.Value.DisplayName);
    }

    /// <summary>Same as <see cref="ResolveAsync"/> but also returns each identity's e-mail (AiRuleService's projection).</summary>
    public static async Task<
        Dictionary<Guid, (string DisplayName, string? Email)>
    > ResolveWithEmailAsync(
        IUnitOfWork unitOfWork,
        IEnumerable<Guid> ids,
        bool ignoreQueryFilters = false
    )
    {
        var distinct = ids.Where(g => g != Guid.Empty).Distinct().ToList();
        var result = new Dictionary<Guid, (string DisplayName, string? Email)>();
        if (distinct.Count == 0)
            return result;

        var userQuery = unitOfWork.Repository<User>().Query().AsNoTracking();
        if (ignoreQueryFilters)
            userQuery = userQuery.IgnoreQueryFilters();

        var found = await userQuery
            .Where(u => distinct.Contains(u.PublicId))
            .Select(u => new
            {
                u.PublicId,
                u.DisplayName,
                u.Email,
            })
            .ToListAsync();
        foreach (var u in found)
            result[u.PublicId] = (u.DisplayName, u.Email);

        var missing = distinct.Where(id => !result.ContainsKey(id)).ToList();
        if (missing.Count == 0)
            return result;

        var aliasQuery = unitOfWork.UserAliases.AsNoTracking();
        if (ignoreQueryFilters)
            aliasQuery = aliasQuery.IgnoreQueryFilters();

        var aliased = await aliasQuery
            .Where(a => missing.Contains(a.AliasPublicId))
            // F4 (DB-11a review): UserAlias itself carries no query filter, but `a.User` still
            // expands under the (possibly non-ignored) User filter — for an alias whose canonical
            // identity the caller's tenant cannot see, EF would otherwise project a null `a.User`
            // into the non-nullable DisplayName/Email below. Drop those rather than crash.
            .Where(a => a.User != null)
            .Select(a => new
            {
                a.AliasPublicId,
                a.User.DisplayName,
                a.User.Email,
            })
            .ToListAsync();
        foreach (var a in aliased)
            result[a.AliasPublicId] = (a.DisplayName, a.Email);

        return result;
    }
}
