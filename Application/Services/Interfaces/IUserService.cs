using Pointer.Application.DTOs.User;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IUserService
{
    Task<Result<UserResponse>> CreateAsync(CreateUserRequest request);
    Task<Result<List<UserResponse>>> ListAsync();
    Task<Result<UserResponse>> UpdateAsync(int id, UpdateUserRequest request);
}
