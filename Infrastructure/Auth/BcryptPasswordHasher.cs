using Pointer.Application.Abstractions;

namespace Pointer.Infrastructure.Auth;

public class BcryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);

    // DB-18 code review (Opus LOW): an empty/non-bcrypt hash reaching Verify (e.g. a defensive/
    // future caller checking a passwordless-only identity's placeholder hash) must fail the check,
    // not throw BCrypt.Net's SaltParseException/FormatException out as an unhandled 500.
    public bool Verify(string password, string hash)
    {
        if (string.IsNullOrEmpty(hash))
            return false;
        try
        {
            return BCrypt.Net.BCrypt.Verify(password, hash);
        }
        catch (Exception)
        {
            return false;
        }
    }
}
