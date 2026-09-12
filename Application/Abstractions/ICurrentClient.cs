namespace Pointer.Application.Abstractions;

/// <summary>
/// Which kind of client made this request, as declared by the <c>X-Pointer-Client</c> header.
/// </summary>
/// <remarks>
/// This is NOT an auth boundary — the header is trivially forgeable and nothing security-critical
/// may depend on it. Its one job is to keep the advisory payload flags off the DOCUMENTED AI paths:
/// the widget and dashboard send the header, an AI tool copying a curl out of skill.md does not.
///
/// It exists because the flags cannot be removed client-side: `pointer.sh` copies already installed
/// in customer repositories call GET /api/comments/{id} and will keep doing so. The server has to
/// decide.
/// </remarks>
public interface ICurrentClient
{
    /// <summary>True for the widget and the dashboard — the surfaces a human is looking at.</summary>
    bool IsHumanSurface { get; }
}
