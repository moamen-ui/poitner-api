using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using Pointer.API.Controllers;
using Pointer.Application.Abstractions;
using Pointer.Application.Common;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Resources;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// Review finding #1: MeController.CreateWorkspace/GetWorkspaceAllowance (DB-19 §3.5) exercised at
/// the controller layer — status code and <c>Result</c> envelope mapping for success, forbidden and
/// failure — mirroring <c>AuthController_LockedResult_Returns429WithRetryAfterHeader</c>
/// (LoginAttemptLimiterTests.cs). <see cref="IWorkspaceCreationService"/> is the only stubbed
/// dependency; every other MeController constructor argument is an unused NSubstitute fake.
/// </summary>
public class MeControllerWorkspaceTests
{
    private static MeController BuildController(IWorkspaceCreationService workspaceCreation) =>
        new(
            Substitute.For<IPreferencesService>(),
            Substitute.For<IProfileService>(),
            Substitute.For<IAuthService>(),
            Substitute.For<INotificationService>(),
            Substitute.For<IUserService>(),
            Substitute.For<IIdentityEraseService>(),
            Substitute.For<IEmailVerificationService>(),
            workspaceCreation,
            Substitute.For<ICurrentUser>()
        )
        {
            ControllerContext = new ControllerContext { HttpContext = new DefaultHttpContext() },
        };

    [Fact]
    public async Task CreateWorkspace_Success_Returns200WithEnvelope()
    {
        var response = new CreateWorkspaceResponse
        {
            WorkspaceId = Guid.NewGuid(),
            Name = "Acme Client",
            Status = "active",
        };
        var stub = Substitute.For<IWorkspaceCreationService>();
        stub.CreateForCurrentIdentityAsync(Arg.Any<CreateWorkspaceRequest>())
            .Returns(Task.FromResult(Result<CreateWorkspaceResponse>.Success(response)));

        var actionResult = await BuildController(stub)
            .CreateWorkspace(new CreateWorkspaceRequest { Name = "Acme Client" });

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var envelope = Assert.IsType<Result<CreateWorkspaceResponse>>(ok.Value);
        Assert.True(envelope.IsSuccess);
        Assert.Equal("Acme Client", envelope.Data!.Name);
        Assert.Equal("active", envelope.Data.Status);
    }

    [Fact]
    public async Task CreateWorkspace_Forbidden_Returns403WithEnvelope()
    {
        var stub = Substitute.For<IWorkspaceCreationService>();
        stub.CreateForCurrentIdentityAsync(Arg.Any<CreateWorkspaceRequest>())
            .Returns(
                Task.FromResult(
                    Result<CreateWorkspaceResponse>.Forbidden(MessageKeys.Common.Forbidden)
                )
            );

        var actionResult = await BuildController(stub)
            .CreateWorkspace(new CreateWorkspaceRequest { Name = "Acme Client" });

        var obj = Assert.IsType<ObjectResult>(actionResult);
        Assert.Equal(StatusCodes.Status403Forbidden, obj.StatusCode);
        var envelope = Assert.IsType<Result<CreateWorkspaceResponse>>(obj.Value);
        Assert.True(envelope.IsForbidden);
    }

    [Fact]
    public async Task CreateWorkspace_LimitReached_Returns400WithEnvelope()
    {
        var stub = Substitute.For<IWorkspaceCreationService>();
        stub.CreateForCurrentIdentityAsync(Arg.Any<CreateWorkspaceRequest>())
            .Returns(
                Task.FromResult(
                    Result<CreateWorkspaceResponse>.LimitReached(
                        MessageKeys.Workspace.OwnedLimitReached,
                        new PlanLimit(EntitlementCatalog.MaxOwnedWorkspaces, 1, 1, 7)
                    )
                )
            );

        var actionResult = await BuildController(stub)
            .CreateWorkspace(new CreateWorkspaceRequest { Name = "Acme Client" });

        var bad = Assert.IsType<BadRequestObjectResult>(actionResult);
        var envelope = Assert.IsType<Result<CreateWorkspaceResponse>>(bad.Value);
        Assert.False(envelope.IsSuccess);
        Assert.True(envelope.IsLimitReached);
        Assert.Equal(MessageKeys.Workspace.OwnedLimitReached, envelope.Message);
    }

    [Fact]
    public async Task CreateWorkspace_ValidationFailure_Returns400WithEnvelope()
    {
        var stub = Substitute.For<IWorkspaceCreationService>();
        stub.CreateForCurrentIdentityAsync(Arg.Any<CreateWorkspaceRequest>())
            .Returns(
                Task.FromResult(
                    Result<CreateWorkspaceResponse>.Failure(MessageKeys.Workspace.NameRequired)
                )
            );

        var actionResult = await BuildController(stub)
            .CreateWorkspace(new CreateWorkspaceRequest { Name = "" });

        var bad = Assert.IsType<BadRequestObjectResult>(actionResult);
        var envelope = Assert.IsType<Result<CreateWorkspaceResponse>>(bad.Value);
        Assert.False(envelope.IsSuccess);
        Assert.Equal(MessageKeys.Workspace.NameRequired, envelope.Message);
    }

    [Fact]
    public async Task GetWorkspaceAllowance_Success_Returns200WithEnvelope()
    {
        var response = new WorkspaceAllowanceResponse
        {
            Owned = 1,
            Max = 3,
            RequiresApproval = false,
            CanCreate = true,
        };
        var stub = Substitute.For<IWorkspaceCreationService>();
        stub.GetAllowanceAsync()
            .Returns(Task.FromResult(Result<WorkspaceAllowanceResponse>.Success(response)));

        var actionResult = await BuildController(stub).GetWorkspaceAllowance();

        var ok = Assert.IsType<OkObjectResult>(actionResult);
        var envelope = Assert.IsType<Result<WorkspaceAllowanceResponse>>(ok.Value);
        Assert.True(envelope.IsSuccess);
        Assert.Equal(1, envelope.Data!.Owned);
        Assert.Equal(3, envelope.Data.Max);
        Assert.True(envelope.Data.CanCreate);
    }
}
