using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Implementation;

public class StatusCatalogService : IStatusCatalogService
{
    // THE single source of truth for comment-status presentation.
    // Rename / recolor / reorder here → every client reflects it on next load.
    private static readonly List<StatusItem> Catalog = new()
    {
        new() { Value = (int)CommentStatus.Open,         Name = "Open",         Label = "Open",      Color = "#2563eb", Order = 1 },
        new() { Value = (int)CommentStatus.ReadyToApply, Name = "ReadyToApply", Label = "Ready",     Color = "#d97706", Order = 2 },
        new() { Value = (int)CommentStatus.Applied,      Name = "Applied",      Label = "Completed", Color = "#16a34a", Order = 3 },
        new() { Value = (int)CommentStatus.Archived,     Name = "Archived",     Label = "Archived",  Color = "#6b7280", Order = 4 },
    };

    public Result<List<StatusItem>> GetAll() => Result<List<StatusItem>>.Success(Catalog);
}
