using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IStatusAdminService
{
    Task<Result<List<StatusAdminItem>>> ListAsync();
    Task<Result<StatusAdminItem>> UpsertAsync(int value, UpdateStatusPresentationRequest request);
    Task<Result> ResetAsync(int value);
}
