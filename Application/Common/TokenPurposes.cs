namespace Pointer.Application.Common;

/// <summary>
/// DB-11c §3.4a. Purpose constants for <see cref="Pointer.Application.Abstractions.IResetTokenService.CreateScoped"/>
/// / <see cref="Pointer.Application.Abstractions.IResetTokenService.TryValidateScoped"/> — a scoped token only
/// validates for the exact purpose it was minted for.
/// </summary>
public static class TokenPurposes
{
    /// <summary>DB-11c: erase confirmation for a passwordless (magic-link) identity.</summary>
    public const string Erase = "erase";

    /// <summary>Reserved: DB-11d change-email confirmation.</summary>
    public const string ChangeEmail = "change-email";

    /// <summary>DB-14: e-mail verification confirmation.</summary>
    public const string VerifyEmail = "verify-email";
}
