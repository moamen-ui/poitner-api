namespace Pointer.Application.Common;

/// <summary>
/// The ONLY e-mail normalisation in the codebase (DB-11a, GLM A1). The database enforces one live identity per
/// <c>lower(email)</c> (<c>ux_users_email_live</c>, expression index); this routine exists so every equality
/// lookup finds that row and every write stores the same shape. Trim + lower-invariant. Returns null for
/// null/whitespace so optional fields (invite e-mail lock) stay null. Never add a second normaliser —
/// the DB-11a acceptance criteria grep for stray <c>.Trim().ToLower()</c> on e-mails.
/// </summary>
public static class EmailNormalizer
{
    public static string? Normalize(string? email) =>
        string.IsNullOrWhiteSpace(email) ? null : email.Trim().ToLowerInvariant();

    /// <summary>For required fields: normalises or returns "" (so validators, not this class, decide emptiness).</summary>
    public static string NormalizeRequired(string? email) => Normalize(email) ?? string.Empty;
}
