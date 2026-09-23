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
        var fileName = $"{Guid.NewGuid():N}.webp";
        var rel = WriteFile(owner, "proj", fileName);

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

        Assert.True(File.Exists(Path.Combine(_tempRoot, "uploads", owner, "proj", fileName)));

        await _storage.DeleteAsync(signedUrl);

        Assert.False(File.Exists(Path.Combine(_tempRoot, "uploads", owner, "proj", fileName)));
    }

    [Fact]
    public async Task DeleteAsync_AcceptsRawRelPath()
    {
        var owner = Guid.NewGuid().ToString("N");
        var fileName = $"{Guid.NewGuid():N}.webp";
        var rel = WriteFile(owner, "proj", fileName);

        await _storage.DeleteAsync(rel);

        Assert.False(File.Exists(Path.Combine(_tempRoot, "uploads", owner, "proj", fileName)));
    }

    [Fact]
    public async Task DeleteAsync_RejectsTraversal()
    {
        var appSettingsPath = Path.Combine(_tempRoot, "appsettings.json");
        File.WriteAllText(appSettingsPath, "{}");

        await _storage.DeleteAsync("uploads/../appsettings.json");

        Assert.True(File.Exists(appSettingsPath));
    }

    // ── DB-16 review fix #1 (BLOCKER): ownership-check-bypass vectors, at the storage layer ──
    // A crafted path can make a naive `StartsWith("uploads/{owner}/")` ownership check pass while
    // still resolving (via Path.GetFullPath) to a file outside that owner's folder. Each vector here
    // targets ownerA's own folder in its literal prefix but tries to escape into ownerB's folder (or
    // branding/) once resolved. All five must leave the victim file untouched.

    [Fact]
    public async Task DeleteAsync_RejectsDotDot_AcrossOwners()
    {
        var ownerA = Guid.NewGuid().ToString("N");
        var ownerB = Guid.NewGuid().ToString("N");
        var victimFile = $"{Guid.NewGuid():N}.png";
        var victim = WriteFile(ownerB, "proj", victimFile);

        var crafted = $"uploads/{ownerA}/../{ownerB}/proj/{victimFile}";
        Assert.StartsWith($"uploads/{ownerA}/", crafted, StringComparison.Ordinal);

        await _storage.DeleteAsync(crafted);

        Assert.True(File.Exists(Path.Combine(_tempRoot, "uploads", ownerB, "proj", victimFile)));
        Assert.Equal(victim, $"uploads/{ownerB}/proj/{victimFile}");
    }

    [Fact]
    public async Task DeleteAsync_RejectsEncodedDotDot_AcrossOwners()
    {
        var ownerA = Guid.NewGuid().ToString("N");
        var ownerB = Guid.NewGuid().ToString("N");
        var victimFile = $"{Guid.NewGuid():N}.png";
        WriteFile(ownerB, "proj", victimFile);

        var innerRel = $"uploads/{ownerA}/../{ownerB}/proj/{victimFile}";
        var crafted = "/api/uploads/file?p=" + Uri.EscapeDataString(innerRel) + "&exp=99999999999&sig=x";

        await _storage.DeleteAsync(crafted);

        Assert.True(File.Exists(Path.Combine(_tempRoot, "uploads", ownerB, "proj", victimFile)));
    }

    [Fact]
    public async Task DeleteAsync_RejectsBackslash_AcrossOwners()
    {
        var ownerA = Guid.NewGuid().ToString("N");
        var ownerB = Guid.NewGuid().ToString("N");
        var victimFile = $"{Guid.NewGuid():N}.png";
        WriteFile(ownerB, "proj", victimFile);

        var crafted = $"uploads/{ownerA}/..\\{ownerB}\\proj\\{victimFile}";
        Assert.StartsWith($"uploads/{ownerA}/", crafted, StringComparison.Ordinal);

        await _storage.DeleteAsync(crafted);

        Assert.True(File.Exists(Path.Combine(_tempRoot, "uploads", ownerB, "proj", victimFile)));
    }

    [Fact]
    public async Task DeleteAsync_RejectsNestedSignedUrl_DoubleDecode()
    {
        var ownerB = Guid.NewGuid().ToString("N");
        var victimFile = $"{Guid.NewGuid():N}.png";
        WriteFile(ownerB, "proj", victimFile);

        var innerRel = $"uploads/{ownerB}/proj/{victimFile}";
        var nestedSignedUrl = "/api/uploads/file?p=" + Uri.EscapeDataString(innerRel) + "&exp=99999999999&sig=x";
        var crafted =
            "/api/uploads/file?p=" + Uri.EscapeDataString(nestedSignedUrl) + "&exp=99999999999&sig=y";

        await _storage.DeleteAsync(crafted);

        Assert.True(File.Exists(Path.Combine(_tempRoot, "uploads", ownerB, "proj", victimFile)));
    }

    [Fact]
    public async Task DeleteAsync_RejectsTraversalIntoBranding()
    {
        var ownerA = Guid.NewGuid().ToString("N");
        var brandingDir = Path.Combine(_tempRoot, "uploads", "branding");
        Directory.CreateDirectory(brandingDir);
        var logoPath = Path.Combine(brandingDir, "logo.png");
        File.WriteAllText(logoPath, "logo-bytes");

        var crafted = $"uploads/{ownerA}/../branding/logo.png";
        Assert.StartsWith($"uploads/{ownerA}/", crafted, StringComparison.Ordinal);

        await _storage.DeleteAsync(crafted);

        Assert.True(File.Exists(logoPath));
    }

    /// <summary>
    /// DB-16 re-review (LOW): a symlinked PROJECT folder must never be traversed into — a link
    /// planted anywhere under uploads/ (by anything with write access to the volume) could otherwise
    /// point a canonical-looking path at a target entirely outside uploads/. Creating a symlink can
    /// fail without elevated privileges on some CI/sandbox configurations (notably Windows without
    /// Developer Mode); this test soft-skips (passes without asserting) when that happens, per the
    /// task's instruction, rather than failing the whole suite on an environment limitation.
    /// </summary>
    [Fact]
    public async Task TryResolve_RefusesSymlinkedProjectFolder()
    {
        var owner = Guid.NewGuid().ToString("N");
        var realProjectDir = Path.Combine(_tempRoot, "uploads", owner, "real-proj");
        Directory.CreateDirectory(realProjectDir);
        var fileName = $"{Guid.NewGuid():N}.png";
        File.WriteAllText(Path.Combine(realProjectDir, fileName), "victim-bytes");

        var linkedProjectDir = Path.Combine(_tempRoot, "uploads", owner, "linked-proj");
        try
        {
            Directory.CreateSymbolicLink(linkedProjectDir, realProjectDir);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or PlatformNotSupportedException)
        {
            // Symlink creation is not available in this environment — nothing to verify here.
            return;
        }

        var relThroughLink = $"uploads/{owner}/linked-proj/{fileName}";

        // Neither ExistsAsync nor DeleteAsync may resolve through the symlinked project folder.
        Assert.Null(await _storage.ExistsAsync(relThroughLink));

        await _storage.DeleteAsync(relThroughLink);

        Assert.True(
            File.Exists(Path.Combine(realProjectDir, fileName)),
            "a file reached only through a symlinked project folder was deleted"
        );
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
        var rel = WriteFile(owner, "proj", $"{Guid.NewGuid():N}.webp", "abcde");

        Assert.Equal(true, await _storage.ExistsAsync(rel));
        Assert.Equal(5, await _storage.SizeAsync(rel));

        await _storage.DeleteAsync(rel);

        // DB-16 review fix #2: a resolved-but-missing file is a confirmed `false`, never null.
        Assert.Equal(false, await _storage.ExistsAsync(rel));
        Assert.Equal(0, await _storage.SizeAsync(rel));
    }

    [Fact]
    public async Task ExistsAsync_NonCanonicalPath_ReturnsNull()
    {
        // DB-16 review fix #2: an unresolvable/non-canonical path is neither confirmed present nor
        // absent — null, not false — so a caller never mistakes "couldn't check" for "gone".
        Assert.Null(await _storage.ExistsAsync("uploads/../appsettings.json"));
        Assert.Null(await _storage.ExistsAsync("not-even-an-uploads-path"));
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

    /// <summary>
    /// DB-16 review fix #8 (LOW): the orphan sweep's DeleteEmptyProjectFoldersAsync can race
    /// SaveAsync — it may see the destination project folder empty and remove it in the window
    /// between SaveAsync's own Directory.CreateDirectory and its FileStream open, since nothing has
    /// been written into it yet. Drives genuine concurrent contention (a background task
    /// aggressively deleting the empty project folder while the foreground repeatedly calls
    /// SaveAsync into it) — without the retry-once-on-DirectoryNotFoundException fix, this
    /// reliably throws within a few hundred iterations; with it, every call succeeds.
    /// </summary>
    [Fact]
    public async Task SaveAsync_SurvivesConcurrentEmptyFolderDeletion()
    {
        var owner = Guid.NewGuid().ToString("N");
        const string project = "proj";
        var projectDir = Path.Combine(_tempRoot, "uploads", owner, project);
        Directory.CreateDirectory(projectDir);

        using var cts = new CancellationTokenSource();
        var deleterTask = Task.Run(() =>
        {
            while (!cts.IsCancellationRequested)
            {
                try
                {
                    // Only remove it when empty — mirrors DeleteEmptyProjectFoldersAsync's own
                    // EnumerateFileSystemEntries().Any() guard; a real SaveAsync in flight (file
                    // partially written) must not be clobbered, only the empty-folder race matters.
                    if (Directory.Exists(projectDir) && !Directory.EnumerateFileSystemEntries(projectDir).Any())
                        Directory.Delete(projectDir);
                }
                catch { /* lost the race against a concurrent CreateDirectory/write — fine, retry */ }
            }
        });

        try
        {
            for (var i = 0; i < 300; i++)
            {
                using var content = new MemoryStream(new byte[] { 1, 2, 3, 4 });
                var rel = await _storage.SaveAsync(owner, project, content, ".png");
                Assert.True(
                    File.Exists(Path.Combine(_tempRoot, rel.Replace('/', Path.DirectorySeparatorChar))),
                    $"iteration {i}: SaveAsync returned a path that does not exist on disk"
                );
            }
        }
        finally
        {
            cts.Cancel();
            await deleterTask;
        }
    }
}
