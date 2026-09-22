namespace Pointer.Infrastructure.Email;

/// <summary>
/// Turns a recipient e-mail into a pseudonym safe to write to logs/Sentry (GLM review F7 —
/// <c>{To}</c> was previously logged verbatim by <see cref="BrevoEmailSender"/> and
/// <see cref="SmtpEmailSender"/>, which meant every recipient's real address ended up in
/// structured stdout logs and, once a Sentry DSN is set, in breadcrumbs). Keeps enough of the
/// address to be useful for support/debugging (first two chars of the local part, plus the
/// domain) without logging the full address.
/// </summary>
internal static class EmailLogRedaction
{
    public static string Pseudonymize(string? email)
    {
        if (string.IsNullOrWhiteSpace(email))
            return "(none)";

        var at = email.IndexOf('@');
        if (at <= 0 || at == email.Length - 1)
            return "***";

        var local = email[..at];
        var domain = email[(at + 1)..];
        var visible = local.Length <= 2 ? local : local[..2];

        return $"{visible}…@{domain}";
    }
}
