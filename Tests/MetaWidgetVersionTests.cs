using System.Text.Json;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Xunit;

namespace Pointer.Tests;

public class MetaWidgetVersionTests
{
    [Fact]
    public async Task MetaService_Returns_WidgetVersion_Matching_Singleton()
    {
        var config = new ConfigurationBuilder().Build();
        var brandingService = new StubBrandingService("Pointer");
        var widgetInfo = new WidgetVersionInfo("99183d8f9f37", "0.1.0", new[] { "99183d8f9f37" });

        var service = new MetaService(config, brandingService, widgetInfo);
        var meta = await service.GetAsync("https://api.example.com");

        Assert.Equal("99183d8f9f37", meta.WidgetVersion);
    }

    [Fact]
    public async Task MetaService_With_Actual_Version_File_Matches_File_Hash()
    {
        var root = RepoRoot.Find();
        var versionFilePath = Path.Combine(root, "API", "wwwroot", "pointer.version.json");
        Assert.True(File.Exists(versionFilePath), $"Expected {versionFilePath} to exist");

        var json = await File.ReadAllTextAsync(versionFilePath);
        using var doc = JsonDocument.Parse(json);
        var expectedHash = doc.RootElement.GetProperty("hash").GetString();

        var apiContentRoot = Path.Combine(root, "API");
        var widgetInfo = WidgetVersionInfo.Load(apiContentRoot, NullLogger.Instance);

        var config = new ConfigurationBuilder().Build();
        var brandingService = new StubBrandingService("Pointer");
        var service = new MetaService(config, brandingService, widgetInfo);

        var meta = await service.GetAsync("https://api.example.com");

        Assert.NotNull(meta.WidgetVersion);
        Assert.Equal(expectedHash, meta.WidgetVersion);
        Assert.Equal(expectedHash, widgetInfo.CurrentHash);
    }
}
