using Pointer.Application.DTOs.Status;
using Pointer.Application.Response;

namespace Pointer.Application.Services.Interfaces;

public interface IStatusCatalogService
{
    Result<List<StatusItem>> GetAll();
}
