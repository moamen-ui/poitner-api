namespace Pointer.Application.Abstractions;

public interface ICurrentUser
{
    Guid? Id { get; }
    bool IsAdmin { get; }
}
