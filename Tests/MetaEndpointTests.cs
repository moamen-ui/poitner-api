using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.Configuration;
using Pointer.API.Controllers;
using Pointer.Application.DTOs.Branding;
using Pointer.Application.DTOs.Meta;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;

namespace Pointer.Tests;

public class MetaEndpointTests
{
    [Fact]
    public void Controller_IsAnonymous_And_HasMetaRateLimit()
    {
        var type = typeof(MetaController);

        var allowAnonymous = type.GetCustomAttribute<AllowAnonymousAttribute>();
        Assert.NotNull(allowAnonymous);

        var rateLimit = type.GetCustomAttribute<EnableRateLimitingAttribute>();
        Assert.NotNull(rateLimit);
        Assert.Equal("meta", rateLimit.PolicyName);
    }

    [Fact]
    public async Task Service_ReturnsCorrectFields()
    {
        var config = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?> { { "Cli:MinVersion", "1.2.3" } })
            .Build();

        var brandingService = new StubBrandingService("Test Product");
        var service = new MetaService(config, brandingService);

        var result = await service.GetAsync("https://api.example.com");

        Assert.Equal(1, result.ApiVersion);
        Assert.Equal("1.2.3", result.MinCliVersion);
        Assert.Equal("Test Product", result.ProductName);
        // R2-03 fills this in. It is what `doctor` compares an installed skill copy against, so a
        // null here would make every install look up to date regardless of how old it is.
        Assert.False(string.IsNullOrWhiteSpace(result.SkillVersion));
        // Version string depends on assembly info, just check it's not null.
        Assert.NotNull(result.Version);
        // ServerTime should be roughly now UTC.
        Assert.True(Math.Abs((DateTime.UtcNow - result.ServerTime).TotalMinutes) < 1);
    }

    [Fact]
    public async Task Controller_ReturnsOkResult()
    {
        var config = new ConfigurationBuilder().Build();
        var metaService = new StubMetaService();
        var controller = new MetaController(metaService, config)
        {
            ControllerContext = new ControllerContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };

        var result = await controller.Get() as OkObjectResult;
        Assert.NotNull(result);

        var okResult = result.Value as Result<MetaResponse>;
        Assert.NotNull(okResult);
        Assert.True(okResult.IsSuccess);
        Assert.Equal("1.0.0", okResult.Data!.MinCliVersion);
    }

    private class StubBrandingService(string productName) : IBrandingService
    {
        public Task<int> BumpVersionAsync() => Task.FromResult(1);

        public Task<BrandingResponse> BuildResponseAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(new BrandingResponse { ProductName = productName });

        public Task<Result<BrandingResponse>> GetAsync(string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Result<BrandingResponse>.Success(new BrandingResponse { ProductName = productName }));

        public Task<Result<BrandingResponse>> UpdateAsync(BrandingWriteDto dto, string publicBase, IReadOnlySet<string> existingKinds) =>
            Task.FromResult(Result<BrandingResponse>.Success(new BrandingResponse { ProductName = productName }));
    }

    private class StubMetaService : IMetaService
    {
        public Task<MetaResponse> GetAsync(string publicBase) =>
            Task.FromResult(new MetaResponse { MinCliVersion = "1.0.0", ProductName = "P" });
    }
}
