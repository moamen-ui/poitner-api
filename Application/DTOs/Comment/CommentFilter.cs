using Pointer.Domain.Enums;

namespace Pointer.Application.DTOs.Comment;

public class CommentFilter
{
    public CommentStatus? Status { get; set; }
    public EnvironmentTag? Environment { get; set; }
    public int PageNumber { get; set; } = 1;
    public int PageSize { get; set; } = 50;
}
