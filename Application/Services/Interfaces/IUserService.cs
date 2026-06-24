using Pointer.Application.DTOs.User;
using Pointer.Application.Response;
using Pointer.Domain.Enums;

namespace Pointer.Application.Services.Interfaces;

public interface IUserService
{
    Task<Result<UserResponse>> CreateAsync(CreateUserRequest request);
    Task<Result<List<UserResponse>>> ListAsync(ApprovalStatus? status = null);
    Task<Result<UserResponse>> UpdateAsync(int id, UpdateUserRequest request);
    Task<Result<UserResponse>> ApproveAsync(int id, ApproveUserRequest request);
    Task<Result<UserResponse>> RejectAsync(int id);
}
