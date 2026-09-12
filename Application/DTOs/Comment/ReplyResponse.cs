namespace Pointer.Application.DTOs.Comment;

public class ReplyResponse
{
    public int Id { get; set; }
    public Guid AuthorId { get; set; }
    public string? AuthorName { get; set; }
    public string Body { get; set; } = string.Empty;
    public DateTime CreatedAt { get; set; }

    /// <summary>
    /// Advisory: this text looked like it contained a credential or executable payload.
    /// </summary>
    /// <remarks>
    /// Populated ONLY for the widget and dashboard (the X-Pointer-Client header). Null — and so
    /// absent from the JSON — for every other caller, which is how the documented AI paths never
    /// see it. See R2-06 exposure rules.
    /// </remarks>
    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public bool? HasPayloadFlag { get; set; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public List<string>? PayloadFlags { get; set; }

}
