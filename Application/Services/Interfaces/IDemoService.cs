using Pointer.Application.DTOs.Demo;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IDemoService
{
    Task<Result<DemoSessionResponse>> ProvisionAsync(string serverUrl);
}
