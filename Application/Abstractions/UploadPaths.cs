using System.Text.RegularExpressions;

namespace Pointer.Application.Abstractions;

/// <summary>
/// DB-16 review fix #1 (BLOCKER). A crafted screenshot path (dot-dot, percent-encoded dot-dot,
/// backslash separators, a nested/double-encoded signed URL, "../branding/...") could make a
/// simple <c>StartsWith("uploads/{ownerId:N}/")</c> ownership check pass while still resolving
/// (via <c>Path.GetFullPath</c>) to a file outside that owner's folder — including another
/// workspace's screenshot or the operator's branding assets. This is the single canonical-shape
/// gate applied BEFORE any ownership/ownership-prefix check at every delete site (purge, comment
/// edit) and again — redundantly, on purpose — inside <c>LocalFileStorage.TryResolve</c>.
///
/// Mirrors the REAL shape written by <c>LocalFileStorage.SaveAsync</c> and validated by
/// <c>UploadsController.Upload</c>:
///   uploads/{ownerSegment}/{project}/{fileName}
/// where:
///   - ownerSegment is a lower-case 32-hex GUID ("N" format, <see cref="Guid.ToString(string?)"/>
///     with "N") or the literal "global" (super-admin-owned projects, pre-20260827 only).
///   - project is whatever <c>UploadsController.ProjectPattern</c> already restricts a project key
///     to: <c>^[A-Za-z0-9._-]+$</c> (stored lower-cased).
///   - fileName is exactly <c>{Guid.NewGuid():N}{extension}</c> — a lower-case 32-hex GUID followed
///     by one of the allowed image extensions.
/// </summary>
public static class UploadPaths
{
    // DB-16 re-review (LOW): '\n'/'\r' added — without them, a segment ending in a trailing
    // newline (e.g. "<hex32>.png\n") is not caught by IndexOfAny alone before the regex stage; kept
    // here too as a fast, allocation-free first guard, redundant with the \z fix below on purpose.
    private static readonly char[] DisallowedChars = ['\\', '%', '?', '#', ':', '\0', '\n', '\r'];

    // DB-16 re-review (LOW): every pattern anchors its end with \z, not $. In .NET, `$` (without
    // RegexOptions.Multiline) matches BOTH the true end of the string AND the position immediately
    // before a single trailing '\n' — so "<hex32>\n" would satisfy `^[0-9a-f]{32}$`. `\z` matches
    // only the absolute end of the input, with no such exception.
    private static readonly Regex OwnerGuidSegment = new(@"^[0-9a-f]{32}\z", RegexOptions.Compiled);

    private static readonly Regex ProjectSegment = new(@"^[A-Za-z0-9._-]+\z", RegexOptions.Compiled);

    private static readonly Regex FileSegment = new(
        @"^[0-9a-f]{32}\.(png|jpe?g|webp|gif)\z",
        RegexOptions.Compiled
    );

    /// <summary>
    /// True only when <paramref name="rel"/> is EXACTLY the canonical
    /// <c>uploads/&lt;owner&gt;/&lt;project&gt;/&lt;file&gt;</c> shape — four non-empty segments,
    /// no <c>.</c> or <c>..</c> segment anywhere, none of <c>\ % ? # : \0 \n \r</c> present at all (so
    /// no unresolved percent-encoding, no backslash separators, no query string riding along, and no
    /// trailing-newline regex-anchor bypass — DB-16 re-review LOW), the
    /// owner segment a lower-case 32-hex GUID or "global", and the file segment a lower-case
    /// 32-hex GUID with an allowed image extension.
    /// </summary>
    public static bool IsCanonical(string? rel)
    {
        if (string.IsNullOrEmpty(rel))
            return false;

        if (rel.IndexOfAny(DisallowedChars) >= 0)
            return false;

        if (!rel.StartsWith("uploads/", StringComparison.Ordinal))
            return false;

        var segments = rel.Split('/');
        if (segments.Length != 4)
            return false;

        foreach (var seg in segments)
        {
            if (seg.Length == 0 || seg == "." || seg == "..")
                return false;
        }

        var owner = segments[1];
        if (owner != "global" && !OwnerGuidSegment.IsMatch(owner))
            return false;

        if (!ProjectSegment.IsMatch(segments[2]))
            return false;

        if (!FileSegment.IsMatch(segments[3]))
            return false;

        return true;
    }
}
