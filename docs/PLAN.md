# Pointer API Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking. **After finishing each task, flip its row in [`TASKS.md`](TASKS.md) to ✅ and update the "Last updated" line.**

**Goal:** Replace Pointer's flat-file Node server with a standalone .NET 8 backend that stores element-level feedback in PostgreSQL, authenticates every comment against a real local account, and serves the web component + AI apply skill.

**Architecture:** Clean Architecture (`API` / `Application` / `Domain` / `Infrastructure`) mirroring `tuwaiq-clubs-api-dotnet-revamp` — `Result<T>` returns, Scrutor auto-registration, FluentValidation, EF Core + Postgres with snake_case mappings, audit fields auto-populated from the JWT `sub`. Auth is **local** (email/password, BCrypt, service-issued HS256 JWT) behind `ITokenService`/`ICurrentUser` seams so SSO can replace it later.

**Tech Stack:** .NET 8, EF Core 8 (Npgsql), FluentValidation, Scrutor, BCrypt.Net-Next, System.IdentityModel.Tokens.Jwt, Docker Compose (Postgres), `just`, CSharpier, xUnit (focused tests only).

## Global Constraints

- Root namespace: `Pointer.*` (e.g. `Pointer.API`, `Pointer.Domain.Entity`).
- File-scoped namespaces; `<Nullable>enable</Nullable>` in every project.
- DB columns are **snake_case**; tables snake_case plural (`users`, `projects`, `comments`, `replies`).
- Services MUST end with `Service` (Scrutor auto-registers); all services/repos **scoped**.
- Every service method returns `Result` or `Result<T>` — no exceptions for flow control.
- Soft delete only — never hard delete; every query filters `DeletedAt == null`.
- Use `DateTime.UtcNow` (this is a greenfield service — unlike clubs, we standardize on UTC).
- Routes lowercase; controllers under `/api`; static files `/pointer.js`, `/skill.md`.
- JWT: HS256, symmetric key from `JWT__SIGNING_KEY` env; 12h lifetime; no refresh token in v1.
- Password hashing: **BCrypt** (work factor 11).
- Identity is read ONLY through `ICurrentUser`; tokens issued/validated ONLY through `ITokenService`.

---

## File Structure

```
pointer-api/
├── Pointer.sln
├── Dockerfile · docker-compose.yaml · justfile · .editorconfig · .csharpierrc.json
├── .env.example
├── Domain/
│   ├── Pointer.Domain.csproj
│   ├── Entity/{BaseEntity,User,Project,Comment,Reply}.cs
│   ├── Enums/{Role,CommentStatus,EnvironmentTag}.cs
│   └── ValueObjects/ElementCapture.cs
├── Application/
│   ├── Pointer.Application.csproj
│   ├── DependencyInjection.cs · IApplicationAssemblyReference.cs
│   ├── Response/{Result,Result.T,PagedData,Pagination}.cs
│   ├── Abstractions/{ICurrentUser,ITokenService,IPasswordHasher}.cs
│   ├── DTOs/{Auth,User,Project,Comment}/*.cs
│   ├── Services/Interfaces/I*Service.cs
│   ├── Services/Implementation/*Service.cs
│   ├── Validators/*Validator.cs
│   └── Resources/MessageKeys.cs
├── Infrastructure/
│   ├── Pointer.Infrastructure.csproj
│   ├── DependencyInjection.cs
│   ├── AppDbContext.cs
│   ├── Mappings/{User,Project,Comment,Reply}Mapping.cs
│   ├── Repository/{IRepository,Repository,IUnitOfWork,UnitOfWork}.cs
│   ├── Auth/{JwtTokenService,BcryptPasswordHasher}.cs
│   ├── CurrentUser/HttpCurrentUser.cs
│   └── Migrations/*
├── API/
│   ├── Pointer.API.csproj
│   ├── Program.cs · appsettings.json
│   ├── Extensions/{AuthenticationExtensions,SwaggerExtensions}.cs
│   ├── Controllers/{AuthController,CommentsController,RepliesController,Admin/UsersController,Admin/ProjectsController}.cs
│   ├── Seed/AdminSeeder.cs
│   └── wwwroot/{pointer.js, skill.md, admin/index.html, admin/app.js, admin/style.css}
└── Tests/
    ├── Pointer.Tests.csproj
    └── {PasswordHasherTests,TokenServiceTests,CommentValidatorTests}.cs
```

---

## Phase 0 — Project scaffold & DX

### Task 0.1: Solution + four projects

**Files:** Create `Pointer.sln`, `Domain/Pointer.Domain.csproj`, `Application/Pointer.Application.csproj`, `Infrastructure/Pointer.Infrastructure.csproj`, `API/Pointer.API.csproj`, `Tests/Pointer.Tests.csproj`.

- [ ] **Step 1: Scaffold solution and projects**

```bash
cd /Users/momen/Desktop/REPOS/pointer-api
dotnet new sln -n Pointer
dotnet new classlib -n Pointer.Domain -o Domain -f net8.0
dotnet new classlib -n Pointer.Application -o Application -f net8.0
dotnet new classlib -n Pointer.Infrastructure -o Infrastructure -f net8.0
dotnet new webapi -n Pointer.API -o API -f net8.0 --no-https
dotnet new xunit -n Pointer.Tests -o Tests -f net8.0
dotnet sln add Domain Application Infrastructure API Tests
# rm default classlib files
rm -f Domain/Class1.cs Application/Class1.cs Infrastructure/Class1.cs
```

- [ ] **Step 2: Wire project references**

```bash
dotnet add Application reference Domain
dotnet add Infrastructure reference Application Domain
dotnet add API reference Application Infrastructure Domain
dotnet add Tests reference Application Infrastructure Domain API
```

- [ ] **Step 3: Add packages**

```bash
dotnet add Infrastructure package Npgsql.EntityFrameworkCore.PostgreSQL -v 8.*
dotnet add Infrastructure package Microsoft.EntityFrameworkCore.Design -v 8.*
dotnet add Infrastructure package BCrypt.Net-Next -v 4.*
dotnet add Infrastructure package System.IdentityModel.Tokens.Jwt -v 8.*
dotnet add Application package FluentValidation.DependencyInjectionExtensions -v 11.*
dotnet add Application package Scrutor -v 4.*
dotnet add API package Microsoft.AspNetCore.Authentication.JwtBearer -v 8.*
dotnet add API package Swashbuckle.AspNetCore -v 6.*
```

- [ ] **Step 4: Enable nullable + file-scoped namespaces in each csproj**

In all five `.csproj` `<PropertyGroup>`: ensure `<Nullable>enable</Nullable>` and `<ImplicitUsings>enable</ImplicitUsings>`.

- [ ] **Step 5: Verify build**

Run: `dotnet build`
Expected: `Build succeeded`.

- [ ] **Step 6: Commit**

```bash
git init && printf "bin/\nobj/\n.env\n*.user\n" > .gitignore
git add -A && git commit -m "chore: scaffold Pointer .NET solution (4 layers + tests)"
```

### Task 0.2: Docker, just, formatting, env

**Files:** Create `Dockerfile`, `docker-compose.yaml`, `justfile`, `.csharpierrc.json`, `.env.example`.

- [ ] **Step 1: `docker-compose.yaml`**

```yaml
services:
  db:
    image: postgres:15
    environment:
      POSTGRES_USER: pointer
      POSTGRES_PASSWORD: pointer
      POSTGRES_DB: pointer
    ports: ["5433:5432"]
    volumes: ["pgdata:/var/lib/postgresql/data"]
  api:
    build: { context: ., target: dev }
    env_file: .env
    ports: ["8090:8080"]
    depends_on: [db]
    volumes: ["./:/src"]
volumes: { pgdata: {} }
```

- [ ] **Step 2: `Dockerfile` (dev + final targets)**

```dockerfile
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS dev
WORKDIR /src
ENV ASPNETCORE_URLS=http://0.0.0.0:8080
CMD ["dotnet", "watch", "--project", "API", "run"]

FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src
COPY . .
RUN dotnet publish API -c Release -o /app

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS final
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["dotnet", "Pointer.API.dll"]
```

- [ ] **Step 3: `justfile`**

```make
set dotenv-load := true
up:        ; docker compose up
down:      ; docker compose down
build:     ; dotnet build
fmt:       ; dotnet csharpier .
migrate name: ; dotnet ef migrations add {{name}} -p Infrastructure -s API
db-update: ; dotnet ef database update -p Infrastructure -s API
test:      ; dotnet test
psql:      ; docker compose exec db psql -U pointer -d pointer
```

- [ ] **Step 4: `.csharpierrc.json`** → `{ "printWidth": 100, "useTabs": false, "tabWidth": 4 }`

- [ ] **Step 5: `.env.example`**

```
ConnectionStrings__Default=Host=db;Port=5432;Database=pointer;Username=pointer;Password=pointer
JWT__SIGNING_KEY=CHANGE_ME_min_32_chars_long_secret_key_123456
JWT__ISSUER=pointer-api
JWT__LIFETIME_HOURS=12
ADMIN__EMAIL=admin@pointer.local
ADMIN__PASSWORD=ChangeMe123!
DBMigrationEnabled=true
```

- [ ] **Step 6: Verify + commit**

Run: `cp .env.example .env && dotnet build`. Expected: success.
```bash
git add -A && git commit -m "chore: docker, just, csharpier, env scaffolding"
```

---

## Phase 1 — Domain

### Task 1.1: BaseEntity + enums

**Files:** Create `Domain/Entity/BaseEntity.cs`, `Domain/Enums/Role.cs`, `Domain/Enums/CommentStatus.cs`, `Domain/Enums/EnvironmentTag.cs`.

**Interfaces — Produces:** `BaseEntity` (int `Id`; `DateTime CreatedAt`; `Guid CreatedBy`; `DateTime? UpdatedAt`; `Guid? UpdatedBy`; `DateTime? DeletedAt`; `Guid? DeletedBy`). Enums below.

- [ ] **Step 1: `BaseEntity.cs`**

```csharp
namespace Pointer.Domain.Entity;

public abstract class BaseEntity
{
    public int Id { get; set; }
    public DateTime CreatedAt { get; set; }
    public Guid CreatedBy { get; set; }
    public DateTime? UpdatedAt { get; set; }
    public Guid? UpdatedBy { get; set; }
    public DateTime? DeletedAt { get; set; }
    public Guid? DeletedBy { get; set; }
}
```

- [ ] **Step 2: Enums**

```csharp
// Domain/Enums/Role.cs
namespace Pointer.Domain.Enums;
public enum Role { Admin = 1, Developer = 2, PM = 3, Tester = 4, Client = 5 }
```
```csharp
// Domain/Enums/CommentStatus.cs
namespace Pointer.Domain.Enums;
public enum CommentStatus { Open = 1, ReadyToApply = 2, Applied = 3 }
```
```csharp
// Domain/Enums/EnvironmentTag.cs
namespace Pointer.Domain.Enums;
public enum EnvironmentTag { Local = 1, Staging = 2, Production = 3 }
```

- [ ] **Step 3: Build + commit**

Run: `dotnet build Domain`. Expected: success.
```bash
git add Domain && git commit -m "feat(domain): BaseEntity + Role/CommentStatus/EnvironmentTag enums"
```

### Task 1.2: Entities + ElementCapture

**Files:** Create `Domain/ValueObjects/ElementCapture.cs`, `Domain/Entity/{User,Project,Comment,Reply}.cs`.

**Interfaces — Produces:**
- `User` : BaseEntity — `string Email`, `string PasswordHash`, `string DisplayName`, `Role Role`, `bool IsActive`.
- `Project` : BaseEntity — `string Key`, `string Name`, `bool IsActive`, `ICollection<Comment> Comments`.
- `Comment` : BaseEntity — `int ProjectId`, `Project Project`, `EnvironmentTag Environment`, `CommentStatus Status`, `Guid AuthorId`, `string Body`, `ElementCapture Element`, `DateTime? AppliedAt`, `Guid? AppliedBy`, `string? AppliedByLabel`, `ICollection<Reply> Replies`.
- `Reply` : BaseEntity — `int CommentId`, `Comment Comment`, `Guid AuthorId`, `string Body`.
- `ElementCapture` — `string? Selector`, `string? Snapshot`, `string? Classes`, `string? ComputedStyles`, `string? AppliedCssRules`, `string? SourcePath`, `string? ParentInfo` (POCO serialized to jsonb).

- [ ] **Step 1: `ElementCapture.cs`**

```csharp
namespace Pointer.Domain.ValueObjects;

public class ElementCapture
{
    public string? Selector { get; set; }
    public string? Snapshot { get; set; }
    public string? Classes { get; set; }
    public string? ComputedStyles { get; set; }
    public string? AppliedCssRules { get; set; }
    public string? SourcePath { get; set; }
    public string? ParentInfo { get; set; }
}
```

- [ ] **Step 2: Entities** (`User`, `Project`, `Comment`, `Reply` exactly per the Produces signatures above; `Author`/nav strings default `string.Empty`, collections `= new List<...>()`, `Element = new()`).

```csharp
// Domain/Entity/User.cs
using Pointer.Domain.Enums;
namespace Pointer.Domain.Entity;
public class User : BaseEntity
{
    public string Email { get; set; } = string.Empty;
    public string PasswordHash { get; set; } = string.Empty;
    public string DisplayName { get; set; } = string.Empty;
    public Role Role { get; set; }
    public bool IsActive { get; set; } = true;
}
```
```csharp
// Domain/Entity/Project.cs
namespace Pointer.Domain.Entity;
public class Project : BaseEntity
{
    public string Key { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public ICollection<Comment> Comments { get; set; } = new List<Comment>();
}
```
```csharp
// Domain/Entity/Comment.cs
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
namespace Pointer.Domain.Entity;
public class Comment : BaseEntity
{
    public int ProjectId { get; set; }
    public Project Project { get; set; } = null!;
    public EnvironmentTag Environment { get; set; }
    public CommentStatus Status { get; set; } = CommentStatus.Open;
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
    public ElementCapture Element { get; set; } = new();
    public DateTime? AppliedAt { get; set; }
    public Guid? AppliedBy { get; set; }
    public string? AppliedByLabel { get; set; }
    public ICollection<Reply> Replies { get; set; } = new List<Reply>();
}
```
```csharp
// Domain/Entity/Reply.cs
namespace Pointer.Domain.Entity;
public class Reply : BaseEntity
{
    public int CommentId { get; set; }
    public Comment Comment { get; set; } = null!;
    public Guid AuthorId { get; set; }
    public string Body { get; set; } = string.Empty;
}
```

- [ ] **Step 3: Build + commit**

Run: `dotnet build Domain`. Expected: success.
```bash
git add Domain && git commit -m "feat(domain): User/Project/Comment/Reply entities + ElementCapture"
```

---

## Phase 2 — Application foundations (Result, abstractions)

### Task 2.1: Result / Result<T> / paging

**Files:** Create `Application/Response/Result.cs`, `Result.T` (in same file ok), `PagedData.cs`, `Pagination.cs`.

**Interfaces — Produces:**
- `Result` — `bool IsSuccess`, `bool IsNotFound`, `bool IsConflict`, `string? Message`, static `Success(string? msg=null)`, `Failure(string msg)`, `NotFound(string msg)`, `Conflict(string msg)`.
- `Result<T>` — adds `T? Data`; same statics returning `Result<T>` plus `Success(T data, string? msg=null)`.
- `PagedData<T>(IReadOnlyList<T> Items, Pagination Pagination)`; `Pagination { int PageNumber, PageSize, TotalItems, TotalPages }`.

- [ ] **Step 1: Implement** (plain POCOs + static factories, matching signatures above).

```csharp
namespace Pointer.Application.Response;

public class Result
{
    public bool IsSuccess { get; protected init; }
    public bool IsNotFound { get; protected init; }
    public bool IsConflict { get; protected init; }
    public string? Message { get; protected init; }

    public static Result Success(string? msg = null) => new() { IsSuccess = true, Message = msg };
    public static Result Failure(string msg) => new() { IsSuccess = false, Message = msg };
    public static Result NotFound(string msg) => new() { IsNotFound = true, Message = msg };
    public static Result Conflict(string msg) => new() { IsConflict = true, Message = msg };
}

public class Result<T> : Result
{
    public T? Data { get; private init; }
    public static Result<T> Success(T data, string? msg = null) =>
        new() { IsSuccess = true, Data = data, Message = msg };
    public new static Result<T> Failure(string msg) => new() { IsSuccess = false, Message = msg };
    public new static Result<T> NotFound(string msg) => new() { IsNotFound = true, Message = msg };
    public new static Result<T> Conflict(string msg) => new() { IsConflict = true, Message = msg };
}
```
```csharp
// Pagination.cs + PagedData.cs
namespace Pointer.Application.Response;
public class Pagination
{
    public int PageNumber { get; set; }
    public int PageSize { get; set; }
    public int TotalItems { get; set; }
    public int TotalPages { get; set; }
}
public class PagedData<T>(IReadOnlyList<T> items, Pagination pagination)
{
    public IReadOnlyList<T> Items { get; } = items;
    public Pagination Pagination { get; } = pagination;
}
```

- [ ] **Step 2: Build + commit** — `dotnet build Application`; `git commit -m "feat(app): Result/Result<T> + paging types"`.

### Task 2.2: Abstractions (the SSO-swap seams)

**Files:** Create `Application/Abstractions/{ICurrentUser,ITokenService,IPasswordHasher}.cs`, `Application/IApplicationAssemblyReference.cs`, `Application/Resources/MessageKeys.cs`.

**Interfaces — Produces:**
- `ICurrentUser` — `Guid? Id { get; }`, `Role? Role { get; }`.
- `ITokenService` — `string Issue(User user)`.
- `IPasswordHasher` — `string Hash(string password)`, `bool Verify(string password, string hash)`.
- `IApplicationAssemblyReference` (empty marker interface).
- `MessageKeys` — nested static classes `Auth`, `User`, `Project`, `Comment` with const string keys used by validators/services (e.g. `Auth.InvalidCredentials`, `Comment.NotFound`, `Comment.BodyRequired`, `Project.NotFound`, `User.EmailTaken`).

- [ ] **Step 1: Implement the three interfaces + marker + MessageKeys** (string keys are plain constants returned directly as messages — no resx needed for v1).

```csharp
// Abstractions/ICurrentUser.cs
using Pointer.Domain.Enums;
namespace Pointer.Application.Abstractions;
public interface ICurrentUser { Guid? Id { get; } Role? Role { get; } }
```
```csharp
// Abstractions/ITokenService.cs
using Pointer.Domain.Entity;
namespace Pointer.Application.Abstractions;
public interface ITokenService { string Issue(User user); }
```
```csharp
// Abstractions/IPasswordHasher.cs
namespace Pointer.Application.Abstractions;
public interface IPasswordHasher { string Hash(string password); bool Verify(string password, string hash); }
```
```csharp
// IApplicationAssemblyReference.cs
namespace Pointer.Application;
public interface IApplicationAssemblyReference { }
```
```csharp
// Resources/MessageKeys.cs
namespace Pointer.Application.Resources;
public static class MessageKeys
{
    public static class Auth { public const string InvalidCredentials = "Invalid email or password."; public const string Inactive = "Account is disabled."; }
    public static class User { public const string NotFound = "User not found."; public const string EmailTaken = "Email already in use."; public const string EmailRequired = "Email is required."; public const string PasswordRequired = "Password is required."; public const string PasswordWeak = "Password must be at least 8 characters."; public const string DisplayNameRequired = "Display name is required."; }
    public static class Project { public const string NotFound = "Project not found."; public const string KeyTaken = "Project key already exists."; public const string KeyRequired = "Project key is required."; }
    public static class Comment { public const string NotFound = "Comment not found."; public const string BodyRequired = "Comment body is required."; public const string Created = "Comment created."; public const string Applied = "Comment marked applied."; }
}
```

- [ ] **Step 2: Build + commit** — `dotnet build Application`; `git commit -m "feat(app): ICurrentUser/ITokenService/IPasswordHasher seams + MessageKeys"`.

---

## Phase 3 — Infrastructure (EF, repo, hashing, token, current-user)

### Task 3.1: AppDbContext + mappings + audit

**Files:** Create `Infrastructure/AppDbContext.cs`, `Infrastructure/Mappings/{User,Project,Comment,Reply}Mapping.cs`.

**Interfaces — Consumes:** entities (1.2), `ICurrentUser` (2.2). **Produces:** `AppDbContext` with `DbSet<User/Project/Comment/Reply>` and `SaveChangesAsync` audit population from `ICurrentUser.Id`.

- [ ] **Step 1: `AppDbContext.cs`**

```csharp
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;

namespace Pointer.Infrastructure;

public class AppDbContext(DbContextOptions<AppDbContext> options, ICurrentUser currentUser) : DbContext(options)
{
    public DbSet<User> Users => Set<User>();
    public DbSet<Project> Projects => Set<Project>();
    public DbSet<Comment> Comments => Set<Comment>();
    public DbSet<Reply> Replies => Set<Reply>();

    protected override void OnModelCreating(ModelBuilder b) =>
        b.ApplyConfigurationsFromAssembly(typeof(AppDbContext).Assembly);

    public override Task<int> SaveChangesAsync(CancellationToken ct = default)
    {
        var now = DateTime.UtcNow;
        var uid = currentUser.Id ?? Guid.Empty;
        foreach (var e in ChangeTracker.Entries<BaseEntity>())
        {
            if (e.State == EntityState.Added) { e.Entity.CreatedAt = now; e.Entity.CreatedBy = uid; }
            else if (e.State == EntityState.Modified)
            {
                e.Entity.UpdatedAt = now; e.Entity.UpdatedBy = uid;
                if (e.Entity.DeletedAt is not null && e.Property(nameof(BaseEntity.DeletedAt)).IsModified)
                    e.Entity.DeletedBy = uid;
            }
        }
        return base.SaveChangesAsync(ct);
    }
}
```

- [ ] **Step 2: Mappings** (snake_case tables/columns; `Comment.Element` as jsonb via `.ToJson()`; unique indexes on `users.email`, `projects.key`).

```csharp
// Mappings/UserMapping.cs
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using Pointer.Domain.Entity;
namespace Pointer.Infrastructure.Mappings;
public class UserMapping : IEntityTypeConfiguration<User>
{
    public void Configure(EntityTypeBuilder<User> b)
    {
        b.ToTable("users");
        b.Property(x => x.Email).HasColumnName("email").IsRequired().HasMaxLength(256);
        b.HasIndex(x => x.Email).IsUnique();
        b.Property(x => x.PasswordHash).HasColumnName("password_hash").IsRequired();
        b.Property(x => x.DisplayName).HasColumnName("display_name").IsRequired().HasMaxLength(128);
        b.Property(x => x.Role).HasColumnName("role").IsRequired();
        b.Property(x => x.IsActive).HasColumnName("is_active");
    }
}
```
```csharp
// Mappings/ProjectMapping.cs
public class ProjectMapping : IEntityTypeConfiguration<Project>
{
    public void Configure(EntityTypeBuilder<Project> b)
    {
        b.ToTable("projects");
        b.Property(x => x.Key).HasColumnName("key").IsRequired().HasMaxLength(64);
        b.HasIndex(x => x.Key).IsUnique();
        b.Property(x => x.Name).HasColumnName("name").IsRequired().HasMaxLength(128);
        b.Property(x => x.IsActive).HasColumnName("is_active");
    }
}
```
```csharp
// Mappings/CommentMapping.cs
public class CommentMapping : IEntityTypeConfiguration<Comment>
{
    public void Configure(EntityTypeBuilder<Comment> b)
    {
        b.ToTable("comments");
        b.Property(x => x.ProjectId).HasColumnName("project_id");
        b.Property(x => x.Environment).HasColumnName("environment");
        b.Property(x => x.Status).HasColumnName("status");
        b.Property(x => x.AuthorId).HasColumnName("author_id");
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.Property(x => x.AppliedAt).HasColumnName("applied_at");
        b.Property(x => x.AppliedBy).HasColumnName("applied_by");
        b.Property(x => x.AppliedByLabel).HasColumnName("applied_by_label").HasMaxLength(256);
        b.OwnsOne(x => x.Element, e => e.ToJson("element"));
        b.HasOne(x => x.Project).WithMany(p => p.Comments).HasForeignKey(x => x.ProjectId).OnDelete(DeleteBehavior.Restrict);
        b.HasIndex(x => new { x.ProjectId, x.Status });
    }
}
```
```csharp
// Mappings/ReplyMapping.cs
public class ReplyMapping : IEntityTypeConfiguration<Reply>
{
    public void Configure(EntityTypeBuilder<Reply> b)
    {
        b.ToTable("replies");
        b.Property(x => x.CommentId).HasColumnName("comment_id");
        b.Property(x => x.AuthorId).HasColumnName("author_id");
        b.Property(x => x.Body).HasColumnName("body").IsRequired();
        b.HasOne(x => x.Comment).WithMany(c => c.Replies).HasForeignKey(x => x.CommentId).OnDelete(DeleteBehavior.Cascade);
    }
}
```
> Note: also map BaseEntity columns (`id`,`created_at`,`created_by`,`updated_at`,`updated_by`,`deleted_at`,`deleted_by`) — add a shared private helper or set `b.Property(x=>x.CreatedAt).HasColumnName("created_at")` etc. in each mapping.

- [ ] **Step 3: Build + commit** — `dotnet build Infrastructure`; `git commit -m "feat(infra): AppDbContext + snake_case mappings + jsonb element + audit"`.

### Task 3.2: Repository + UnitOfWork

**Files:** Create `Infrastructure/Repository/{IRepository,Repository,IUnitOfWork,UnitOfWork}.cs`. Put `IRepository`/`IUnitOfWork` in `Application/Abstractions` so services depend on Application only.

**Interfaces — Produces:** `IRepository<T> { IQueryable<T> Query(); Task AddAsync(T); void Update(T); Task<T?> GetByIdAsync(int); }`; `IUnitOfWork { IRepository<T> Repository<T>() where T:BaseEntity; Task<int> SaveChangesAsync(); }`.

- [ ] **Step 1: Interfaces in `Application/Abstractions/IRepository.cs` + `IUnitOfWork.cs`** (signatures above; `using Pointer.Domain.Entity;`).
- [ ] **Step 2: `Repository<T>` + `UnitOfWork` in Infrastructure** (generic repo over `AppDbContext.Set<T>()`; `UnitOfWork` caches repos in a dict and delegates `SaveChangesAsync`).

```csharp
// Infrastructure/Repository/Repository.cs
using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
namespace Pointer.Infrastructure.Repository;
public class Repository<T>(AppDbContext db) : IRepository<T> where T : BaseEntity
{
    private readonly DbSet<T> _set = db.Set<T>();
    public IQueryable<T> Query() => _set;
    public async Task AddAsync(T e) => await _set.AddAsync(e);
    public void Update(T e) => _set.Update(e);
    public async Task<T?> GetByIdAsync(int id) => await _set.FindAsync(id);
}
```
```csharp
// Infrastructure/Repository/UnitOfWork.cs
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
namespace Pointer.Infrastructure.Repository;
public sealed class UnitOfWork(AppDbContext db) : IUnitOfWork
{
    private readonly Dictionary<Type, object> _repos = new();
    public IRepository<T> Repository<T>() where T : BaseEntity
    {
        if (!_repos.TryGetValue(typeof(T), out var r)) { r = new Repository<T>(db); _repos[typeof(T)] = r; }
        return (IRepository<T>)r;
    }
    public Task<int> SaveChangesAsync() => db.SaveChangesAsync();
}
```

- [ ] **Step 3: Build + commit** — `dotnet build`; `git commit -m "feat(infra): generic Repository + UnitOfWork"`.

### Task 3.3: BcryptPasswordHasher (with unit test)

**Files:** Create `Infrastructure/Auth/BcryptPasswordHasher.cs`, `Tests/PasswordHasherTests.cs`.

- [ ] **Step 1: Write failing test**

```csharp
using Pointer.Infrastructure.Auth;
using Xunit;
public class PasswordHasherTests
{
    [Fact] public void Hash_then_Verify_roundtrips()
    {
        var h = new BcryptPasswordHasher();
        var hash = h.Hash("s3cret!");
        Assert.True(h.Verify("s3cret!", hash));
        Assert.False(h.Verify("wrong", hash));
    }
}
```

- [ ] **Step 2: Run → fails** — `dotnet test --filter PasswordHasherTests`. Expected: compile error (type missing).
- [ ] **Step 3: Implement**

```csharp
using Pointer.Application.Abstractions;
namespace Pointer.Infrastructure.Auth;
public class BcryptPasswordHasher : IPasswordHasher
{
    public string Hash(string password) => BCrypt.Net.BCrypt.HashPassword(password, workFactor: 11);
    public bool Verify(string password, string hash) => BCrypt.Net.BCrypt.Verify(password, hash);
}
```

- [ ] **Step 4: Run → passes** — `dotnet test --filter PasswordHasherTests`. Expected: PASS.
- [ ] **Step 5: Commit** — `git commit -m "feat(infra): BCrypt password hasher + test"`.

### Task 3.4: JwtTokenService + HttpCurrentUser (with token test)

**Files:** Create `Infrastructure/Auth/JwtTokenService.cs`, `Infrastructure/CurrentUser/HttpCurrentUser.cs`, `Tests/TokenServiceTests.cs`. Add `JwtOptions` POCO in Infrastructure (`SigningKey`, `Issuer`, `LifetimeHours`).

- [ ] **Step 1: Failing test** — issue a token for a `User{Id-mapped sub}`, parse it, assert `sub`, `role`, `email` claims present.

```csharp
using Microsoft.Extensions.Options;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Infrastructure.Auth;
using System.IdentityModel.Tokens.Jwt;
using Xunit;
public class TokenServiceTests
{
    [Fact] public void Issue_includes_sub_role_email()
    {
        var opts = Options.Create(new JwtOptions { SigningKey = new string('k', 40), Issuer = "pointer-api", LifetimeHours = 12 });
        var svc = new JwtTokenService(opts);
        var token = svc.Issue(new User { Id = 7, Email = "a@b.c", DisplayName = "A", Role = Role.Developer });
        var jwt = new JwtSecurityTokenHandler().ReadJwtToken(token);
        Assert.Equal("a@b.c", jwt.Claims.First(c => c.Type == "email").Value);
        Assert.Equal(((int)Role.Developer).ToString(), jwt.Claims.First(c => c.Type == "role").Value);
        Assert.False(string.IsNullOrEmpty(jwt.Subject));
    }
}
```

> **`sub` strategy:** `User.Id` is an int but audit fields are `Guid`. Map the int id into a deterministic Guid for `sub` via `new Guid(...)`? Simpler: store `sub` = a per-user `Guid` column. **Decision:** add `Guid PublicId` to `User` (default `Guid.NewGuid()` on create) used as `sub`; `ICurrentUser.Id` returns that Guid; audit `CreatedBy` stores it. Update Task 1.2 `User` to include `Guid PublicId`, Task 3.1 mapping `public_id` unique. (Applied here to keep types consistent.)

- [ ] **Step 2: Run → fails**.
- [ ] **Step 3: Implement `JwtOptions`, `JwtTokenService`** (HS256; claims `sub=PublicId`, `email`, `name`, `role=(int)Role`; `exp` per lifetime), and **`HttpCurrentUser`** (reads `ClaimTypes.NameIdentifier`/`sub` → Guid, `role` claim → `Role`).

```csharp
// Infrastructure/Auth/JwtTokenService.cs
using System.IdentityModel.Tokens.Jwt;
using System.Security.Claims;
using System.Text;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using Pointer.Application.Abstractions;
using Pointer.Domain.Entity;
namespace Pointer.Infrastructure.Auth;
public class JwtOptions { public string SigningKey { get; set; } = ""; public string Issuer { get; set; } = "pointer-api"; public int LifetimeHours { get; set; } = 12; }
public class JwtTokenService(IOptions<JwtOptions> opts) : ITokenService
{
    public string Issue(User u)
    {
        var o = opts.Value;
        var key = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(o.SigningKey));
        var creds = new SigningCredentials(key, SecurityAlgorithms.HmacSha256);
        var claims = new[]
        {
            new Claim(JwtRegisteredClaimNames.Sub, u.PublicId.ToString()),
            new Claim("email", u.Email),
            new Claim("name", u.DisplayName),
            new Claim("role", ((int)u.Role).ToString()),
        };
        var token = new JwtSecurityToken(o.Issuer, o.Issuer, claims,
            expires: DateTime.UtcNow.AddHours(o.LifetimeHours), signingCredentials: creds);
        return new JwtSecurityTokenHandler().WriteToken(token);
    }
}
```
```csharp
// Infrastructure/CurrentUser/HttpCurrentUser.cs
using System.Security.Claims;
using Microsoft.AspNetCore.Http;
using Pointer.Application.Abstractions;
using Pointer.Domain.Enums;
namespace Pointer.Infrastructure.CurrentUser;
public class HttpCurrentUser(IHttpContextAccessor accessor) : ICurrentUser
{
    public Guid? Id => Guid.TryParse(accessor.HttpContext?.User.FindFirst(ClaimTypes.NameIdentifier)?.Value
        ?? accessor.HttpContext?.User.FindFirst("sub")?.Value, out var g) ? g : null;
    public Role? Role => Enum.TryParse<Role>(accessor.HttpContext?.User.FindFirst("role")?.Value, out var r) ? r : null;
}
```

- [ ] **Step 4: Run → passes**.
- [ ] **Step 5: Commit** — `git commit -m "feat(infra): JWT token service + HttpCurrentUser + test"`.

### Task 3.5: Infrastructure DI

**Files:** Create `Infrastructure/DependencyInjection.cs`.

- [ ] **Step 1: `AddInfrastructure(IServiceCollection, IConfiguration)`** — register `AppDbContext` (Npgsql, conn from `ConnectionStrings:Default`, retry policy), `IRepository<>`→`Repository<>`, `IUnitOfWork`→`UnitOfWork`, `IPasswordHasher`→`BcryptPasswordHasher`, `ITokenService`→`JwtTokenService`, `ICurrentUser`→`HttpCurrentUser`, `AddHttpContextAccessor()`, bind `JwtOptions` from `JWT` section.

```csharp
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Pointer.Application.Abstractions;
using Pointer.Infrastructure.Auth;
using Pointer.Infrastructure.CurrentUser;
using Pointer.Infrastructure.Repository;
namespace Pointer.Infrastructure;
public static class DependencyInjection
{
    public static IServiceCollection AddInfrastructure(this IServiceCollection s, IConfiguration c)
    {
        s.AddDbContext<AppDbContext>(o => o.UseNpgsql(c.GetConnectionString("Default"),
            n => n.EnableRetryOnFailure(5, TimeSpan.FromSeconds(30), null)));
        s.Configure<JwtOptions>(c.GetSection("JWT"));
        s.AddHttpContextAccessor();
        s.AddScoped(typeof(IRepository<>), typeof(Repository<>));
        s.AddScoped<IUnitOfWork, UnitOfWork>();
        s.AddScoped<IPasswordHasher, BcryptPasswordHasher>();
        s.AddScoped<ITokenService, JwtTokenService>();
        s.AddScoped<ICurrentUser, HttpCurrentUser>();
        return s;
    }
}
```
> Bind note: `JWT__SIGNING_KEY` env maps to `JWT:SigningKey` only if the POCO prop is `SigningKey`. Use config keys `JWT:SigningKey/Issuer/LifetimeHours`; update `.env.example` to `JWT__SigningKey=...`, `JWT__Issuer=...`, `JWT__LifetimeHours=12`.

- [ ] **Step 2: Build + commit** — `dotnet build`; `git commit -m "feat(infra): DI registration"`.

---

## Phase 4 — Application services & DTOs

> Pattern for every service: ctor injects `IUnitOfWork`, `ICurrentUser`, and (auth) `IPasswordHasher`/`ITokenService`. Read with `.AsNoTracking().Where(x => x.DeletedAt == null)`. Map inline. Return `Result<T>`.

### Task 4.1: Auth DTOs + AuthService + validator + test

**Files:** Create `Application/DTOs/Auth/{LoginRequest,LoginResponse,MeResponse}.cs`, `Application/Services/Interfaces/IAuthService.cs`, `Application/Services/Implementation/AuthService.cs`, `Application/Validators/LoginValidator.cs`, `Tests/CommentValidatorTests.cs` (add login case).

**Interfaces — Produces:** `IAuthService { Task<Result<LoginResponse>> LoginAsync(LoginRequest); Result<MeResponse> Me(); }`. `LoginRequest{string Email,Password}`; `LoginResponse{string Token, MeResponse User}`; `MeResponse{Guid Id,string Email,string DisplayName,Role Role}`.

- [ ] **Step 1: DTOs** (per signatures).
- [ ] **Step 2: `AuthService`** — find active user by email; `IPasswordHasher.Verify`; on fail `Result.Failure(MessageKeys.Auth.InvalidCredentials)`; on success `ITokenService.Issue` → `LoginResponse`. `Me()` builds from `ICurrentUser` (re-query for details).
- [ ] **Step 3: `LoginValidator`** — `Email` NotEmpty+EmailAddress, `Password` NotEmpty.
- [ ] **Step 4: Build + commit** — `git commit -m "feat(app): auth service + DTOs + validator"`.

### Task 4.2: User (admin) DTOs + UserService

**Files:** `Application/DTOs/User/{CreateUserRequest,UpdateUserRequest,UserResponse}.cs`, `Services/Interfaces/IUserService.cs`, `Services/Implementation/UserService.cs`, `Validators/CreateUserValidator.cs`.

**Interfaces — Produces:** `IUserService { Task<Result<UserResponse>> CreateAsync(CreateUserRequest); Task<Result<List<UserResponse>>> ListAsync(); Task<Result<UserResponse>> UpdateAsync(int id, UpdateUserRequest); }`. `CreateUserRequest{string Email,Password,DisplayName; Role Role}`; `UpdateUserRequest{Role? Role; bool? IsActive; string? Password}`; `UserResponse{int Id; Guid PublicId; string Email,DisplayName; Role Role; bool IsActive}`.

- [ ] **Step 1: DTOs**.
- [ ] **Step 2: `UserService.CreateAsync`** — reject duplicate email (`Conflict(MessageKeys.User.EmailTaken)`), hash password, set `PublicId = Guid.NewGuid()`, save. `ListAsync` returns all non-deleted. `UpdateAsync` sets role/active/password (re-hash if provided).
- [ ] **Step 3: `CreateUserValidator`** — email valid+required, password min 8, displayName required, role `IsInEnum`.
- [ ] **Step 4: Build + commit** — `git commit -m "feat(app): user admin service + DTOs + validator"`.

### Task 4.3: Project DTOs + ProjectService

**Files:** `Application/DTOs/Project/{CreateProjectRequest,ProjectResponse,UpdateProjectRequest}.cs`, `Services/Interfaces/IProjectService.cs`, `Services/Implementation/ProjectService.cs`, `Validators/CreateProjectValidator.cs`.

**Interfaces — Produces:** `IProjectService { Task<Result<ProjectResponse>> CreateAsync(CreateProjectRequest); Task<Result<List<ProjectResponse>>> ListAsync(); Task<Result<ProjectResponse>> UpdateAsync(int,UpdateProjectRequest); Task<Result<int>> ResolveIdAsync(string key); }`. `ResolveIdAsync` returns project id by key (or `NotFound`) — used by CommentService. `CreateProjectRequest{string Key,Name}`; `ProjectResponse{int Id;string Key,Name;bool IsActive}`; `UpdateProjectRequest{string? Name; bool? IsActive}`.

- [ ] **Step 1: DTOs**.
- [ ] **Step 2: `ProjectService`** — `CreateAsync` rejects dup key (`Conflict`), normalizes key to lowercase. `ResolveIdAsync(key)` → active project id or `NotFound(MessageKeys.Project.NotFound)`.
- [ ] **Step 3: `CreateProjectValidator`** — key required, `^[a-z0-9._-]+$`, name required.
- [ ] **Step 4: Build + commit** — `git commit -m "feat(app): project service + DTOs + validator"`.

### Task 4.4: Comment/Reply DTOs + CommentService + validator test

**Files:** `Application/DTOs/Comment/{CreateCommentRequest,ElementCaptureDto,CommentResponse,CommentListItemDto,UpdateCommentStatusRequest,AddReplyRequest,ReplyResponse,CommentFilter}.cs`, `Services/Interfaces/ICommentService.cs`, `Services/Implementation/CommentService.cs`, `Validators/CreateCommentValidator.cs`, `Tests/CommentValidatorTests.cs`.

**Interfaces — Consumes:** `IProjectService.ResolveIdAsync`. **Produces:**
`ICommentService {`
` Task<Result<CommentResponse>> CreateAsync(string projectKey, CreateCommentRequest, Guid authorId);`
` Task<Result<PagedData<CommentListItemDto>>> ListAsync(string projectKey, CommentFilter);`
` Task<Result<CommentResponse>> GetByIdAsync(int id);`
` Task<Result<CommentResponse>> UpdateStatusAsync(int id, UpdateCommentStatusRequest, Guid actorId);`
` Task<Result<ReplyResponse>> AddReplyAsync(int commentId, AddReplyRequest, Guid authorId);`
` Task<Result> DeleteAsync(int id, Guid actorId, bool isAdmin); }`
DTO shapes: `CreateCommentRequest{string Body; EnvironmentTag Environment; ElementCaptureDto Element}`; `ElementCaptureDto` mirrors `ElementCapture` fields; `UpdateCommentStatusRequest{CommentStatus Status; string? Reply; string? AppliedByLabel}`; `CommentFilter{CommentStatus? Status; EnvironmentTag? Environment; int PageNumber=1; PageSize=50}`; `CommentResponse`/`CommentListItemDto` include id, status, environment, body, authorId, element (response only), createdAt, appliedAt/By/Label.

- [ ] **Step 1: DTOs**.
- [ ] **Step 2: Failing validator test**

```csharp
using FluentValidation.TestHelper;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.Validators;
using Xunit;
public class CommentValidatorTests
{
    [Fact] public void Rejects_empty_body()
    {
        var r = new CreateCommentValidator().TestValidate(new CreateCommentRequest { Body = "" });
        r.ShouldHaveValidationErrorFor(x => x.Body);
    }
}
```
Add `FluentValidation.TestHelper` via `dotnet add Tests package FluentValidation -v 11.*`.

- [ ] **Step 3: Run → fails**.
- [ ] **Step 4: Implement `CreateCommentValidator`** (Body NotEmpty+MaxLength(4000), Environment IsInEnum) and **`CommentService`**:
  - `CreateAsync`: `ResolveIdAsync(projectKey)`; build `Comment{ ProjectId, Environment, Status=Open, AuthorId=authorId, Body, Element=map(dto) }`; `AddAsync`+`SaveChangesAsync`; map response.
  - `ListAsync`: query by project id + filters, paged, `AsNoTracking`, order `CreatedAt` desc.
  - `UpdateStatusAsync`: load; set status; if `Applied` → `AppliedAt=UtcNow, AppliedBy=actorId, AppliedByLabel=req.AppliedByLabel`; if `req.Reply` present add a `Reply`. Save.
  - `AddReplyAsync`: load comment (NotFound guard); add reply.
  - `DeleteAsync`: load; allow if `actorId == comment.AuthorId || isAdmin` else `Result.Failure`; set `DeletedAt=UtcNow`.
- [ ] **Step 5: Run → passes** — `dotnet test --filter CommentValidatorTests`.
- [ ] **Step 6: Commit** — `git commit -m "feat(app): comment+reply service, DTOs, validator + test"`.

### Task 4.5: Application DI (Scrutor + validators)

**Files:** Create `Application/DependencyInjection.cs`.

- [ ] **Step 1: `AddApplication()`** — `AddValidatorsFromAssemblyContaining<IApplicationAssemblyReference>()`; Scrutor scan classes ending `Service` → `AsImplementedInterfaces().WithScopedLifetime()`.

```csharp
using System.Reflection;
using FluentValidation;
using Microsoft.Extensions.DependencyInjection;
namespace Pointer.Application;
public static class DependencyInjection
{
    public static IServiceCollection AddApplication(this IServiceCollection s)
    {
        var asm = typeof(IApplicationAssemblyReference).Assembly;
        s.AddValidatorsFromAssembly(asm);
        s.Scan(scan => scan.FromAssemblies(asm)
            .AddClasses(c => c.Where(t => t.Name.EndsWith("Service")))
            .AsImplementedInterfaces().WithScopedLifetime());
        return s;
    }
}
```

- [ ] **Step 2: Build + commit** — `git commit -m "feat(app): DI (Scrutor + FluentValidation)"`.

---

## Phase 5 — API (auth pipeline, controllers, seeding, migration)

### Task 5.1: Program.cs + auth pipeline + validation filter

**Files:** Modify `API/Program.cs`; create `API/Extensions/AuthenticationExtensions.cs`, `API/appsettings.json`.

- [ ] **Step 1: `AuthenticationExtensions.AddJwtAuth(IServiceCollection, IConfiguration)`** — `AddAuthentication(JwtBearerDefaults).AddJwtBearer` with `TokenValidationParameters{ ValidateIssuer=true, ValidIssuer=JWT:Issuer, ValidateAudience=true, ValidAudience=JWT:Issuer, IssuerSigningKey=SymmetricSecurityKey(JWT:SigningKey), ValidateLifetime=true }`; map `role` claim to `RoleClaimType` and `sub`→NameIdentifier (`options.MapInboundClaims=false` + `TokenValidationParameters.NameClaimType=JwtRegisteredClaimNames.Sub`, `RoleClaimType="role"`).
- [ ] **Step 2: `Program.cs`** — `AddControllers()`; `AddApplication()`; `AddInfrastructure(config)`; `AddJwtAuth(config)`; `AddAuthorization()`; Swagger with Bearer; static files (`UseStaticFiles` + `UseDefaultFiles` for `wwwroot/admin`); CORS allow-any-origin for `/api` + `/pointer.js` (the component loads cross-origin); `app.UseAuthentication(); app.UseAuthorization(); app.MapControllers();`. Auto-migrate + seed when `DBMigrationEnabled=true`.

```csharp
// CORS (the web component runs on the host app's origin)
builder.Services.AddCors(o => o.AddDefaultPolicy(p =>
    p.AllowAnyOrigin().AllowAnyHeader().AllowAnyMethod()));
// ... app.UseCors();
```
> Roles in `[Authorize(Roles=...)]` must match the role claim string. Since we emit `role=(int)Role`, authorize with the int string, e.g. `[Authorize(Roles = "1")]` for Admin. **Cleaner:** add `API/Auth/PointerRoles.cs` with `public const string Admin = "1";` and use that. Define it and reference everywhere.

- [ ] **Step 3: Build + commit** — `git commit -m "feat(api): program pipeline, JWT bearer, CORS, swagger, static files"`.

### Task 5.2: AuthController

**Files:** Create `API/Controllers/AuthController.cs`.

- [ ] **Step 1:** `POST /api/auth/login` (`[AllowAnonymous]`) → `IAuthService.LoginAsync` → `Ok`/`BadRequest`. `GET /api/auth/me` (`[Authorize]`) → `Me()`.
- [ ] **Step 2: Build + manual verify deferred to 5.5; commit** — `git commit -m "feat(api): auth controller"`.

### Task 5.3: Admin controllers (Users, Projects)

**Files:** Create `API/Controllers/Admin/UsersController.cs` (`[Authorize(Roles = PointerRoles.Admin)]`, route `api/admin/users`), `API/Controllers/Admin/ProjectsController.cs` (`api/admin/projects`).

- [ ] **Step 1:** Users: `GET` list, `POST` create, `PATCH {id}` update → map `IUserService`. Projects: `GET`/`POST`/`PATCH` → `IProjectService`.
- [ ] **Step 2: Build + commit** — `git commit -m "feat(api): admin users + projects controllers"`.

### Task 5.4: Comments + Replies controllers

**Files:** Create `API/Controllers/CommentsController.cs`, `API/Controllers/RepliesController.cs`.

- [ ] **Step 1:** `[Authorize]` all. Routes:
  - `POST api/projects/{key}/comments` → `CreateAsync(key, body, User.GetId())`.
  - `GET  api/projects/{key}/comments` (`[FromQuery] CommentFilter`) → `ListAsync`.
  - `GET  api/comments/{id}` → `GetByIdAsync`.
  - `PATCH api/comments/{id}` → `UpdateStatusAsync(id, body, User.GetId())`.
  - `POST api/comments/{id}/replies` → `AddReplyAsync`.
  - `DELETE api/comments/{id}` → `DeleteAsync(id, User.GetId(), User.IsInRole(PointerRoles.Admin))`.
- [ ] **Step 2:** Add `API/Extensions/ClaimsPrincipalExtensions.cs` → `Guid GetId(this ClaimsPrincipal)` (parse `sub`/NameIdentifier; throw/Empty guard).
- [ ] **Step 3: Build + commit** — `git commit -m "feat(api): comments + replies controllers"`.

### Task 5.5: Admin seeder + initial migration + end-to-end smoke

**Files:** Create `API/Seed/AdminSeeder.cs`; generate migration.

- [ ] **Step 1: `AdminSeeder.SeedAsync(IServiceProvider)`** — if no `Admin` user exists, create one from `ADMIN__EMAIL`/`ADMIN__PASSWORD` (hash via `IPasswordHasher`, `Role.Admin`, `PublicId=NewGuid`). Call after migrate in `Program.cs`.
- [ ] **Step 2: Generate migration + bring up**

```bash
cp .env.example .env
dotnet ef migrations add InitialCreate -p Infrastructure -s API
docker compose up -d db
just db-update            # or rely on DBMigrationEnabled on API boot
docker compose up -d api
```

- [ ] **Step 3: Smoke test (verification = this task's test cycle)**

```bash
# login as seeded admin
TOKEN=$(curl -s localhost:8090/api/auth/login -H 'Content-Type: application/json' \
  -d '{"email":"admin@pointer.local","password":"ChangeMe123!"}' | jq -r .data.token)
# create a project
curl -s localhost:8090/api/admin/projects -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' -d '{"key":"tuwaiq-clubs","name":"Tuwaiq Clubs"}'
# create a comment
curl -s localhost:8090/api/projects/tuwaiq-clubs/comments -H "Authorization: Bearer $TOKEN" \
  -H 'Content-Type: application/json' \
  -d '{"body":"Make this 24px","environment":2,"element":{"selector":".title","sourcePath":"apps/tuwaiq-clubs/src/x.tsx:14"}}'
# fetch queue
curl -s "localhost:8090/api/projects/tuwaiq-clubs/comments?status=1" -H "Authorization: Bearer $TOKEN"
```
Expected: login returns a token; project + comment created (`isSuccess:true`); list returns the comment with its `element` jsonb.

- [ ] **Step 4: Commit** — `git commit -m "feat(api): admin seeder + initial migration; e2e smoke passes"`.

---

## Phase 6 — Static assets served by API

### Task 6.1: Port pointer.js + add login (served from wwwroot)

**Files:** Create `API/wwwroot/pointer.js` (start from `../Pointer/comments-skill/pointer.js`).

- [ ] **Step 1:** Copy the existing component. Replace the **name/role identity modal** with an **email/password login modal**: on submit `POST {server}/api/auth/login`, store `data.token` in `localStorage['pointer_token']` and `data.user` in `localStorage['pointer_user']`.
- [ ] **Step 2:** Add `Authorization: Bearer ${token}` to every fetch (create comment, list, patch, reply). On HTTP 401 clear token+user and re-open the login modal.
- [ ] **Step 3:** Remove the self-reported role dropdown; show `pointer_user.displayName`/role from the stored account. Comment POST body uses the new `element` shape (`{selector,snapshot,classes,computedStyles,appliedCssRules,sourcePath,parentInfo}`) + `environment` (read from the `environment` attribute, default Staging).
- [ ] **Step 4: Verify** — load `http://localhost:8090/pointer.js` in a `test.html` with `<pointer-feedback project="tuwaiq-clubs" server="http://localhost:8090">`; log in as a seeded user; leave a comment; confirm it appears via the `GET` smoke call. (Use the playwright-cli skill for the browser check.)
- [ ] **Step 5: Commit** — `git commit -m "feat(web): pointer.js login + bearer auth + new element payload"`.

### Task 6.2: Updated skill.md

**Files:** Create `API/wwwroot/skill.md` (adapt `../Pointer/comments-skill/skill.md`).

- [ ] **Step 1:** Document the new auth flow: read `POINTER_SERVER`, `POINTER_PROJECT`, `POINTER_EMAIL`, `POINTER_PASSWORD` from env; `POST /api/auth/login` → cache token; `GET /api/projects/{key}/comments?status=1` (ReadyToApply) for the queue; apply each item by `element.sourcePath` else codebase search; `PATCH /api/comments/{id}` `{status:3,reply,appliedByLabel}` where `appliedByLabel` = `git config user.email`.
- [ ] **Step 2:** Keep the "sacred CSS rule" guidance (edit the winning rule from `element.appliedCssRules`).
- [ ] **Step 3: Commit** — `git commit -m "docs(skill): update apply workflow for auth + new endpoints"`.

---

## Phase 7 — Admin UI (build-free static)

### Task 7.1: Admin login + users + projects pages

**Files:** Create `API/wwwroot/admin/{index.html,app.js,style.css}`.

- [ ] **Step 1:** `index.html` — a single page: login form; once logged in (token in `localStorage['pointer_admin_token']`) show two panels: **Users** (table of email/displayName/role/active + "add user" form with role `<select>` + disable button) and **Projects** (table + add form).
- [ ] **Step 2:** `app.js` — vanilla `fetch` against `/api/auth/login`, `/api/admin/users`, `/api/admin/projects` with the bearer token; render tables; handle add/disable/role-change (`PATCH`).
- [ ] **Step 3: Verify** — open `http://localhost:8090/admin/`, log in as admin, create a `Tester` user and a project, confirm via `GET /api/admin/users`. (playwright-cli skill.)
- [ ] **Step 4: Commit** — `git commit -m "feat(admin-ui): build-free users + projects management"`.

---

## Phase 8 — Clubs cutover + AI apply skill install

### Task 8.1: Point clubs app at the new API

**Files:** Modify `tuwaiq-academy-mono-spa/apps/tuwaiq-clubs/.env` (`VITE_POINTER_SERVER=http://localhost:8090`). `index.html` loader shape unchanged.

- [ ] **Step 1:** Update `.env`; run the clubs dev server; confirm the overlay loads `pointer.js` from the new API and the login modal appears.
- [ ] **Step 2: End-to-end** — provision a clubs user via admin UI → log in through the overlay → leave a comment on a real element → mark "Ready to Apply" → run the AI skill (login with automation account) → fetch → apply → `PATCH` to Applied → verify status in DB (`just psql`).
- [ ] **Step 3: Commit (clubs repo)** — `git commit -m "chore(clubs): point Pointer at .NET backend"` (in the monorepo, only with explicit user permission per repo rules).

### Task 8.2: Install the AI skill in consuming repos

- [ ] **Step 1:** Document/install: `curl -s http://localhost:8090/skill.md -o .claude/skills/pointer-feedback/SKILL.md` and set `POINTER_*` env in the dev's shell/`.env`.
- [ ] **Step 2: Commit** — `git commit -m "docs: pointer skill install instructions"`.

---

## Phase 9 — Docs sync

### Task 9.1: Finalize README + keep DESIGN/TASKS current

- [ ] **Step 1:** Flesh out `README.md` quick-start: `cp .env.example .env`, `just up`, seed admin, open `/admin/`, provision users + project, integrate an app (two-line loader), install skill.
- [ ] **Step 2:** Ensure `TASKS.md` rows reflect reality (✅), `DESIGN.md` matches any deviations (e.g. the `PublicId` decision, role-claim-as-int).
- [ ] **Step 3: Commit** — `git commit -m "docs: finalize README + sync design/tasks"`.

---

## Self-Review notes (resolved)

- **`sub` type mismatch** (int Id vs Guid audit) → resolved by adding `User.PublicId` (Guid) as the JWT subject and the value stored in audit fields. Applied to Tasks 1.2, 3.1, 3.4, 4.2, 5.5.
- **Role claim vs `[Authorize(Roles)]`** → standardized on emitting `role=(int)Role` and a `PointerRoles` constants class; authorize uses those strings. Applied in Task 5.1/5.3.
- **CORS** → required because the component runs on the host app origin; added in 5.1.
- **Env binding** → `JWT__SigningKey` style double-underscore keys map to `JWT:SigningKey`; `.env.example` updated in 3.5.
- **No active-test reference codebase** → focused unit tests on hasher/token/validator only; everything else verified via build + curl/browser smoke (explicit steps in 5.5, 6.1, 7.1, 8.1).
- **Spec coverage:** auth (P3/P4.1/P5.1-2), local accounts+roles+provisioning admin UI (P4.2/5.3/7), multi-project (P4.3), comments+element jsonb (P1.2/3.1/4.4), apply flow + skill (P4.4/6.2/8), web component login (P6.1), clubs cutover (P8.1) — all mapped.
