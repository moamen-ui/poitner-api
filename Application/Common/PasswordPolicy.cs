using Pointer.Application.Resources;

namespace Pointer.Application.Common;

/// <summary>
/// DB-14 D14.4. One policy for every place a password is set. Returns null when acceptable, else the
/// <c>MessageKeys.User.*</c> message. Rules: 10–128 chars; not in the embedded top-1000 list
/// (case-insensitive exact match); not equal to the e-mail or its local part (case-insensitive).
/// Existing hashes are never re-checked (D14.5).
/// </summary>
public static class PasswordPolicy
{
    public const int MinLength = 10;
    public const int MaxLength = 128;

    private static readonly Lazy<HashSet<string>> CommonPasswords = new(LoadCommonPasswords);

    public static string? Validate(string? password, string? email)
    {
        if (string.IsNullOrEmpty(password) || password.Length < MinLength)
            return MessageKeys.User.PasswordWeak;
        if (password.Length > MaxLength)
            return MessageKeys.User.PasswordTooLong;
        if (IsCommon(password))
            return MessageKeys.User.PasswordCommon;

        var normalizedEmail = EmailNormalizer.Normalize(email);
        if (normalizedEmail != null)
        {
            var normalizedPassword = password.Trim().ToLowerInvariant();
            var localPart = normalizedEmail.Split('@')[0];
            if (normalizedPassword == normalizedEmail || normalizedPassword == localPart)
                return MessageKeys.User.PasswordIsEmail;
        }

        return null;
    }

    /// <summary>Case-insensitive exact match against the embedded top-1000 list.</summary>
    public static bool IsCommon(string password) => CommonPasswords.Value.Contains(password);

    private static HashSet<string> LoadCommonPasswords()
    {
        var assembly = typeof(PasswordPolicy).Assembly;
        var resourceName = assembly
            .GetManifestResourceNames()
            .FirstOrDefault(n => n.EndsWith("common-passwords.txt", StringComparison.Ordinal));
        if (resourceName is null)
            throw new InvalidOperationException(
                "DB-14: embedded resource common-passwords.txt not found in the Application assembly."
            );

        using var stream = assembly.GetManifestResourceStream(resourceName)!;
        using var reader = new StreamReader(stream);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string? line;
        while ((line = reader.ReadLine()) != null)
        {
            // Only the two header lines are skipped (both start with '#'). The resource holds
            // exactly 1000 distinct case-insensitive entries and no blank line (the source list's
            // one blank-line entry and its 2 case-variant duplicates of "password" were dropped;
            // Tests/PasswordPolicyTests.cs asserts the set size is exactly 1000).
            if (line.StartsWith('#') || line.Length == 0)
                continue;
            set.Add(line);
        }
        return set;
    }
}
