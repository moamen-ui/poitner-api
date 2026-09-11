namespace Pointer.Application.Common;

using System.Text.RegularExpressions;

/// <summary>
/// Pure static detector that inspects comment and reply bodies for credential-like
/// or executable payload patterns.
/// Matches produce advisory flags only; matched secret text, offsets, and exception
/// details are never exposed in the output surface.
/// </summary>
public static class PayloadFlagDetector
{
    public static readonly TimeSpan MatchTimeout = TimeSpan.FromMilliseconds(100);

    public static readonly (string Name, Regex Rx)[] Patterns =
    [
        (
            "openai_key",
            new Regex(
                @"\bsk-[A-Za-z0-9_-]{20,}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                MatchTimeout
            )
        ),
        (
            "aws_access_key",
            new Regex(
                @"\bAKIA[0-9A-Z]{16}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                MatchTimeout
            )
        ),
        (
            "github_token",
            new Regex(
                @"\bgh[pousr]_[A-Za-z0-9]{36,}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                MatchTimeout
            )
        ),
        (
            "pointer_key",
            new Regex(
                @"\bptr_[A-Za-z0-9]{24,}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                MatchTimeout
            )
        ),
        (
            "jwt",
            new Regex(
                @"\beyJ[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\.[A-Za-z0-9_-]{10,}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                MatchTimeout
            )
        ),
        (
            "private_key_block",
            new Regex(
                @"-----BEGIN [A-Z ]*PRIVATE KEY-----",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                MatchTimeout
            )
        ),
        (
            "long_base64",
            new Regex(
                @"\b[A-Za-z0-9+/]{64,}={0,2}\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                MatchTimeout
            )
        ),
        (
            "script_tag",
            new Regex(
                @"<\s*script\b",
                RegexOptions.Compiled
                    | RegexOptions.CultureInvariant
                    | RegexOptions.IgnoreCase,
                MatchTimeout
            )
        ),
        (
            "pipe_to_shell",
            new Regex(
                @"\b(curl|wget)\b[^\n]{0,200}\|\s*(sh|bash|zsh)\b",
                RegexOptions.Compiled | RegexOptions.CultureInvariant,
                MatchTimeout
            )
        ),
        (
            "password_assignment",
            new Regex(
                @"\b(password|passwd|secret|token)\s*[:=]\s*\S{8,}",
                RegexOptions.Compiled
                    | RegexOptions.CultureInvariant
                    | RegexOptions.IgnoreCase,
                MatchTimeout
            )
        ),
    ];

    /// <summary>
    /// Scans <paramref name="text"/> for known payload patterns.
    /// Returns the names of matched patterns (empty if clean or if match timed out).
    /// Pure function with no logger; callers are responsible for logging warnings on timeout.
    /// </summary>
    public static IReadOnlyList<string> Detect(string? text) => Detect(text, Patterns);

    /// <summary>
    /// Scans <paramref name="text"/> using a specified set of patterns.
    /// </summary>
    public static IReadOnlyList<string> Detect(
        string? text,
        IEnumerable<(string Name, Regex Rx)> patterns
    )
    {
        if (string.IsNullOrEmpty(text) || patterns == null)
        {
            return Array.Empty<string>();
        }

        try
        {
            List<string>? matched = null;
            foreach (var (name, rx) in patterns)
            {
                if (rx.IsMatch(text))
                {
                    matched ??= new List<string>();
                    matched.Add(name);
                }
            }

            return matched ?? (IReadOnlyList<string>)Array.Empty<string>();
        }
        catch (RegexMatchTimeoutException)
        {
            return Array.Empty<string>();
        }
    }
}
