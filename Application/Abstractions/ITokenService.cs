using Pointer.Domain.Entity;

namespace Pointer.Application.Abstractions;

public interface ITokenService
{
    string Issue(User user);
}
