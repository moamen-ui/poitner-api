namespace Pointer.Application.DTOs.Auth;

/// <summary>Body for POST /api/auth/confirm-erase (DB-11c §3.4b) — the scoped, one-time token
/// e-mailed by POST /api/me/request-erase.</summary>
public class ConfirmEraseRequest
{
    public string Token { get; set; } = string.Empty;
}
