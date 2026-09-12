namespace Pointer.Application.Common;

using System.Diagnostics;
using System.Text.RegularExpressions;

/// <summary>
/// Pure static defense-in-depth sanitizer for DOM snapshots submitted in comment creation.
/// Enforces that form values and sensitive attributes are stripped, and that inner text is
/// replaced with ••• when a project has CaptureTextContent set to false.
/// Never throws; malformed inputs pass through unchanged.
/// </summary>
public static class SnapshotSanitizer
{
    private static readonly Regex TagRegex = new(@"^<([a-zA-Z0-9_-]+)", RegexOptions.Compiled);
    private static readonly Regex FormTagRegex = new(@"^(input|textarea|select|option)$", RegexOptions.Compiled | RegexOptions.IgnoreCase);
    private static readonly Regex AttrRegex = new(
        @"(?<name>[a-zA-Z0-9_-]+)(?:\s*=\s*(?:""(?<val>[^""]*)""|'(?<val>[^']*)'|(?<val>[^\s/>]+)))?",
        RegexOptions.Compiled
    );
    private static readonly Regex CloseTagRegex = new(@"^(.*?)(</[a-zA-Z0-9_-]+>)$", RegexOptions.Compiled | RegexOptions.Singleline);

    public static string Sanitize(string? snapshot, bool captureTextContent)
    {
        if (string.IsNullOrEmpty(snapshot))
        {
            return snapshot ?? string.Empty;
        }

        // Malformed input check: length > 4000
        if (snapshot.Length > 4000)
        {
            Debug.WriteLine($"[SnapshotSanitizer] Snapshot length {snapshot.Length} exceeds 4000; returning unchanged.");
            return snapshot;
        }

        // Malformed input check: no closing '>'
        int openTagEnd = snapshot.IndexOf('>');
        if (openTagEnd < 0)
        {
            Debug.WriteLine("[SnapshotSanitizer] Snapshot has no closing '>'; returning unchanged.");
            return snapshot;
        }

        // Malformed input check: odd number of '"' in the opening tag
        string openingTag = snapshot[..(openTagEnd + 1)];
        int quoteCount = openingTag.Count(c => c == '"');
        if (quoteCount % 2 != 0)
        {
            Debug.WriteLine("[SnapshotSanitizer] Odd number of quotes in opening tag; returning unchanged.");
            return snapshot;
        }

        var tagMatch = TagRegex.Match(openingTag);
        if (!tagMatch.Success)
        {
            return snapshot;
        }

        string tagName = tagMatch.Groups[1].Value;
        bool isFormTag = FormTagRegex.IsMatch(tagName);

        bool selfClosing = openTagEnd > 0 && snapshot[openTagEnd - 1] == '/';
        int attrEnd = selfClosing ? openTagEnd - 1 : openTagEnd;
        string attrPart = snapshot.Substring(tagMatch.Length, attrEnd - tagMatch.Length);

        var matches = AttrRegex.Matches(attrPart);
        var keptAttrs = new List<string>();

        foreach (Match m in matches)
        {
            string name = m.Groups["name"].Value;
            string lower = name.ToLowerInvariant();
            bool hasVal = m.Groups["val"].Success;
            string val = hasVal ? m.Groups["val"].Value : string.Empty;

            // Strip attributes in the sensitive list (A.3)
            if (IsSensitiveAttribute(lower))
            {
                continue;
            }

            if (lower == "value")
            {
                if (isFormTag)
                {
                    // Strip value="..." on input|textarea|select|option tags, replace with value="•••" when non-empty
                    if (!string.IsNullOrEmpty(val))
                    {
                        keptAttrs.Add("value=\"•••\"");
                    }
                    // When empty, stripped
                    continue;
                }
                else
                {
                    // Non-form tags: exact "value" is in the sensitive list -> strip
                    continue;
                }
            }

            if (hasVal)
            {
                keptAttrs.Add($"{name}=\"{val}\"");
            }
            else
            {
                keptAttrs.Add(name);
            }
        }

        string newOpeningTag = "<" + tagName;
        if (keptAttrs.Count > 0)
        {
            newOpeningTag += " " + string.Join(" ", keptAttrs);
        }
        newOpeningTag += selfClosing ? "/>" : ">";

        string rest = snapshot[(openTagEnd + 1)..];
        if (!captureTextContent)
        {
            // If project has CaptureTextContent == false, remove inner text (>...< between open and close tag) -> >•••<
            var closeMatch = CloseTagRegex.Match(rest);
            if (closeMatch.Success)
            {
                rest = "•••" + closeMatch.Groups[2].Value;
            }
        }

        return newOpeningTag + rest;
    }

    private static bool IsSensitiveAttribute(string lower)
    {
        if (lower == "data-value" ||
            lower == "data-email" ||
            lower == "data-token" ||
            lower == "data-secret" ||
            lower == "authorization" ||
            lower == "srcdoc")
        {
            return true;
        }

        if (lower.StartsWith("data-user", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }
}
