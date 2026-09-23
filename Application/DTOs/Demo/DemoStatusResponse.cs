namespace Pointer.Application.DTOs.Demo;

/// <summary>Response of POST /api/demo/extend (DB-17 §3.6).</summary>
public class DemoStatusResponse
{
    public DateTime ExpiresAt { get; set; }
    public DateTime? ExtendedAt { get; set; }
    public bool CanExtend { get; set; }
}
