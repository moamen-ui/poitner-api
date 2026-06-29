using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Demo;
using Pointer.Application.Response;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;

namespace Pointer.Application.Services.Implementation;

public class DemoService : IDemoService
{
    private const int DemoMaxActive = 100;
    private const int DemoTtlHours = 24;

    private readonly IUnitOfWork _unitOfWork;
    private readonly IPasswordHasher _passwordHasher;
    private readonly ITokenService _tokenService;

    public DemoService(
        IUnitOfWork unitOfWork,
        IPasswordHasher passwordHasher,
        ITokenService tokenService)
    {
        _unitOfWork = unitOfWork;
        _passwordHasher = passwordHasher;
        _tokenService = tokenService;
    }

    public async Task<Result<DemoSessionResponse>> ProvisionAsync(string serverUrl)
    {
        // a. Active cap check
        var active = await _unitOfWork.Repository<User>()
            .Query()
            .IgnoreQueryFilters()
            .CountAsync(u => u.IsDemo && u.DeletedAt == null && u.ExpiresAt > DateTime.UtcNow);

        if (active >= DemoMaxActive)
            return Result<DemoSessionResponse>.Failure("Demo is at capacity, please try again shortly.");

        // b. Resolve the "Workspace Admin" role
        var role = await _unitOfWork.Repository<Role>()
            .Query()
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(r => r.Name == "Workspace Admin" && r.DeletedAt == null);

        if (role == null)
            return Result<DemoSessionResponse>.Failure("Workspace Admin role not found. Please contact the administrator.");

        // c. Build demo user
        var slug = Guid.NewGuid().ToString("N")[..8];
        var publicId = Guid.NewGuid();
        var email = $"demo-{slug}@demo.pointer";
        var password = Guid.NewGuid().ToString("N")[..12] + "Aa1!";

        var demoUser = new User
        {
            PublicId = publicId,
            Email = email,
            PasswordHash = _passwordHasher.Hash(password),
            DisplayName = "Demo User",
            RoleId = role.Id,
            OwnerId = publicId,
            ApprovalStatus = ApprovalStatus.Approved,
            IsActive = true,
            IsDemo = true,
            ExpiresAt = DateTime.UtcNow.AddHours(DemoTtlHours),
        };

        await _unitOfWork.Repository<User>().AddAsync(demoUser);

        // d. Seed tenant data — Project first (needs SaveChanges to get Id)
        var project = new Project
        {
            Key = $"demo-{slug}",
            Name = "Demo Project",
            IsActive = true,
            OwnerId = publicId,
        };

        await _unitOfWork.Repository<Project>().AddAsync(project);
        await _unitOfWork.SaveChangesAsync();

        // Seed ~3 sample Comments on the project
        var comments = new[]
        {
            new Comment
            {
                ProjectId = project.Id,
                OwnerId = publicId,
                AuthorId = publicId,
                Environment = EnvironmentTag.Staging,
                Status = CommentStatus.Open,
                Body = "Sample: tighten this heading — font-size feels too large on mobile.",
                IsPrivate = false,
                Element = new ElementCapture
                {
                    Selector = "h1.hero-title",
                    Snapshot = "<h1 class=\"hero-title\">Welcome to the Demo</h1>",
                },
            },
            new Comment
            {
                ProjectId = project.Id,
                OwnerId = publicId,
                AuthorId = publicId,
                Environment = EnvironmentTag.Staging,
                Status = CommentStatus.ReadyToApply,
                Body = "Sample: button colour should match the brand palette (#1a73e8).",
                IsPrivate = false,
                Element = new ElementCapture
                {
                    Selector = "button.cta-primary",
                    Snapshot = "<button class=\"cta-primary\">Get Started</button>",
                },
            },
            new Comment
            {
                ProjectId = project.Id,
                OwnerId = publicId,
                AuthorId = publicId,
                Environment = EnvironmentTag.Staging,
                Status = CommentStatus.Applied,
                Body = "Sample: nav link spacing was too tight — fixed.",
                IsPrivate = false,
                Element = new ElementCapture
                {
                    Selector = "nav a",
                    Snapshot = "<a href=\"/about\">About</a>",
                },
            },
        };

        foreach (var comment in comments)
            await _unitOfWork.Repository<Comment>().AddAsync(comment);

        await _unitOfWork.SaveChangesAsync();

        // e. Issue token (Role must be populated for claims)
        demoUser.Role = role;
        var token = _tokenService.Issue(demoUser);

        // f. Return response
        return Result<DemoSessionResponse>.Success(new DemoSessionResponse
        {
            Token = token,
            Email = email,
            Password = password,
            ProjectKey = project.Key,
            ExpiresAt = demoUser.ExpiresAt!.Value,
            ServerUrl = serverUrl,
        });
    }
}
