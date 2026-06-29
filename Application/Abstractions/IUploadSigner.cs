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
