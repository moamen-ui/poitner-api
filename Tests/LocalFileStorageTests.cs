using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.FileProviders;
using Pointer.Application.Abstractions;
using Pointer.Infrastructure.Storage;
using Xunit;

namespace Pointer.Tests;

/// <summary>
/// DB-16: real-disk tests for <see cref="LocalFileStorage"/> — a fresh temp directory as
/// WebRootPath (via a fake IWebHostEnvironment) and the real UploadSigner (needs JWT:SigningKey).
/// Notably <see cref="DeleteAsync_AcceptsSignedUrl"/> is the B1 regression test: before this doc,
/// DeleteAsync given a signed URL silently deleted nothing.
/// </summary>
public class LocalFileStorageTests : IDisposable
{
    private sealed class FakeWebHostEnvironment : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = null!;
        public string ApplicationName { get; set; } = "Pointer.Tests";
        public IFileProvider ContentRootFileProvider { get; set; } = null!;
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Test";
    }

    private readonly string _tempRoot;
    private readonly LocalFileStorage _storage;

    public LocalFileStorageTests()
    {
        _tempRoot = Path.Combine(Path.GetTempPath(), "pointer-db16-lfs-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempRoot);
        var env = new FakeWebHostEnvironment { WebRootPath = _tempRoot, ContentRootPath = _tempRoot };
        var signer = new UploadSigner(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789",
                    }
                )
                .Build()
        );
        _storage = new LocalFileStorage(env, signer);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempRoot))
                Directory.Delete(_tempRoot, recursive: true);
        }
        catch { /* best-effort cleanup */ }
    }

    private string WriteFile(string ownerSegment, string project, string name, string content = "x")
    {
        var dir = Path.Combine(_tempRoot, "uploads", ownerSegment, project);
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, name);
        File.WriteAllText(path, content);
        return $"uploads/{ownerSegment}/{project}/{name}";
    }

    [Fact]
    public async Task DeleteAsync_AcceptsSignedUrl()
    {
        var owner = Guid.NewGuid().ToString("N");
        var rel = WriteFile(owner, "proj", "a.webp");

        var signer = new UploadSigner(
            new ConfigurationBuilder()
                .AddInMemoryCollection(
                    new Dictionary<string, string?>
                    {
                        ["JWT:SigningKey"] = "test-key-0123456789abcdef0123456789",
                    }
                )
                .Build()
        );
        var signedUrl = signer.SignedUrl(rel);

        Assert.True(File.Exists(Path.Combine(_tempRoot, "uploads", owner, "proj", "a.webp")));

        await _storage.DeleteAsync(signedUrl);

        Assert.False(File.Exists(Path.Combine(_tempRoot, "uploads", owner, "proj", "a.webp")));
    }

    [Fact]
    public async Task DeleteAsync_AcceptsRawRelPath()
    {
        var owner = Guid.NewGuid().ToString("N");
        var rel = WriteFile(owner, "proj", "b.webp");

        await _storage.DeleteAsync(rel);

        Assert.False(File.Exists(Path.Combine(_tempRoot, "uploads", owner, "proj", "b.webp")));
    }

    [Fact]
    public async Task DeleteAsync_RejectsTraversal()
    {
        var appSettingsPath = Path.Combine(_tempRoot, "appsettings.json");
        File.WriteAllText(appSettingsPath, "{}");

        await _storage.DeleteAsync("uploads/../appsettings.json");

        Assert.True(File.Exists(appSettingsPath));
    }

    [Fact]
    public async Task ListOwnerFilesAsync_ReturnsForwardSlashRelPaths_AndSizes()
    {
        var owner = Guid.NewGuid().ToString("N");
        WriteFile(owner, "proj1", "a.webp", "12345");
        WriteFile(owner, "proj2", "b.webp", "1234567890");

        var found = new List<StoredFile>();
        await foreach (var f in _storage.ListOwnerFilesAsync(owner))
            found.Add(f);

        Assert.Equal(2, found.Count);
        Assert.All(found, f => Assert.DoesNotContain('\\', f.RelativePath));
        Assert.Contains(found, f => f.RelativePath == $"uploads/{owner}/proj1/a.webp" && f.Bytes == 5);
        Assert.Contains(found, f => f.RelativePath == $"uploads/{owner}/proj2/b.webp" && f.Bytes == 10);
    }

    [Fact]
    public async Task ListOwnerSegmentsAsync_ReturnsFirstLevelFolders()
    {
        var owner = Guid.NewGuid().ToString("N");
        WriteFile(owner, "proj", "a.webp");
        Directory.CreateDirectory(Path.Combine(_tempRoot, "uploads", "branding"));

        var segments = await _storage.ListOwnerSegmentsAsync();

        Assert.Contains(owner, segments);
        Assert.Contains("branding", segments);
    }

    [Fact]
    public async Task ExistsAsync_SizeAsync_RoundTrip()
    {
        var owner = Guid.NewGuid().ToString("N");
        var rel = WriteFile(owner, "proj", "c.webp", "abcde");

        Assert.True(await _storage.ExistsAsync(rel));
        Assert.Equal(5, await _storage.SizeAsync(rel));

        await _storage.DeleteAsync(rel);

        Assert.False(await _storage.ExistsAsync(rel));
        Assert.Equal(0, await _storage.SizeAsync(rel));
    }

    [Fact]
    public async Task DeleteEmptyProjectFoldersAsync_RemovesEmptyOldFolder_KeepsRecentAndNonEmpty()
    {
        var owner = Guid.NewGuid().ToString("N");
        var emptyOldDir = Path.Combine(_tempRoot, "uploads", owner, "empty-old");
        var emptyRecentDir = Path.Combine(_tempRoot, "uploads", owner, "empty-recent");
        var nonEmptyDir = Path.Combine(_tempRoot, "uploads", owner, "non-empty");
        Directory.CreateDirectory(emptyOldDir);
        Directory.CreateDirectory(emptyRecentDir);
        Directory.CreateDirectory(nonEmptyDir);
        File.WriteAllText(Path.Combine(nonEmptyDir, "a.webp"), "x");

        var old = DateTime.UtcNow.AddDays(-3);
        Directory.SetLastWriteTimeUtc(emptyOldDir, old);
        // emptyRecentDir keeps its just-created (recent) mtime.

        var graceCutoff = DateTime.UtcNow.AddHours(-48);
        var removed = await _storage.DeleteEmptyProjectFoldersAsync(owner, graceCutoff);

        Assert.Equal(1, removed);
        Assert.False(Directory.Exists(emptyOldDir));
        Assert.True(Directory.Exists(emptyRecentDir));
        Assert.True(Directory.Exists(nonEmptyDir));
        // Never the owner folder itself.
        Assert.True(Directory.Exists(Path.Combine(_tempRoot, "uploads", owner)));
    }
}
