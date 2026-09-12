using System.Reflection;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Meta;
using Pointer.Application.Services.Interfaces;

namespace Pointer.Application.Services.Implementation;

public sealed class MetaService(IConfiguration configuration, IBrandingService brandingService) : IMetaService
{
    public async Task<MetaResponse> GetAsync(string publicBase)
    {
        var minCliVersion = configuration["Cli:MinVersion"] ?? "0.0.0";
        var branding = await brandingService.GetAsync(publicBase, Array.Empty<string>().ToHashSet());
        // BrandingDefaults, not a literal: a hardcoded "Pointer" here is the same white-label leak
        // the CLI had, and /api/meta is what the CLI reads the product name from.
        var productName = branding.IsSuccess && branding.Data != null ? branding.Data.ProductName : BrandingDefaults.ProductName;

        var assembly = Assembly.GetEntryAssembly();
        var informationalVersion = assembly?.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion ?? "0.0.0-dev";

        return new MetaResponse
        {
            Version = informationalVersion,
            ApiVersion = 1,
            MinCliVersion = minCliVersion,
            // Resolved through the SAME helper the served-file middleware stamps with — if these
            // diverged, doctor would compare an installed stamp against a different value.
            SkillVersion = SkillVersionResolver.Resolve(configuration),
            ProductName = productName,
            ServerTime = DateTime.UtcNow
        };
    }
}
