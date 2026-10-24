using System.Text.Json;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Downloads;

public sealed record DownloadJob(string Id, string InstanceId, string Name, string VersionId,
    string State, DateTimeOffset UpdatedAt, string? Message = null);
public sealed record CachedCatalog(DateTimeOffset FetchedAt, MinecraftVersionCatalog Catalog);

/// <summary>Small, atomic state files; game data and existing account caches are never exported.</summary>
public sealed class DownloadWorkspace(NexoPathService paths)
{
    private readonly JsonSerializerOptions _json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public const int MaxJobs = 100;

    private string StatePath(string name)
    {
        var root = paths.EnsureDataRootPhysical();
        var path = Path.Combine(root, name);
        if (File.Exists(path) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException("Download state must be a regular managed file.");
        return path;
    }

    public async Task<IReadOnlyList<DownloadJob>> LoadJobsAsync(CancellationToken token = default)
    {
        var path = StatePath("download-jobs.json");
        if (!File.Exists(path)) return [];
        var jobs = await BoundedPersistedJson.DeserializeAsync<List<DownloadJob>>(path, _json, token)
            ?? throw new InvalidDataException("Download history has an invalid root.");
        if (jobs.Count > MaxJobs || jobs.Any(x => x is null || !Guid.TryParseExact(x.Id, "N", out _)
            || !Guid.TryParseExact(x.InstanceId, "N", out _) || string.IsNullOrWhiteSpace(x.VersionId)
            || string.IsNullOrWhiteSpace(x.Name) || x.Name.Length > 256 || x.Message?.Length > 2048
            || x.State is not ("Queued" or "Downloading" or "Paused" or "Failed" or "Complete"))
            || jobs.Select(x => x.Id).Distinct(StringComparer.Ordinal).Count() != jobs.Count)
            throw new InvalidDataException("Download history contains invalid entries; the original was preserved.");
        return jobs.Select(x => x.State is "Queued" or "Downloading"
            ? x with { State = "Paused", Message = "Interrupted session; choose Resume queue." } : x).ToArray();
    }

    public Task SaveJobsAsync(IEnumerable<DownloadJob> jobs, CancellationToken token = default)
        => AtomicJsonFile.WriteAsync(StatePath("download-jobs.json"), jobs.Take(MaxJobs).ToArray(), _json, token);

    public async Task<CachedCatalog?> LoadCatalogAsync(CancellationToken token = default)
    {
        var path = StatePath("download-catalog.json");
        if (!File.Exists(path)) return null;
        var value = await BoundedPersistedJson.DeserializeAsync<CachedCatalog>(path, _json, token);
        if (value?.Catalog?.Versions is null || value.Catalog.Versions.Count > 10000) return null;
        foreach (var version in value.Catalog.Versions)
        {
            MetadataPath.RequireSingleComponent(version.Id, "cached version ID");
            TrustedDownloadPolicy.RequireTrustedUri(version.Url, "cached version URL");
            if (version.Sha1.Length != 40 || !version.Sha1.All(Uri.IsHexDigit))
                throw new InvalidDataException("Cached version digest is invalid.");
        }
        return value;
    }

    public Task SaveCatalogAsync(MinecraftVersionCatalog catalog, CancellationToken token = default)
        => AtomicJsonFile.WriteAsync(StatePath("download-catalog.json"),
            new CachedCatalog(DateTimeOffset.UtcNow, catalog), _json, token);

    public async Task<string> ExportReportAsync(IEnumerable<DownloadJob> jobs, CancellationToken token = default)
    {
        var path = StatePath("download-report.json");
        var report = new
        {
            product = "UN_Nexo", edition = "download-only", createdAt = DateTimeOffset.UtcNow,
            authentication = false, gameplay = false,
            jobs = jobs.Take(MaxJobs).Select(x => new { x.VersionId, x.State, x.UpdatedAt }),
            notice = "Game files, account caches, local paths and credentials are excluded."
        };
        await AtomicJsonFile.WriteAsync(path, report, _json, token);
        return path;
    }
}
