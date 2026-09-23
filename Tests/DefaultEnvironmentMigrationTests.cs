using Microsoft.EntityFrameworkCore;
using Pointer.API.Seed;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Xunit;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Pointer.Application.Services.Interfaces;
using Pointer.Application.Abstractions;

namespace Pointer.Tests;

public class DefaultEnvironmentMigrationTests
{
    private class DummyPasswordHasher : IPasswordHasher
    {
        public string Hash(string password) => password;
        public bool Verify(string password, string hash) => password == hash;
    }
    
    private class DummySettingsService : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback) => Task.FromResult(fallback);
        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;
        public Task<int> GetIntAsync(string key, int fallback) => Task.FromResult(fallback);
        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
        public Task<string> GetStringAsync(string key, string fallback) => Task.FromResult(fallback);
        public Task SetStringAsync(string key, string value) => Task.CompletedTask;
    }
    
    private sealed class FakeCurrentUser : ICurrentUser
    {
        public Guid? Id { get; set; }
        public bool IsAdmin { get; set; }
        public bool IsSuperAdmin { get; set; }
        public bool IsQuickAccess { get; set; }
        public Guid? TenantId { get; set; }
        public int? RoleId { get; set; }
        public string? KeyScopes { get; set; }
        public string? Scope { get; set; }
    }

    private static IServiceProvider BuildServices(string dbName)
    {
        var services = new ServiceCollection();
        services.AddDbContext<AppDbContext>(options => options.UseInMemoryDatabase(dbName));
        services.AddScoped<IPasswordHasher, DummyPasswordHasher>();
        services.AddScoped<ISettingsService, DummySettingsService>();
        services.AddScoped<ICurrentUser>(sp => new FakeCurrentUser { IsSuperAdmin = true });
        
        var config = new ConfigurationBuilder().Build();
        services.AddSingleton<IConfiguration>(config);
        
        return services.BuildServiceProvider();
    }

    [Fact]
    public async Task SeedAsync_RetiresGlobalDefault_AndCreatesLocal()
    {
        var dbName = Guid.NewGuid().ToString();
        
        using (var scope = BuildServices(dbName).CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.AppEnvironments.Add(new AppEnvironment { Name = "default", OwnerId = null, IsEnabled = true });
            db.SaveChanges();
        }

        await AdminSeeder.SeedAsync(BuildServices(dbName));

        using (var scope = BuildServices(dbName).CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            var defaultEnv = db.AppEnvironments.IgnoreQueryFilters().Single(e => e.Name == "default" && e.OwnerId == null);
            Assert.False(defaultEnv.IsEnabled);
            Assert.True(defaultEnv.IsRetired);

            var localEnv = db.AppEnvironments.IgnoreQueryFilters().Single(e => e.Name == "local" && e.OwnerId == null);
            Assert.NotNull(localEnv);
        }
    }

    [Fact]
    public async Task SeedAsync_RepointsExistingProjectAppUrlToLocal()
    {
        var dbName = Guid.NewGuid().ToString();
        var tenant = Guid.NewGuid();
        
        using (var scope = BuildServices(dbName).CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            var p = new Project { Key = "p", Name = "P", OwnerId = tenant, AppUrl = "https://test" };
            db.Projects.Add(p);
            db.SaveChanges();
        }

        await AdminSeeder.SeedAsync(BuildServices(dbName));

        using (var scope = BuildServices(dbName).CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            
            var localEnv = db.AppEnvironments.IgnoreQueryFilters().Single(e => e.Name == "local" && e.OwnerId == null);
            var url = db.ProjectAppUrls.IgnoreQueryFilters().Single();
            
            Assert.Equal(localEnv.Id, url.AppEnvironmentId);
            Assert.Equal("https://test", url.Url);
        }
    }
}
