using Pointer.Application.DTOs.Meta;

namespace Pointer.Application.Services.Interfaces;

public interface IMetaService
{
    Task<MetaResponse> GetAsync(string publicBase);
}
