using System.Reflection;
using Microsoft.Extensions.Configuration;
using Pointer.Application.DTOs.Meta;
using Pointer.Application.Services.Interfaces;

namespace Pointer.Application.Services.Implementation;

public sealed class MetaService(IConfiguration configuration, IBrandingService brandingService) : IMetaService
{
    public async Task<MetaResponse> GetAsync(string publicBase)
    {
        var minCliVersion = configuration["Cli:MinVersion"] ?? "0.0.0";
        var branding = await brandingService.GetAsync(publicBase, Array.Empty<string>().ToHashSet());
        var productName = branding.IsSuccess && branding.Data != null ? branding.Data.ProductName : "Pointer";

        var assembly = Assembly.GetEntryAssembly();
        var informationalVersion = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";

        return new MetaResponse
        {
            Version = informationalVersion,
            ApiVersion = 1,
            MinCliVersion = minCliVersion,
            SkillVersion = null,
            ProductName = productName,
            ServerTime = DateTime.UtcNow
        };
    }
}
