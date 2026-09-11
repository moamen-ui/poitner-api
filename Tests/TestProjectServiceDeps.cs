using Microsoft.Extensions.Configuration;
using Pointer.Application.Services.Interfaces;

namespace Pointer.Tests;

/// <summary>
/// Defaults for the two dependencies ProjectService gained with origin enforcement (R1-05). Both
/// return "nothing configured", which is the shape every pre-existing test assumes: no branding app
/// URL and no trusted-origin list, so enforcement decisions fall through to the project's own rows.
/// </summary>
public static class TestProjectServiceDeps
{
    public sealed class EmptySettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback = "") => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    public static ISettingsService Settings() => new EmptySettings();

    public static IConfiguration Configuration() => new ConfigurationBuilder().Build();
}
