namespace Pointer.Application.DTOs.Export;

/// <summary>
/// Summary returned after an import. Follows the regular API response convention
/// (camelCase, wrapped in <c>Result&lt;T&gt;</c> by the controller).
/// </summary>
public class ImportResultDto
{
    public int ImportedComments { get; set; }

    public int ImportedReplies { get; set; }

    public int SkippedDuplicates { get; set; }

    public List<string> Warnings { get; set; } = new();
}
