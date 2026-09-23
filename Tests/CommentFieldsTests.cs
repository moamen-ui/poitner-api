using Microsoft.EntityFrameworkCore;
using Pointer.Application.Abstractions;
using Pointer.Application.DTOs.Comment;
using Pointer.Application.DTOs.Workspace;
using Pointer.Application.Services.Implementation;
using Pointer.Application.Services.Interfaces;
using Pointer.Domain.Entity;
using Pointer.Domain.Enums;
using Pointer.Domain.ValueObjects;
using Pointer.Infrastructure;
using Pointer.Infrastructure.Mappings;
using Pointer.Infrastructure.Repository;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// R4-01 admin-defined comment fields: definition validation (PUT rules), value validation on
/// create and PATCH /fields, resolution into the read DTOs, capture-config exposure, tenant
/// isolation, and the JsonColumn converter's canonical-serialization comparer. Fixture copied
/// from CommentLanguageTests (in-memory AppDbContext + FakeCurrentUser). The InMemory provider
/// ignores HasFilter on indexes and the converters round-trip in memory only — the same
/// limitation the existing PickedActions tests accept; the converter itself is tested directly
/// as pure functions (JsonColumn_DictionaryComparer_IgnoresKeyOrder).
/// </summary>
public class CommentFieldsTests
{
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
        public long? ImpersonationSessionId { get; set; }
        public bool IsImpersonating => ImpersonationSessionId != null;
    }

    private sealed class FakeFileStorage : IFileStorage
    {
        public Task<string> SaveAsync(
            string ownerSegment,
            string project,
            Stream content,
            string extension
        ) => Task.FromResult("uploads/x");

        public Task DeleteAsync(string relativePathOrUrl) => Task.CompletedTask;

        public Task DeleteOwnerFilesAsync(string ownerSegment) => Task.CompletedTask;
    }

    private sealed class FakeUploadSigner : IUploadSigner
    {
        public string SignedUrl(string relPath) => relPath;

        public bool Validate(string relPath, long exp, string sig) => true;

        public string ExtractRelPath(string stored) => stored;
    }

    private sealed class FakeSettings : ISettingsService
    {
        public Task<bool> GetBoolAsync(string key, bool fallback = false) =>
            Task.FromResult(fallback);

        public Task SetBoolAsync(string key, bool value) => Task.CompletedTask;

        public Task<string> GetStringAsync(string key, string fallback = "") =>
            Task.FromResult(fallback);

        public Task SetStringAsync(string key, string value) => Task.CompletedTask;

        public Task<int> GetIntAsync(string key, int fallback = 0) => Task.FromResult(fallback);

        public Task SetIntAsync(string key, int value) => Task.CompletedTask;
    }

    private static AppDbContext BuildContext(ICurrentUser user, string dbName) =>
        new(
            new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(dbName).Options,
            user,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build()
        );

    private sealed class Harness
    {
        public required string DbName { get; init; }
        public required AppDbContext Db { get; init; }
        public required CommentService CommentService { get; init; }
        public required CommentFieldService FieldService { get; init; }
        public required ProjectService ProjectSvc { get; init; }
        public required Guid TenantId { get; init; }
        public required Guid AuthorId { get; init; }
    }

    private static Harness BuildHarness(string dbName)
    {
        var tenant = Guid.NewGuid();
        var author = Guid.NewGuid();

        using (var seed = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seed.Projects.Add(
                new Project
                {
                    Key = "proj",
                    Name = "Proj",
                    IsActiveLocal = true,
                    IsActiveStaging = true,
                    IsActiveProduction = true,
                    OwnerId = tenant,
                }
            );
            seed.SaveChanges();
        }

        var user = new FakeCurrentUser
        {
            Id = author,
            TenantId = tenant,
            IsSuperAdmin = false,
        };
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        var projectService = new ProjectService(
            uow,
            user,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var actionService = new PredefinedActionService(
            uow,
            projectService,
            user,
            new PassThroughEntitlements()
        );
        var fieldService = new CommentFieldService(uow, user, new FakeAuditWriter());
        var commentService = new CommentService(
            uow,
            projectService,
            actionService,
            new FakeFileStorage(),
            user,
            new FakeUploadSigner(),
            new FakeSettings(),
            new PassThroughEntitlements(),
            commentFields: fieldService
        );

        return new Harness
        {
            DbName = dbName,
            Db = db,
            CommentService = commentService,
            FieldService = fieldService,
            ProjectSvc = projectService,
            TenantId = tenant,
            AuthorId = author,
        };
    }

    // Secondary-actor services over the SAME in-memory database (query filters key off each
    // context's ICurrentUser, which is exactly what the isolation tests exercise).
    private static CommentFieldService FieldServiceFor(ICurrentUser user, string dbName) =>
        new(new UnitOfWork(BuildContext(user, dbName)), user);

    private static CommentService CommentServiceFor(ICurrentUser user, string dbName)
    {
        var db = BuildContext(user, dbName);
        var uow = new UnitOfWork(db);
        var projectService = new ProjectService(
            uow,
            user,
            new PassThroughEntitlements(),
            TestProjectServiceDeps.Settings(),
            TestProjectServiceDeps.Configuration(),
            new FakeAuditWriter()
        );
        var actionService = new PredefinedActionService(
            uow,
            projectService,
            user,
            new PassThroughEntitlements()
        );
        return new CommentService(
            uow,
            projectService,
            actionService,
            new FakeFileStorage(),
            user,
            new FakeUploadSigner(),
            new FakeSettings(),
            new PassThroughEntitlements()
        );
    }

    private static CreateCommentRequest Req(Dictionary<string, string>? fields = null) =>
        new()
        {
            Body = "hello there",
            Environment = EnvironmentTag.Local,
            Element = new ElementCaptureDto(),
            CustomFields = fields,
        };

    private static CommentFieldDefinitionDto Field(
        string key,
        string label,
        CommentFieldType type = CommentFieldType.Text,
        string[]? options = null,
        string[]? hosts = null,
        string? tool = null,
        bool enabled = true,
        int sort = 0
    ) =>
        new()
        {
            Key = key,
            Label = label,
            Type = type,
            Options = options?.ToList() ?? new List<string>(),
            AllowedHosts = hosts?.ToList() ?? new List<string>(),
            SuggestedTool = tool,
            Hint = null,
            Enabled = enabled,
            SortOrder = sort,
        };

    private static CommentFieldDefinition Def(
        string key,
        string label,
        CommentFieldType type = CommentFieldType.Text,
        string[]? options = null,
        string[]? hosts = null,
        string? tool = null,
        bool enabled = true
    ) =>
        new()
        {
            Key = key,
            Label = label,
            Type = type,
            Options = options?.ToList() ?? new List<string>(),
            AllowedHosts = hosts?.ToList() ?? new List<string>(),
            SuggestedTool = tool,
            Enabled = enabled,
        };

    // The canonical Jira example from the doc: jira_url, *.atlassian.net, suggested tool atlassian.
    private static UpdateCommentFieldDefinitionsRequest JiraDefs() =>
        new()
        {
            Fields = new List<CommentFieldDefinitionDto>
            {
                Field(
                    "jira_url",
                    "Jira ticket",
                    CommentFieldType.Url,
                    hosts: new[] { "*.atlassian.net" },
                    tool: "atlassian"
                ),
            },
        };

    [Fact]
    public async Task PutDefinitions_RejectsDuplicateKeys_BadKey_TooMany()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());

        var duplicate = await h.FieldService.UpdateDefinitionsAsync(
            new UpdateCommentFieldDefinitionsRequest
            {
                Fields = new List<CommentFieldDefinitionDto>
                {
                    Field("first_key", "First"),
                    Field("first_key", "Second"),
                },
            }
        );
        Assert.False(duplicate.IsSuccess);
        Assert.Contains("Duplicate comment field key", duplicate.Message);

        var badKey = await h.FieldService.UpdateDefinitionsAsync(
            new UpdateCommentFieldDefinitionsRequest
            {
                Fields = new List<CommentFieldDefinitionDto>
                {
                    Field("BadKey", "Bad"),
                    Field("9starts", "Bad"),
                    Field("x", "Bad"),
                },
            }
        );
        Assert.False(badKey.IsSuccess);
        Assert.Contains("Invalid comment field key", badKey.Message);

        var tooMany = await h.FieldService.UpdateDefinitionsAsync(
            new UpdateCommentFieldDefinitionsRequest
            {
                Fields = Enumerable
                    .Range(0, 11)
                    .Select(i => Field($"field_{i}", $"Field {i}"))
                    .ToList(),
            }
        );
        Assert.False(tooMany.IsSuccess);
        Assert.Contains("at most 10", tooMany.Message);
    }

    [Fact]
    public async Task PutDefinitions_NormalisesSortOrder()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());

        var put = await h.FieldService.UpdateDefinitionsAsync(
            new UpdateCommentFieldDefinitionsRequest
            {
                Fields = new List<CommentFieldDefinitionDto>
                {
                    Field("c_third", "C", sort: 99),
                    Field("a_first", "A", sort: 7),
                    Field("b_second", "B", sort: 2),
                },
            }
        );
        Assert.True(put.IsSuccess);
        // SortOrder re-normalised to 0..n-1 in the GIVEN order — incoming values are ignored.
        Assert.Equal(new[] { 0, 1, 2 }, put.Data!.Fields.Select(f => f.SortOrder).ToArray());
        Assert.Equal(
            new[] { "c_third", "a_first", "b_second" },
            put.Data!.Fields.Select(f => f.Key).ToArray()
        );

        // GET sorts by the normalised SortOrder.
        var get = await h.FieldService.GetDefinitionsAsync();
        Assert.True(get.IsSuccess);
        Assert.Equal(
            new[] { "c_third", "a_first", "b_second" },
            get.Data!.Fields.Select(f => f.Key).ToArray()
        );
    }

    [Fact]
    public async Task Create_StoresValidValues_OmitsEmpty()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        var created = await h.CommentService.CreateAsync(
            "proj",
            Req(
                new Dictionary<string, string>
                {
                    ["jira_url"] = "  https://acme.atlassian.net/browse/APP-42  ",
                    ["jira_url_note"] = "   ",
                }
            ),
            h.AuthorId
        );
        Assert.True(created.IsSuccess);

        // Stored trimmed; a value that trims to empty is removed (unset), never stored.
        var fields = Assert.Single(created.Data!.CustomFields);
        Assert.Equal("jira_url", fields.Key);
        Assert.Equal("https://acme.atlassian.net/browse/APP-42", fields.Value);

        var read = await h.CommentService.GetByIdAsync(created.Data!.Id, h.AuthorId);
        Assert.True(read.IsSuccess);
        Assert.Single(read.Data!.CustomFields);
    }

    [Fact]
    public async Task Create_RejectsUnknownKey()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        var result = await h.CommentService.CreateAsync(
            "proj",
            Req(new Dictionary<string, string> { ["nope"] = "x" }),
            h.AuthorId
        );
        Assert.False(result.IsSuccess);
        Assert.Contains("Unknown comment field 'nope'", result.Message);
    }

    [Fact]
    public async Task Create_RejectsDisabledKey()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True(
            (
                await h.FieldService.UpdateDefinitionsAsync(
                    new UpdateCommentFieldDefinitionsRequest
                    {
                        Fields = new List<CommentFieldDefinitionDto>
                        {
                            Field("jira_url", "Jira ticket", CommentFieldType.Url, enabled: false),
                        },
                    }
                )
            ).IsSuccess
        );

        // Create loads ENABLED definitions only — a disabled key behaves like an unknown one there.
        var result = await h.CommentService.CreateAsync(
            "proj",
            Req(new Dictionary<string, string> { ["jira_url"] = "https://x.example/" }),
            h.AuthorId
        );
        Assert.False(result.IsSuccess);
    }

    [Fact]
    public async Task Create_UrlHostNotAllowed_Rejected()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        var result = await h.CommentService.CreateAsync(
            "proj",
            Req(
                new Dictionary<string, string>
                {
                    ["jira_url"] = "https://evil.example/browse/APP-42",
                }
            ),
            h.AuthorId
        );
        Assert.False(result.IsSuccess);
        // The failure names the LABEL (acceptance criterion 3).
        Assert.Contains("Jira ticket", result.Message);
        Assert.Contains("*.atlassian.net", result.Message);
    }

    [Fact]
    public async Task Create_UrlWildcardMatchesBareDomain()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        // *.atlassian.net also admits the bare domain itself, not just subdomains.
        var result = await h.CommentService.CreateAsync(
            "proj",
            Req(
                new Dictionary<string, string>
                {
                    ["jira_url"] = "https://atlassian.net/browse/APP-1",
                }
            ),
            h.AuthorId
        );
        Assert.True(result.IsSuccess);
    }

    [Fact]
    public async Task Create_SelectOutsideOptions_Rejected()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True(
            (
                await h.FieldService.UpdateDefinitionsAsync(
                    new UpdateCommentFieldDefinitionsRequest
                    {
                        Fields = new List<CommentFieldDefinitionDto>
                        {
                            Field(
                                "severity",
                                "Severity",
                                CommentFieldType.Select,
                                options: new[] { "Low", "High" }
                            ),
                        },
                    }
                )
            ).IsSuccess
        );

        var result = await h.CommentService.CreateAsync(
            "proj",
            Req(new Dictionary<string, string> { ["severity"] = "Medium" }),
            h.AuthorId
        );
        Assert.False(result.IsSuccess);
        Assert.Contains("Severity", result.Message);
    }

    [Fact]
    public async Task Create_TextTooLong_Rejected()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True(
            (
                await h.FieldService.UpdateDefinitionsAsync(
                    new UpdateCommentFieldDefinitionsRequest
                    {
                        Fields = new List<CommentFieldDefinitionDto> { Field("note", "Note") },
                    }
                )
            ).IsSuccess
        );

        var result = await h.CommentService.CreateAsync(
            "proj",
            Req(new Dictionary<string, string> { ["note"] = new string('a', 501) }),
            h.AuthorId
        );
        Assert.False(result.IsSuccess);
        Assert.Contains("Note", result.Message);
    }

    [Fact]
    public async Task Read_ResolvesLabelTypeTool()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        var created = await h.CommentService.CreateAsync(
            "proj",
            Req(
                new Dictionary<string, string>
                {
                    ["jira_url"] = "https://acme.atlassian.net/browse/APP-42",
                }
            ),
            h.AuthorId
        );
        Assert.True(created.IsSuccess);

        var read = await h.CommentService.GetByIdAsync(created.Data!.Id, h.AuthorId);
        Assert.True(read.IsSuccess);
        var field = Assert.Single(read.Data!.CustomFields);
        Assert.Equal("jira_url", field.Key);
        Assert.Equal("Jira ticket", field.Label);
        Assert.Equal(CommentFieldType.Url, field.Type);
        Assert.Equal("https://acme.atlassian.net/browse/APP-42", field.Value);
        Assert.Equal("atlassian", field.SuggestedTool);
    }

    [Fact]
    public async Task Read_OrphanValueFallsBackToKey()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        var created = await h.CommentService.CreateAsync(
            "proj",
            Req(
                new Dictionary<string, string>
                {
                    ["jira_url"] = "https://acme.atlassian.net/browse/APP-42",
                }
            ),
            h.AuthorId
        );
        Assert.True(created.IsSuccess);

        // The workspace deletes every definition: the stored value must still come back.
        Assert.True(
            (
                await h.FieldService.UpdateDefinitionsAsync(
                    new UpdateCommentFieldDefinitionsRequest()
                )
            ).IsSuccess
        );

        var read = await h.CommentService.GetByIdAsync(created.Data!.Id, h.AuthorId);
        Assert.True(read.IsSuccess);
        var orphan = Assert.Single(read.Data!.CustomFields);
        Assert.Equal("jira_url", orphan.Key);
        Assert.Equal("jira_url", orphan.Label); // Label falls back to the key
        Assert.Equal(CommentFieldType.Text, orphan.Type);
        Assert.Null(orphan.SuggestedTool);
        Assert.Equal("https://acme.atlassian.net/browse/APP-42", orphan.Value);
    }

    [Fact]
    public async Task UpdateFields_AuthorAllowed_AdminAllowed_OtherForbidden()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        var created = await h.CommentService.CreateAsync("proj", Req(), h.AuthorId);
        Assert.True(created.IsSuccess);
        var id = created.Data!.Id;

        var patch = new UpdateCommentFieldsRequest
        {
            CustomFields = new Dictionary<string, string>
            {
                ["jira_url"] = "https://acme.atlassian.net/browse/APP-7",
            },
        };

        // Author (plain stakeholder) is allowed.
        var byAuthor = await h.CommentService.UpdateFieldsAsync(id, patch, h.AuthorId);
        Assert.True(byAuthor.IsSuccess);
        var field = Assert.Single(byAuthor.Data!.CustomFields);
        Assert.Equal("https://acme.atlassian.net/browse/APP-7", field.Value);
        Assert.NotNull(byAuthor.Data.EditedAt);

        // Author who is a quick-access user is still allowed (they own the comment).
        var quickAuthor = new FakeCurrentUser
        {
            Id = h.AuthorId,
            TenantId = h.TenantId,
            IsQuickAccess = true,
        };
        var byQuick = await CommentServiceFor(quickAuthor, h.DbName)
            .UpdateFieldsAsync(id, patch, quickAuthor.Id!.Value);
        Assert.True(byQuick.IsSuccess);

        // A workspace admin is allowed.
        var adminId = Guid.NewGuid();
        var admin = new FakeCurrentUser
        {
            Id = adminId,
            TenantId = h.TenantId,
            IsAdmin = true,
        };
        var byAdmin = await CommentServiceFor(admin, h.DbName)
            .UpdateFieldsAsync(id, patch, adminId);
        Assert.True(byAdmin.IsSuccess);

        // Another non-admin in the SAME workspace: 403, deliberately unlike EditAsync's 400.
        var otherId = Guid.NewGuid();
        var other = new FakeCurrentUser { Id = otherId, TenantId = h.TenantId };
        var byOther = await CommentServiceFor(other, h.DbName)
            .UpdateFieldsAsync(id, patch, otherId);
        Assert.False(byOther.IsSuccess);
        Assert.True(byOther.IsForbidden);
    }

    [Fact]
    public async Task CaptureConfig_ListsEnabledOnly()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True(
            (
                await h.FieldService.UpdateDefinitionsAsync(
                    new UpdateCommentFieldDefinitionsRequest
                    {
                        Fields = new List<CommentFieldDefinitionDto>
                        {
                            Field(
                                "jira_url",
                                "Jira ticket",
                                CommentFieldType.Url,
                                hosts: new[] { "*.atlassian.net" },
                                tool: "atlassian"
                            ),
                            Field("hidden", "Hidden", enabled: false),
                        },
                    }
                )
            ).IsSuccess
        );

        var cfg = await h.ProjectSvc.GetCaptureConfigAsync("proj");
        Assert.True(cfg.IsSuccess);
        var exposed = Assert.Single(cfg.Data!.CommentFields); // disabled ones are absent
        Assert.Equal("jira_url", exposed.Key);
        Assert.True(exposed.Enabled);
    }

    [Fact]
    public async Task TenantIsolation_OtherWorkspaceDefinitionsNotApplied()
    {
        var dbName = Guid.NewGuid().ToString();
        var h = BuildHarness(dbName);
        // Workspace A (harness tenant) defines NOTHING; workspace B defines jira_url for *.b.com.
        var tenantB = Guid.NewGuid();
        using (var seedB = BuildContext(new FakeCurrentUser { IsSuperAdmin = true }, dbName))
        {
            seedB.WorkspaceSettings.Add(
                new WorkspaceSetting
                {
                    OwnerId = tenantB,
                    CommentFieldDefinitions = new List<CommentFieldDefinition>
                    {
                        Def(
                            "jira_url",
                            "Jira ticket",
                            CommentFieldType.Url,
                            hosts: new[] { "*.b.com" }
                        ),
                    },
                }
            );
            seedB.SaveChanges();
        }

        // A workspace-A comment must be validated against A's (empty) definitions — B's row for
        // the same key must not silently apply.
        var result = await h.CommentService.CreateAsync(
            "proj",
            Req(
                new Dictionary<string, string>
                {
                    ["jira_url"] = "https://acme.atlassian.net/browse/APP-42",
                }
            ),
            h.AuthorId
        );
        Assert.False(result.IsSuccess);
        Assert.Contains("Unknown comment field 'jira_url'", result.Message);
    }

    [Fact]
    public async Task SuperAdmin_AndQuickAccess_GetPutForbidden()
    {
        var dbName = Guid.NewGuid().ToString();
        var h = BuildHarness(dbName);
        // Seed the workspace's row first: a super admin's GET must be Forbidden — never another
        // tenant's row slipping through the filter pass-through.
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        var superAdmin = new FakeCurrentUser { Id = Guid.NewGuid(), IsSuperAdmin = true };
        var superGet = await FieldServiceFor(superAdmin, dbName).GetDefinitionsAsync();
        Assert.False(superGet.IsSuccess);
        Assert.True(superGet.IsForbidden);
        Assert.Null(superGet.Data);
        var superPut = await FieldServiceFor(superAdmin, dbName).UpdateDefinitionsAsync(JiraDefs());
        Assert.True(superPut.IsForbidden);

        var quick = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            TenantId = h.TenantId,
            IsQuickAccess = true,
        };
        var quickGet = await FieldServiceFor(quick, dbName).GetDefinitionsAsync();
        Assert.True(quickGet.IsForbidden);
        var quickPut = await FieldServiceFor(quick, dbName).UpdateDefinitionsAsync(JiraDefs());
        Assert.True(quickPut.IsForbidden);
    }

    [Fact]
    public async Task UpdateFields_CrossWorkspaceAdmin_NotFound()
    {
        var dbName = Guid.NewGuid().ToString();
        var h = BuildHarness(dbName);
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        var created = await h.CommentService.CreateAsync("proj", Req(), h.AuthorId);
        Assert.True(created.IsSuccess);

        // Workspace B's admin: the strict-own comment filter hides workspace A's comment → 404,
        // not 403 and never a successful write.
        var tenantB = Guid.NewGuid();
        var adminB = new FakeCurrentUser
        {
            Id = Guid.NewGuid(),
            TenantId = tenantB,
            IsAdmin = true,
        };
        var byAdminB = await CommentServiceFor(adminB, dbName)
            .UpdateFieldsAsync(
                created.Data!.Id,
                new UpdateCommentFieldsRequest { CustomFields = new Dictionary<string, string>() },
                adminB.Id!.Value
            );
        Assert.False(byAdminB.IsSuccess);
        Assert.True(byAdminB.IsNotFound);
    }

    [Fact]
    public async Task Read_DisabledDefinitionValueStillResolved()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());
        Assert.True((await h.FieldService.UpdateDefinitionsAsync(JiraDefs())).IsSuccess);

        var created = await h.CommentService.CreateAsync(
            "proj",
            Req(
                new Dictionary<string, string>
                {
                    ["jira_url"] = "https://acme.atlassian.net/browse/APP-42",
                }
            ),
            h.AuthorId
        );
        Assert.True(created.IsSuccess);

        // Disable the definition: the stored value stays visible (with its resolved label).
        Assert.True(
            (
                await h.FieldService.UpdateDefinitionsAsync(
                    new UpdateCommentFieldDefinitionsRequest
                    {
                        Fields = new List<CommentFieldDefinitionDto>
                        {
                            Field(
                                "jira_url",
                                "Jira ticket",
                                CommentFieldType.Url,
                                hosts: new[] { "*.atlassian.net" },
                                tool: "atlassian",
                                enabled: false
                            ),
                        },
                    }
                )
            ).IsSuccess
        );

        var read = await h.CommentService.GetByIdAsync(created.Data!.Id, h.AuthorId);
        Assert.True(read.IsSuccess);
        var field = Assert.Single(read.Data!.CustomFields);
        Assert.Equal("Jira ticket", field.Label);
        Assert.Equal("https://acme.atlassian.net/browse/APP-42", field.Value);
    }

    [Fact]
    public async Task PutDefinitions_RejectsOptionsOnNonSelect_HostsOnNonUrl()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());

        var optionsOnNonSelect = await h.FieldService.UpdateDefinitionsAsync(
            new UpdateCommentFieldDefinitionsRequest
            {
                Fields = new List<CommentFieldDefinitionDto>
                {
                    Field("link", "Link", CommentFieldType.Url, options: new[] { "Nope" }),
                },
            }
        );
        Assert.False(optionsOnNonSelect.IsSuccess);
        Assert.Contains("only allowed on select fields", optionsOnNonSelect.Message);

        var hostsOnNonUrl = await h.FieldService.UpdateDefinitionsAsync(
            new UpdateCommentFieldDefinitionsRequest
            {
                Fields = new List<CommentFieldDefinitionDto>
                {
                    Field("note", "Note", CommentFieldType.Text, hosts: new[] { "example.com" }),
                },
            }
        );
        Assert.False(hostsOnNonUrl.IsSuccess);
        Assert.Contains("only allowed on link fields", hostsOnNonUrl.Message);
    }

    [Fact]
    public async Task PutDefinitions_RejectsMultilineLabel()
    {
        var h = BuildHarness(Guid.NewGuid().ToString());

        var result = await h.FieldService.UpdateDefinitionsAsync(
            new UpdateCommentFieldDefinitionsRequest
            {
                Fields = new List<CommentFieldDefinitionDto>
                {
                    Field("note", "Line one\nLine two"),
                },
            }
        );
        Assert.False(result.IsSuccess);
        Assert.Contains("Invalid label", result.Message);
    }

    [Fact]
    public void Url_RejectsUserInfo()
    {
        // Pure validation — no database needed.
        var defs = new List<CommentFieldDefinition> { Def("link", "Link", CommentFieldType.Url) };
        var svc = new CommentFieldService(
            new UnitOfWork(BuildContext(new FakeCurrentUser(), Guid.NewGuid().ToString())),
            new FakeCurrentUser(),
            new FakeAuditWriter()
        );

        var ok = svc.ValidateValues(
            defs,
            new Dictionary<string, string> { ["link"] = "https://atlassian.net/browse/X" }
        );
        Assert.True(ok.IsSuccess);

        var withUserInfo = svc.ValidateValues(
            defs,
            new Dictionary<string, string> { ["link"] = "https://user@atlassian.net/browse/X" }
        );
        Assert.False(withUserInfo.IsSuccess);
        Assert.Contains("must be a valid http(s) link", withUserInfo.Message);

        var ftp = svc.ValidateValues(
            defs,
            new Dictionary<string, string> { ["link"] = "ftp://atlassian.net/x" }
        );
        Assert.False(ftp.IsSuccess);

        var relative = svc.ValidateValues(
            defs,
            new Dictionary<string, string> { ["link"] = "/browse/X" }
        );
        Assert.False(relative.IsSuccess);
    }

    [Fact]
    public void JsonColumn_DictionaryComparer_IgnoresKeyOrder()
    {
        var a = new Dictionary<string, string> { ["alpha"] = "1", ["beta"] = "2" };
        var b = new Dictionary<string, string> { ["beta"] = "2", ["alpha"] = "1" };

        // Canonical serialization: keys sorted, so insertion order cannot leak into the store.
        Assert.Equal(JsonColumn.Serialize(a), JsonColumn.Serialize(b));
        Assert.Equal(JsonColumn.Serialize(a), "{\"alpha\":\"1\",\"beta\":\"2\"}");
        var roundTrip = JsonColumn.Parse<Dictionary<string, string>>(JsonColumn.Serialize(a));
        Assert.Equal(a, roundTrip);

        // Tolerant parse: garbage/wrong kind → empty instance, never a throw.
        Assert.Empty(JsonColumn.Parse<Dictionary<string, string>>("[]"));
        Assert.Empty(JsonColumn.Parse<Dictionary<string, string>>(null));
        Assert.Empty(JsonColumn.Parse<Dictionary<string, string>>("not json"));

        var comparer = JsonColumn.BuildComparer<Dictionary<string, string>>();
        Assert.True(comparer.Equals(a, b));
        Assert.Equal(comparer.GetHashCode(a), comparer.GetHashCode(b));
        Assert.True(comparer.Equals(comparer.Snapshot(a), b));

        // EF marks the entity UNCHANGED when the property is swapped for a reordered dictionary
        // with the same pairs, and MODIFIED for a real change.
        var h = BuildHarness(Guid.NewGuid().ToString());
        var comment = new Comment
        {
            ProjectId = 1,
            Environment = EnvironmentTag.Local,
            Status = CommentStatus.Open,
            AuthorId = h.AuthorId,
            Body = "x",
            OwnerId = h.TenantId,
            CustomFields = a,
        };
        h.Db.Comments.Add(comment);
        h.Db.SaveChanges();
        Assert.Equal(EntityState.Unchanged, h.Db.Entry(comment).State);

        comment.CustomFields = b; // same pairs, different insertion order
        h.Db.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Unchanged, h.Db.Entry(comment).State);

        comment.CustomFields = new Dictionary<string, string> { ["alpha"] = "changed" };
        h.Db.ChangeTracker.DetectChanges();
        Assert.Equal(EntityState.Modified, h.Db.Entry(comment).State);
    }
}
