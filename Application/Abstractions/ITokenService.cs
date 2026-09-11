using Pointer.Domain.Entity;

namespace Pointer.Application.Abstractions;

public interface ITokenService
{
    /// <param name="keyScopes">
    /// When the session was opened with an API key, that key's <c>ApiKeyScopes</c> value, emitted as a
    /// <c>key_scopes</c> claim so §25 can enforce scopes without re-reading the key. Null for password
    /// logins, and then the claim is omitted entirely.
    /// </param>
    string Issue(User user, int? keyScopes = null);
}
