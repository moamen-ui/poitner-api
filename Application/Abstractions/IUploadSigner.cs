namespace Pointer.Application.Abstractions;

/// <summary>
/// Produces and validates short-lived HMAC-signed URLs for uploaded files so that screenshots
/// are not accessible via a permanent unauthenticated static path.
/// </summary>
public interface IUploadSigner
{
    /// <summary>
    /// Returns a signed URL of the form:
    ///   /api/uploads/file?p={urlEncoded(relPath)}&amp;exp={unixSecondsNow+3600}&amp;sig={base64url(HMACSHA256)}
    /// relPath should begin with "uploads/".
    /// </summary>
    string SignedUrl(string relPath);

    /// <summary>
    /// DB-13 review fix #2: same as <see cref="SignedUrl(string)"/> but the URL expires at whichever
    /// is sooner — the normal TTL, or <paramref name="notAfter"/> (the impersonating operator's
    /// session <c>ExpiresAt</c>, from <c>ICurrentUser.ImpersonationExpiresAt</c>) — so a screenshot
    /// URL handed out mid-session cannot outlive it. Pass null (or omit) for the ordinary,
    /// non-impersonating case. Default interface implementation ignores <paramref name="notAfter"/>
    /// and falls back to the plain TTL, so every hand-written test double implementing only the
    /// single-arg overload keeps compiling unchanged; the concrete UploadSigner (Infrastructure)
    /// is the only real override.
    /// </summary>
    string SignedUrl(string relPath, DateTime? notAfter) => SignedUrl(relPath);

    /// <summary>
    /// Returns true when the signature is valid AND the expiry is still in the future.
    /// Constant-time comparison is used to prevent timing attacks.
    /// </summary>
    bool Validate(string relPath, long exp, string sig);

    /// <summary>
    /// Robustly extracts the "uploads/..." relative path from any of:
    ///   (a) an already-signed URL  (/api/uploads/file?p=uploads%2F...)
    ///   (b) an absolute or relative public URL containing "uploads/"
    ///   (c) a raw relative path starting with "uploads/"
    /// Returns the relPath as-is if none of the above patterns match.
    /// </summary>
    string ExtractRelPath(string stored);
}
