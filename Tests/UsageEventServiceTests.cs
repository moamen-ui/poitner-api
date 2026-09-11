using Pointer.Application.Abstractions;
using System;
using System.Linq;
using System.Threading.Tasks;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using Pointer.Application.DTOs.Event;
using Pointer.Application.Response;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

public class UsageEventServiceTests : IDisposable
{
    private readonly AppDbContext _db;
    private readonly UnitOfWork _uow;
    private readonly ICurrentUser _currentUser;
    private readonly IProjectService _projectService;
    private readonly UsageEventService _service;

    public UsageEventServiceTests()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
            
        _currentUser = Substitute.For<ICurrentUser>();
        _currentUser.TenantId.Returns(Guid.NewGuid());
        _currentUser.Id.Returns(Guid.NewGuid());

        _db = new AppDbContext(options, _currentUser, new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build());
        _uow = new UnitOfWork(_db);
        
        _projectService = Substitute.For<IProjectService>();
        
        _service = new UsageEventService(_uow, _currentUser, _projectService);
    }

    [Fact]
    public async Task RecordEventAsync_RecordsEventSuccessfully()
    {
        var result = await _service.RecordEventAsync("installed", "cli", null, new { foo = "bar" });
        Assert.True(result.IsSuccess);

        var ev = await _db.UsageEvents.FirstOrDefaultAsync();
        Assert.NotNull(ev);
        Assert.Equal("installed", ev.Type);
        Assert.Contains("foo", ev.Meta);
    }

    [Fact]
    public async Task RecordEventAsync_WithProjectKey_ResolvesProjectIdAndOwnerId()
    {
        var user = new User { PublicId = _currentUser.TenantId.Value, Email = "test@example.com" };
        var project = new Project { Key = "my-project", Name = "My Project", OwnerId = user.PublicId, CreatedBy = user.PublicId };
        _db.Users.Add(user);
        _db.Projects.Add(project);
        await _db.SaveChangesAsync();

        _projectService.EnsureAsync("my-project").Returns(Task.FromResult(Result<int>.Success(project.Id)));

        var result = await _service.RecordEventAsync("installed", "cli", "my-project");
        Assert.True(result.IsSuccess);

        var ev = await _db.UsageEvents.FirstOrDefaultAsync();
        Assert.NotNull(ev);
        Assert.Equal(project.Id, ev.ProjectId);
        Assert.Equal(project.OwnerId, ev.OwnerId);
    }

    [Fact]
    public async Task RecordEventAsync_UnknownProjectKey_ReturnsNotFound()
    {
        _projectService.EnsureAsync("unknown-project").Returns(Task.FromResult(Result<int>.NotFound("Not Found")));

        var result = await _service.RecordEventAsync("installed", "cli", "unknown-project");
        Assert.False(result.IsSuccess);
        Assert.Equal("Project not found", result.Message);
    }

    public void Dispose()
    {
        _db.Dispose();
    }
}
