using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Pointer.Application.Abstractions;
using Pointer.Application.Services.Interfaces;

namespace Pointer.Application.Services.Implementation;

/// <inheritdoc cref="IWorkspaceStateService"/>
public class WorkspaceStateService : IWorkspaceStateService
{
    private static readonly TimeSpan CacheTtl = TimeSpan.FromSeconds(30);

    private readonly IUnitOfWork _unitOfWork;
    private readonly IMemoryCache _cache;

    public WorkspaceStateService(IUnitOfWork unitOfWork, IMemoryCache? cache = null)
    {
        _unitOfWork = unitOfWork;
        _cache = cache ?? new MemoryCache(new MemoryCacheOptions());
    }

    private static string CacheKey(Guid workspaceId) => $"wsfrozen:{workspaceId:N}";

    public Task<WorkspaceFreeze> GetAsync(Guid workspaceId) =>
        _cache.GetOrCreateAsync(
            CacheKey(workspaceId),
            async entry =>
            {
                entry.AbsoluteExpirationRelativeToNow = CacheTtl;

                // Gemini LOW #2: explicit Id + DeletedAt predicate — a missing or soft-deleted row is
                // never frozen (IgnoreQueryFilters because this runs from anonymous/no-tenant paths too).
                var row = await _unitOfWork
                    .Workspaces.IgnoreQueryFilters()
                    .AsNoTracking()
                    .Where(w => w.Id == workspaceId && w.DeletedAt == null)
                    .Select(w => new
                    {
                        w.PausedAt,
                        w.PausedByOperator,
                        w.DeletionScheduledFor,
                    })
                    .FirstOrDefaultAsync();

                if (row is null)
                    return new WorkspaceFreeze(false, false, false, null);

                var isPaused = row.PausedAt != null;
                var isFrozen = isPaused || row.DeletionScheduledFor != null;
                return new WorkspaceFreeze(
                    isFrozen,
                    isPaused,
                    row.PausedByOperator,
                    row.DeletionScheduledFor
                );
            }
        )!;

    public void Invalidate(Guid workspaceId) => _cache.Remove(CacheKey(workspaceId));
}
