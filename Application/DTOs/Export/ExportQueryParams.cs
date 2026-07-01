using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Export;

/// <summary>Query-string binding for the export endpoints; maps to <see cref="ExportOptions"/>.</summary>
public class ExportQueryParams
{
    public bool? IncludePrivate { get; set; }

    public bool? IncludeDeleted { get; set; }

    public CommentStatus? Status { get; set; }

    public EnvironmentTag? Environment { get; set; }

    public ExportOptions ToOptions() =>
        new()
        {
            IncludePrivate = IncludePrivate ?? false,
            IncludeDeleted = IncludeDeleted ?? false,
            Status = Status,
            Environment = Environment
        };
}
