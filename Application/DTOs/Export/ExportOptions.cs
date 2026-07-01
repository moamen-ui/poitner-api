using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Export;

/// <summary>
/// Server-side export filters. <see cref="IncludePrivate"/> and <see cref="IncludeDeleted"/>
/// are clamped to <c>false</c> for non-admin callers inside the service regardless of input.
/// </summary>
public class ExportOptions
{
    public bool IncludePrivate { get; set; }

    public bool IncludeDeleted { get; set; }

    public CommentStatus? Status { get; set; }

    public EnvironmentTag? Environment { get; set; }
}
