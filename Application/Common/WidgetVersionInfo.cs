using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pointer.Application.Common;

/// <summary>
/// In-memory descriptor of the built and retained widget releases (R3-03).
/// Loaded once at startup from wwwroot/pointer.version.json.
/// </summary>
public sealed class WidgetVersionInfo
{
    private readonly HashSet<string> _retainedHashes;

    public string? CurrentHash { get; }
    public string? Version { get; }
    public IReadOnlySet<string> RetainedHashes => _retainedHashes;

    public WidgetVersionInfo(string? currentHash, string? version, IEnumerable<string> retainedHashes)
    {
        CurrentHash = currentHash;
        Version = version;
        _retainedHashes = new HashSet<string>(retainedHashes, StringComparer.OrdinalIgnoreCase);
    }

    public bool IsRetained(string? hash)
    {
        if (string.IsNullOrWhiteSpace(hash))
            return false;
        return _retainedHashes.Contains(hash);
    }

    public static WidgetVersionInfo Empty { get; } = new(null, null, Array.Empty<string>());

    public static WidgetVersionInfo Load(string contentRootPath, ILogger logger)
    {
        var versionFile = Path.Combine(contentRootPath, "wwwroot", "pointer.version.json");
        if (!File.Exists(versionFile))
        {
            logger.LogWarning("pointer.version.json not found at {Path}. Widget versioning disabled.", versionFile);
            return Empty;
        }

        try
        {
            var json = File.ReadAllText(versionFile);
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            var hash = root.TryGetProperty("hash", out var hProp) ? hProp.GetString() : null;
            var version = root.TryGetProperty("version", out var vProp) ? vProp.GetString() : null;

            var retained = new List<string>();
            if (root.TryGetProperty("retained", out var retainedProp) && retainedProp.ValueKind == JsonValueKind.Array)
            {
                foreach (var item in retainedProp.EnumerateArray())
                {
                    if (item.TryGetProperty("hash", out var itemHashProp))
                    {
                        var itemHash = itemHashProp.GetString();
                        if (!string.IsNullOrWhiteSpace(itemHash))
                        {
                            var dir = Path.Combine(contentRootPath, "wwwroot", "widget", itemHash);
                            if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "pointer.js")))
                            {
                                retained.Add(itemHash);
                            }
                            else
                            {
                                logger.LogWarning("Widget retained directory for {Hash} does not exist at {Dir}. Dropping from retained set.", itemHash, dir);
                            }
                        }
                    }
                }
            }

            // Verify current hash directory
            if (!string.IsNullOrWhiteSpace(hash) && !retained.Contains(hash, StringComparer.OrdinalIgnoreCase))
            {
                var dir = Path.Combine(contentRootPath, "wwwroot", "widget", hash);
                if (Directory.Exists(dir) && File.Exists(Path.Combine(dir, "pointer.js")))
                {
                    retained.Add(hash);
                }
                else
                {
                    logger.LogWarning("Current widget directory for {Hash} does not exist at {Dir}.", hash, dir);
                }
            }

            return new WidgetVersionInfo(hash, version, retained);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Failed to parse pointer.version.json. Widget versioning disabled.");
            return Empty;
        }
    }
}
