using System.Globalization;
using System.Runtime.CompilerServices;
using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class CurseForgeModProvider : IModDependencyProvider
{
    private const string ApiBase = "https://api.curseforge.com/v1/";
    private const int MaxMetadataBytes = 4 * 1024 * 1024;
    private const long MaxModBytes = 512L * 1024L * 1024L;
    private const int MaxRedirects = 5;
    private const int PageSize = 50;
    private const int MaxDiscoveryPages = 20;

    private readonly HttpClient _httpClient;
    private readonly ICurseForgeApiKeyProvider _apiKeyProvider;
    private readonly string _userAgent;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private (int GameId, int ModsClassId)? _minecraftCatalog;

    public CurseForgeModProvider(
        HttpClient httpClient,
        ICurseForgeApiKeyProvider apiKeyProvider,
        string? userAgent = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _apiKeyProvider = apiKeyProvider ?? throw new ArgumentNullException(nameof(apiKeyProvider));
        _userAgent = string.IsNullOrWhiteSpace(userAgent)
            ? "yannickfan67-ai-UN_Nexo/0.0 (github.com/yannickfan67-ai/UN_Nexo)"
            : userAgent.Trim();

        using var probe = new HttpRequestMessage();
        probe.Headers.UserAgent.ParseAdd(_userAgent);
    }

    public string ProviderId => "curseforge";
    public string DisplayName => "CurseForge";
    public bool IsConfigured => _apiKeyProvider.IsConfigured;

    public async Task<IReadOnlyList<ModProviderProject>> SearchAsync(
        string query,
        string minecraftVersion,
        string loader,
        int limit = 20,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(query))
            throw new ArgumentException("A CurseForge search query is required.", nameof(query));
        var gameVersion = RequireValue(minecraftVersion, nameof(minecraftVersion));
        var loaderType = NormalizeLoader(loader);
        if (limit is < 1 or > PageSize)
            throw new ArgumentOutOfRangeException(
                nameof(limit),
                $"CurseForge search limit must be between 1 and {PageSize}.");

        var catalog = await EnsureMinecraftCatalogAsync(cancellationToken);
        var relative =
            "mods/search?gameId=" + catalog.GameId.ToString(CultureInfo.InvariantCulture)
            + "&classId=" + catalog.ModsClassId.ToString(CultureInfo.InvariantCulture)
            + "&gameVersion=" + Uri.EscapeDataString(gameVersion)
            + "&modLoaderType=" + loaderType.ToString(CultureInfo.InvariantCulture)
            + "&searchFilter=" + Uri.EscapeDataString(query.Trim())
            + "&pageSize=" + limit.ToString(CultureInfo.InvariantCulture);

        using var document = await SendApiJsonAsync(
            HttpMethod.Get,
            relative,
            content: null,
            cancellationToken);
        if (!TryGetDataArray(document.RootElement, out var data))
            throw new InvalidDataException("CurseForge search response must contain a data array.");

        var projects = new List<ModProviderProject>();
        foreach (var item in data.EnumerateArray())
        {
            var project = ParseProject(item);
            if (project is not null)
                projects.Add(project);
        }

        return projects;
    }

    public async Task<ModProviderProject?> GetProjectAsync(
        string projectId,
        CancellationToken cancellationToken = default)
    {
        var modId = ParsePositiveInt(projectId, nameof(projectId));
        using var document = await SendApiJsonAsync(
            HttpMethod.Get,
            "mods/" + modId.ToString(CultureInfo.InvariantCulture),
            content: null,
            cancellationToken);
        if (!TryGetDataObject(document.RootElement, out var data))
            return null;

        var project = ParseProject(data);
        return project is not null
               && string.Equals(project.ProjectId, projectId, StringComparison.Ordinal)
            ? project
            : null;
    }

    public async Task<ModProviderVersion?> GetLatestCompatibleVersionAsync(
        string projectId,
        string minecraftVersion,
        string loader,
        CancellationToken cancellationToken = default)
    {
        var modId = ParsePositiveInt(projectId, nameof(projectId));
        var gameVersion = RequireValue(minecraftVersion, nameof(minecraftVersion));
        var loaderType = NormalizeLoader(loader);
        var relative =
            $"mods/{modId.ToString(CultureInfo.InvariantCulture)}/files"
            + "?gameVersion=" + Uri.EscapeDataString(gameVersion)
            + "&modLoaderType=" + loaderType.ToString(CultureInfo.InvariantCulture)
            + "&pageSize=" + PageSize.ToString(CultureInfo.InvariantCulture);

        using var document = await SendApiJsonAsync(
            HttpMethod.Get,
            relative,
            content: null,
            cancellationToken);
        if (!TryGetDataArray(document.RootElement, out var data))
            throw new InvalidDataException(
                "CurseForge mod-files response must contain a data array.");

        ModProviderVersion? latest = null;
        foreach (var item in data.EnumerateArray())
        {
            var candidate = ParseFileVersion(item);
            if (candidate is null
                || !string.Equals(candidate.ProjectId, projectId, StringComparison.Ordinal))
                continue;
            if (latest is null || candidate.PublishedAt > latest.PublishedAt)
                latest = candidate;
        }

        return latest;
    }

    public async Task<ModProviderVersion?> GetCompatibleVersionAsync(
        string? projectId,
        string? versionId,
        string minecraftVersion,
        string loader,
        CancellationToken cancellationToken = default)
    {
        var gameVersion = RequireValue(minecraftVersion, nameof(minecraftVersion));
        var loaderType = NormalizeLoader(loader);

        if (versionId is null)
        {
            if (projectId is null)
                throw new ArgumentException(
                    "A CurseForge project id is required when no file id is supplied.",
                    nameof(projectId));
            return await GetLatestCompatibleVersionAsync(
                projectId,
                gameVersion,
                loader,
                cancellationToken);
        }

        var fileId = ParsePositiveInt(versionId, nameof(versionId));
        ModProviderVersion? candidate;

        if (projectId is not null)
        {
            var modId = ParsePositiveInt(projectId, nameof(projectId));
            using var document = await SendApiJsonAsync(
                HttpMethod.Get,
                $"mods/{modId.ToString(CultureInfo.InvariantCulture)}/files/{fileId.ToString(CultureInfo.InvariantCulture)}",
                content: null,
                cancellationToken);
            if (!TryGetDataObject(document.RootElement, out var data))
                return null;
            candidate = ParseFileVersion(data);
        }
        else
        {
            using var requestContent = new StringContent(
                JsonSerializer.Serialize(new { fileIds = new[] { fileId } }),
                Encoding.UTF8,
                "application/json");
            using var document = await SendApiJsonAsync(
                HttpMethod.Post,
                "mods/files",
                requestContent,
                cancellationToken);
            if (!TryGetDataArray(document.RootElement, out var data))
                return null;
            candidate = data.GetArrayLength() == 1
                ? ParseFileVersion(data[0])
                : null;
        }

        if (candidate is null
            || !string.Equals(candidate.VersionId, versionId, StringComparison.Ordinal)
            || projectId is not null
               && !string.Equals(candidate.ProjectId, projectId, StringComparison.Ordinal))
            return null;

        return FileTagsAreCompatible(
            candidate,
            gameVersion,
            loaderType)
            ? candidate
            : null;
    }

    public async Task<IReadOnlyDictionary<string, ModProviderInstalledMatch>> MatchInstalledAsync(
        string modsDirectory,
        IReadOnlyList<InstalledMod> installedMods,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(installedMods);
        if (installedMods.Count == 0 || !Directory.Exists(modsDirectory))
            return new Dictionary<string, ModProviderInstalledMatch>(StringComparer.Ordinal);

        var byFingerprint = new Dictionary<uint, InstalledMod>();
        foreach (var mod in installedMods)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!InstanceModService.TryResolvePhysicalManagedModPath(
                    modsDirectory,
                    mod.FileName,
                    out var candidate))
                continue;

            await using var stream = new FileStream(
                candidate,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);

            if (!InstanceModService.TryResolvePhysicalManagedModPath(
                    modsDirectory,
                    mod.FileName,
                    out var revalidated)
                || !PathEquals(candidate, revalidated))
                continue;

            var fingerprint = await CurseForgeFingerprint.ComputeAsync(
                stream,
                cancellationToken);
            byFingerprint.TryAdd(fingerprint, mod);
        }

        if (byFingerprint.Count == 0)
            return new Dictionary<string, ModProviderInstalledMatch>(StringComparer.Ordinal);

        var catalog = await EnsureMinecraftCatalogAsync(cancellationToken);
        using var requestContent = new StringContent(
            JsonSerializer.Serialize(new
            {
                fingerprints = byFingerprint.Keys
                    .Select(value => (long)value)
                    .ToArray()
            }),
            Encoding.UTF8,
            "application/json");
        using var document = await SendApiJsonAsync(
            HttpMethod.Post,
            $"fingerprints/{catalog.GameId.ToString(CultureInfo.InvariantCulture)}",
            requestContent,
            cancellationToken);

        if (!TryGetDataObject(document.RootElement, out var data)
            || !data.TryGetProperty("exactMatches", out var matches)
            || matches.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                "CurseForge fingerprint response must contain data.exactMatches.");

        var result = new Dictionary<string, ModProviderInstalledMatch>(StringComparer.Ordinal);
        foreach (var match in matches.EnumerateArray())
        {
            if (match.ValueKind != JsonValueKind.Object
                || !match.TryGetProperty("file", out var file)
                || file.ValueKind != JsonValueKind.Object
                || !TryGetPositiveInt(file, "modId", out var modId)
                || !TryGetPositiveInt(file, "id", out var fileId)
                || !TryGetUInt32(file, "fileFingerprint", out var fingerprint)
                || !byFingerprint.TryGetValue(fingerprint, out var local))
                continue;

            var projectId = modId.ToString(CultureInfo.InvariantCulture);
            var versionNumber = TryGetString(file, "displayName")
                                ?? TryGetString(file, "fileName")
                                ?? fileId.ToString(CultureInfo.InvariantCulture);
            result.TryAdd(
                projectId,
                new ModProviderInstalledMatch(
                    ProviderId,
                    projectId,
                    fileId.ToString(CultureInfo.InvariantCulture),
                    versionNumber,
                    local.FileName,
                    local.IsEnabled));
        }

        return result;
    }

    public async Task<ModProviderStagedInstall> StageAsync(
        ModProviderProject project,
        ModProviderVersion version,
        string stagingDirectory,
        CancellationToken cancellationToken = default)
    {
        ValidateProjectVersion(project, version);
        if (string.IsNullOrWhiteSpace(stagingDirectory))
            throw new ArgumentException(
                "A caller-owned staging directory is required.",
                nameof(stagingDirectory));

        var file = version.SelectPrimaryFile();
        ValidateProviderFile(file);

        var stagingRoot = Path.GetFullPath(stagingDirectory);
        EnsurePhysicalStagingDirectory(stagingRoot);
        var stagedPath = ResolveStagingPath(stagingRoot, file.FileName);
        if (File.Exists(stagedPath) || Directory.Exists(stagedPath))
            throw new IOException(
                $"The caller-owned staging directory already contains '{file.FileName}'.");

        var downloadUrl = await GetDownloadUrlAsync(
            project.ProjectId,
            version.VersionId,
            cancellationToken);
        await DownloadVerifiedAsync(
            downloadUrl,
            file,
            stagedPath,
            cancellationToken);

        EnsurePhysicalStagingDirectory(stagingRoot);
        return new ModProviderStagedInstall(
            project,
            version,
            stagedPath);
    }

    public async Task<ModProviderInstallResult> InstallAsync(
        string instanceId,
        ModProviderProject project,
        ModProviderVersion version,
        ModProviderInstalledMatch? existing,
        InstanceModService modService,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(instanceId))
            throw new ArgumentException("An instance id is required.", nameof(instanceId));
        ArgumentNullException.ThrowIfNull(modService);
        ValidateProjectVersion(project, version);
        if (existing is not null
            && (!string.Equals(existing.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase)
                || !string.Equals(existing.ProjectId, project.ProjectId, StringComparison.Ordinal)))
        {
            throw new InvalidDataException(
                "The installed CurseForge match does not belong to the selected project.");
        }

        using var stagingScope = ProviderStagingScope.Create();
        var entryDirectory = stagingScope.CreateEntryDirectory();
        var staged = await StageAsync(
            project,
            version,
            entryDirectory,
            cancellationToken);
        var stagedPath = stagingScope.ValidateStagedFile(
            entryDirectory,
            staged.StagedPath);

        var installed = await modService.InstallProviderUpdateAsync(
            instanceId,
            stagedPath,
            existing?.LocalFileName,
            existing?.IsEnabled ?? true,
            cancellationToken);
        return new ModProviderInstallResult(project, version, installed);
    }

    private async Task<(int GameId, int ModsClassId)> EnsureMinecraftCatalogAsync(
        CancellationToken cancellationToken)
    {
        if (_minecraftCatalog is { } cached)
            return cached;

        await _catalogGate.WaitAsync(cancellationToken);
        try
        {
            if (_minecraftCatalog is { } initialized)
                return initialized;

            int? gameId = null;
            for (var page = 0; page < MaxDiscoveryPages && gameId is null; page++)
            {
                var index = page * PageSize;
                using var document = await SendApiJsonAsync(
                    HttpMethod.Get,
                    $"games?index={index.ToString(CultureInfo.InvariantCulture)}&pageSize={PageSize.ToString(CultureInfo.InvariantCulture)}",
                    content: null,
                    cancellationToken);
                if (!TryGetDataArray(document.RootElement, out var data))
                    throw new InvalidDataException(
                        "CurseForge games response must contain a data array.");

                foreach (var game in data.EnumerateArray())
                {
                    if (game.ValueKind != JsonValueKind.Object
                        || !TryGetPositiveInt(game, "id", out var candidateId))
                        continue;
                    var slug = TryGetString(game, "slug");
                    var name = TryGetString(game, "name");
                    if (string.Equals(slug, "minecraft", StringComparison.OrdinalIgnoreCase)
                        || string.Equals(name, "Minecraft", StringComparison.OrdinalIgnoreCase))
                    {
                        gameId = candidateId;
                        break;
                    }
                }

                if (gameId is not null || data.GetArrayLength() < PageSize)
                    break;
            }

            if (gameId is null)
                throw new InvalidOperationException(
                    "CurseForge did not expose the Minecraft game to this API key.");

            using var categoriesDocument = await SendApiJsonAsync(
                HttpMethod.Get,
                $"categories?gameId={gameId.Value.ToString(CultureInfo.InvariantCulture)}&classesOnly=true",
                content: null,
                cancellationToken);
            if (!TryGetDataArray(categoriesDocument.RootElement, out var categories))
                throw new InvalidDataException(
                    "CurseForge categories response must contain a data array.");

            int? modsClassId = null;
            foreach (var category in categories.EnumerateArray())
            {
                if (category.ValueKind != JsonValueKind.Object
                    || !TryGetPositiveInt(category, "id", out var id))
                    continue;

                var isClass = !category.TryGetProperty("isClass", out var isClassElement)
                              || isClassElement.ValueKind == JsonValueKind.True;
                if (!isClass)
                    continue;

                var slug = TryGetString(category, "slug");
                var name = TryGetString(category, "name");
                if (string.Equals(slug, "mc-mods", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(slug, "mods", StringComparison.OrdinalIgnoreCase)
                    || string.Equals(name, "Mods", StringComparison.OrdinalIgnoreCase))
                {
                    modsClassId = id;
                    break;
                }
            }

            if (modsClassId is null)
                throw new InvalidOperationException(
                    "CurseForge did not expose the Minecraft Mods class to this API key.");

            _minecraftCatalog = (gameId.Value, modsClassId.Value);
            return _minecraftCatalog.Value;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private async Task<string> GetDownloadUrlAsync(
        string projectId,
        string versionId,
        CancellationToken cancellationToken)
    {
        var modId = ParsePositiveInt(projectId, nameof(projectId));
        var fileId = ParsePositiveInt(versionId, nameof(versionId));
        using var document = await SendApiJsonAsync(
            HttpMethod.Get,
            $"mods/{modId.ToString(CultureInfo.InvariantCulture)}/files/{fileId.ToString(CultureInfo.InvariantCulture)}/download-url",
            content: null,
            cancellationToken);

        if (!document.RootElement.TryGetProperty("data", out var data)
            || data.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(data.GetString()))
        {
            throw new InvalidOperationException(
                "CurseForge did not provide a third-party download URL for this file. The author may have disabled third-party distribution; open the project page to install it manually.");
        }

        var value = data.GetString()!.Trim();
        _ = RequireTrustedCdnUri(value);
        return value;
    }

    private async Task DownloadVerifiedAsync(
        string downloadUrl,
        ModProviderFile file,
        string destinationPath,
        CancellationToken cancellationToken)
    {
        using var response = await SendCdnGetAsync(downloadUrl, cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength is { } declaredLength)
        {
            if (declaredLength > MaxModBytes)
                throw new InvalidDataException(
                    "The CurseForge file is larger than Nexo's download safety limit.");
            if (declaredLength != file.Size)
                throw new InvalidDataException(
                    "The CurseForge download size does not match file metadata.");
        }

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            destinationPath,
            FileMode.CreateNew,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA1);
        var buffer = new byte[128 * 1024];
        long total = 0;

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            total += read;
            if (total > MaxModBytes || total > file.Size)
                throw new InvalidDataException(
                    "The CurseForge download exceeded its declared safe size.");
            hash.AppendData(buffer, 0, read);
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        await output.FlushAsync(cancellationToken);
        if (total != file.Size)
            throw new InvalidDataException(
                "The CurseForge download ended before the declared file size.");

        var actualSha1 = Convert.ToHexString(hash.GetHashAndReset()).ToLowerInvariant();
        if (!string.Equals(actualSha1, file.Sha1, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                "The CurseForge download failed SHA-1 verification.");
    }

    private async Task<HttpResponseMessage> SendCdnGetAsync(
        string downloadUrl,
        CancellationToken cancellationToken)
    {
        var current = RequireTrustedCdnUri(downloadUrl);
        var apiKey = await _apiKeyProvider.GetApiKeyAsync(cancellationToken);

        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            ApplyUserAgent(request);
            request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var effective = response.RequestMessage?.RequestUri;
            if (effective is not null && !UriEquals(effective, current))
            {
                response.Dispose();
                throw new InvalidDataException(
                    "The CurseForge HTTP client followed a redirect automatically. Automatic redirects must be disabled so Nexo can validate every CDN target.");
            }

            if (!IsRedirect(response.StatusCode))
                return response;

            if (redirectCount >= MaxRedirects)
            {
                response.Dispose();
                throw new InvalidDataException(
                    $"The CurseForge download exceeded the {MaxRedirects}-redirect safety limit.");
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                throw new InvalidDataException(
                    "The CurseForge CDN returned a redirect without a Location header.");
            }

            Uri next;
            try
            {
                next = location.IsAbsoluteUri
                    ? location
                    : new Uri(current, location);
            }
            catch (UriFormatException ex)
            {
                response.Dispose();
                throw new InvalidDataException(
                    "The CurseForge CDN returned an invalid redirect target.",
                    ex);
            }

            response.Dispose();
            current = RequireTrustedCdnUri(next.AbsoluteUri);
        }
    }

    private async Task<JsonDocument> SendApiJsonAsync(
        HttpMethod method,
        string relative,
        HttpContent? content,
        CancellationToken cancellationToken)
    {
        var apiKey = await _apiKeyProvider.GetApiKeyAsync(cancellationToken);
        using var request = new HttpRequestMessage(
            method,
            new Uri(new Uri(ApiBase), relative));
        request.Content = content;
        ApplyUserAgent(request);
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/json"));
        request.Headers.TryAddWithoutValidation("x-api-key", apiKey);

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        var expected = request.RequestUri!;
        var effective = response.RequestMessage?.RequestUri;
        if (effective is not null && !UriEquals(effective, expected))
            throw new InvalidDataException(
                "The CurseForge HTTP client followed an API redirect automatically. Automatic redirects must be disabled.");
        if (IsRedirect(response.StatusCode))
            throw new InvalidDataException(
                "CurseForge API redirects are not accepted because credentials must remain on the configured API origin.");

        response.EnsureSuccessStatusCode();
        return await BoundedJsonResponse.ReadAsync(
            response.Content,
            MaxMetadataBytes,
            "CurseForge",
            cancellationToken);
    }

    private void ApplyUserAgent(HttpRequestMessage request)
    {
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(_userAgent);
    }

    private static ModProviderProject? ParseProject(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !TryGetPositiveInt(item, "id", out var id)
            || !TryGetRequiredString(item, "slug", out var slug)
            || !TryGetRequiredString(item, "name", out var name))
            return null;

        var description = TryGetString(item, "summary") ?? string.Empty;
        var downloads = item.TryGetProperty("downloadCount", out var downloadElement)
                        && downloadElement.TryGetInt64(out var parsedDownloads)
            ? Math.Max(0, parsedDownloads)
            : 0;

        var author = "Unknown author";
        if (item.TryGetProperty("authors", out var authors)
            && authors.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in authors.EnumerateArray())
            {
                var value = TryGetString(candidate, "name");
                if (!string.IsNullOrWhiteSpace(value))
                {
                    author = value;
                    break;
                }
            }
        }

        string? iconUrl = null;
        if (item.TryGetProperty("logo", out var logo)
            && logo.ValueKind == JsonValueKind.Object)
        {
            iconUrl = TryGetString(logo, "thumbnailUrl")
                      ?? TryGetString(logo, "url");
        }

        return new ModProviderProject(
            "curseforge",
            id.ToString(CultureInfo.InvariantCulture),
            slug,
            name,
            description,
            author,
            iconUrl,
            downloads,
            "https://www.curseforge.com/minecraft/mc-mods/"
            + Uri.EscapeDataString(slug));
    }

    private static ModProviderVersion? ParseFileVersion(JsonElement item)
    {
        if (item.ValueKind != JsonValueKind.Object
            || !TryGetPositiveInt(item, "id", out var fileId)
            || !TryGetPositiveInt(item, "modId", out var modId)
            || item.TryGetProperty("isAvailable", out var available)
               && available.ValueKind == JsonValueKind.False
            || !TryGetRequiredString(item, "fileName", out var fileName)
            || !IsSafeJarFileName(fileName)
            || !item.TryGetProperty("fileLength", out var lengthElement)
            || !lengthElement.TryGetInt64(out var length)
            || length is <= 0 or > MaxModBytes
            || !TryGetRequiredString(item, "fileDate", out var dateText)
            || !DateTimeOffset.TryParse(dateText, out var published)
            || !TryGetSha1(item, out var sha1))
            return null;

        var projectId = modId.ToString(CultureInfo.InvariantCulture);
        var versionId = fileId.ToString(CultureInfo.InvariantCulture);
        var displayName = TryGetString(item, "displayName") ?? fileName;
        var downloadUrl = TryGetString(item, "downloadUrl") ?? string.Empty;
        var version = new ModProviderVersion(
            "curseforge",
            projectId,
            versionId,
            displayName,
            displayName,
            published,
            [
                new ModProviderFile(
                    fileName,
                    downloadUrl,
                    sha1,
                    length,
                    true)
            ])
        {
            Dependencies = ParseDependencies(item)
        };

        FileCompatibilityTags.Set(
            version,
            ReadStringArray(item, "gameVersions"));
        return version;
    }

    private static IReadOnlyList<ModProviderDependency> ParseDependencies(
        JsonElement item)
    {
        if (!item.TryGetProperty("dependencies", out var dependencies)
            || dependencies.ValueKind == JsonValueKind.Null)
            return [];
        if (dependencies.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException(
                "CurseForge file dependencies must be an array.");

        var result = new List<ModProviderDependency>();
        foreach (var dependency in dependencies.EnumerateArray())
        {
            if (dependency.ValueKind != JsonValueKind.Object
                || !TryGetPositiveInt(dependency, "modId", out var modId)
                || !dependency.TryGetProperty("relationType", out var relation)
                || !relation.TryGetInt32(out var relationType))
                continue;

            var type = relationType switch
            {
                3 => ModProviderDependencyType.Required,
                2 => ModProviderDependencyType.Optional,
                5 => ModProviderDependencyType.Incompatible,
                1 or 6 => ModProviderDependencyType.Embedded,
                _ => (ModProviderDependencyType?)null
            };
            if (type is not null)
            {
                result.Add(new ModProviderDependency(
                    modId.ToString(CultureInfo.InvariantCulture),
                    null,
                    type.Value));
            }
        }

        return result;
    }

    private static bool FileTagsAreCompatible(
        ModProviderVersion version,
        string minecraftVersion,
        int loaderType)
    {
        var tags = FileCompatibilityTags.Get(version);
        if (tags.Count == 0)
            return false;
        if (!tags.Contains(minecraftVersion, StringComparer.Ordinal))
            return false;

        var loaderName = loaderType switch
        {
            1 => "Forge",
            4 => "Fabric",
            5 => "Quilt",
            6 => "NeoForge",
            _ => string.Empty
        };
        return loaderName.Length > 0
               && tags.Contains(loaderName, StringComparer.OrdinalIgnoreCase);
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement item,
        string propertyName)
    {
        if (!item.TryGetProperty(propertyName, out var array)
            || array.ValueKind != JsonValueKind.Array)
            return [];

        return array.EnumerateArray()
            .Where(value => value.ValueKind == JsonValueKind.String)
            .Select(value => value.GetString()?.Trim())
            .Where(value => !string.IsNullOrWhiteSpace(value))
            .Select(value => value!)
            .ToArray();
    }

    private static bool TryGetSha1(
        JsonElement item,
        out string sha1)
    {
        sha1 = string.Empty;
        if (!item.TryGetProperty("hashes", out var hashes)
            || hashes.ValueKind != JsonValueKind.Array)
            return false;

        foreach (var hash in hashes.EnumerateArray())
        {
            if (hash.ValueKind != JsonValueKind.Object
                || !hash.TryGetProperty("algo", out var algorithm)
                || !algorithm.TryGetInt32(out var algorithmId)
                || algorithmId != 1
                || !TryGetRequiredString(hash, "value", out var value)
                || !IsSha1(value))
                continue;

            sha1 = value.ToLowerInvariant();
            return true;
        }

        return false;
    }

    private void ValidateProjectVersion(
        ModProviderProject project,
        ModProviderVersion version)
    {
        ArgumentNullException.ThrowIfNull(project);
        ArgumentNullException.ThrowIfNull(version);
        if (!string.Equals(project.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(version.ProviderId, ProviderId, StringComparison.OrdinalIgnoreCase)
            || !string.Equals(project.ProjectId, version.ProjectId, StringComparison.Ordinal))
            throw new InvalidDataException(
                "The selected CurseForge project and file do not match.");
    }

    private static void ValidateProviderFile(ModProviderFile file)
    {
        if (!IsSafeJarFileName(file.FileName)
            || !IsSha1(file.Sha1)
            || file.Size is <= 0 or > MaxModBytes)
            throw new InvalidDataException(
                "The selected CurseForge file metadata is not safe to install.");
    }

    private static int NormalizeLoader(string loader)
    {
        var value = RequireValue(loader, nameof(loader)).ToLowerInvariant();
        return value switch
        {
            "forge" => 1,
            "fabric" => 4,
            "quilt" => 5,
            "neoforge" => 6,
            _ => throw new NotSupportedException(
                $"CurseForge mod installation is not supported for loader '{loader}'.")
        };
    }

    private static Uri RequireTrustedCdnUri(string value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri)
            || uri.Scheme != Uri.UriSchemeHttps
            || (!uri.IsDefaultPort && uri.Port != 443)
            || !string.IsNullOrEmpty(uri.UserInfo)
            || !(uri.IdnHost.Equals("forgecdn.net", StringComparison.OrdinalIgnoreCase)
                 || uri.IdnHost.EndsWith(".forgecdn.net", StringComparison.OrdinalIgnoreCase)))
        {
            throw new InvalidDataException(
                "CurseForge download URL is not on the trusted HTTPS forgecdn.net origin.");
        }

        return uri;
    }

    private static bool UriEquals(Uri left, Uri right)
        => left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
           && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
           && left.Port == right.Port
           && left.PathAndQuery.Equals(right.PathAndQuery, StringComparison.Ordinal)
           && left.Fragment.Equals(right.Fragment, StringComparison.Ordinal);

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static int ParsePositiveInt(
        string value,
        string parameterName)
    {
        if (!int.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed)
            || parsed <= 0)
            throw new ArgumentException(
                "A positive CurseForge numeric id is required.",
                parameterName);
        return parsed;
    }

    private static bool TryGetPositiveInt(
        JsonElement element,
        string name,
        out int value)
    {
        value = 0;
        return element.TryGetProperty(name, out var property)
               && property.TryGetInt32(out value)
               && value > 0;
    }

    private static bool TryGetUInt32(
        JsonElement element,
        string name,
        out uint value)
    {
        value = 0;
        if (!element.TryGetProperty(name, out var property))
            return false;
        if (property.TryGetUInt32(out value))
            return true;
        if (property.TryGetInt64(out var signed)
            && signed is >= 0 and <= uint.MaxValue)
        {
            value = (uint)signed;
            return true;
        }
        return false;
    }

    private static bool TryGetDataArray(
        JsonElement root,
        out JsonElement data)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out data)
            && data.ValueKind == JsonValueKind.Array)
            return true;

        data = default;
        return false;
    }

    private static bool TryGetDataObject(
        JsonElement root,
        out JsonElement data)
    {
        if (root.ValueKind == JsonValueKind.Object
            && root.TryGetProperty("data", out data)
            && data.ValueKind == JsonValueKind.Object)
            return true;

        data = default;
        return false;
    }

    private static bool TryGetRequiredString(
        JsonElement element,
        string name,
        out string value)
    {
        value = TryGetString(element, name) ?? string.Empty;
        return value.Length > 0;
    }

    private static string? TryGetString(
        JsonElement element,
        string name)
        => element.ValueKind == JsonValueKind.Object
           && element.TryGetProperty(name, out var value)
           && value.ValueKind == JsonValueKind.String
            ? value.GetString()?.Trim()
            : null;

    private static bool IsSafeJarFileName(string fileName)
        => fileName.EndsWith(".jar", StringComparison.OrdinalIgnoreCase)
           && fileName is not "." and not ".."
           && string.Equals(Path.GetFileName(fileName), fileName, StringComparison.Ordinal)
           && fileName.IndexOfAny(Path.GetInvalidFileNameChars()) < 0;

    private static bool IsSha1(string value)
        => value.Length == 40 && value.All(Uri.IsHexDigit);

    private static string RequireValue(
        string value,
        string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw new ArgumentException("A value is required.", parameterName);
        return value.Trim();
    }

    private static bool PathEquals(string left, string right)
        => string.Equals(
            Path.GetFullPath(left),
            Path.GetFullPath(right),
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal);

    private static void EnsurePhysicalStagingDirectory(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                "The caller-owned provider staging directory does not exist.");

        var attributes = File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                "The provider staging directory must be a physical non-reparse directory.");
        }
    }

    private static string ResolveStagingPath(
        string stagingDirectory,
        string fileName)
    {
        var root = Path.GetFullPath(stagingDirectory);
        var candidate = Path.GetFullPath(Path.Combine(root, fileName));
        var rootWithSeparator = Path.EndsInDirectorySeparator(root)
            ? root
            : root + Path.DirectorySeparatorChar;
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!candidate.StartsWith(rootWithSeparator, comparison))
        {
            throw new InvalidDataException(
                "The CurseForge staged file must remain inside the caller-owned staging directory.");
        }

        return candidate;
    }

    // Avoid expanding the provider-neutral version model with provider-specific
    // compatibility tags. The association is lifetime-scoped to parsed version
    // objects and is never persisted.
    private static class FileCompatibilityTags
    {
        private static readonly ConditionalWeakTable<
            ModProviderVersion,
            Tags> Values = new();

        internal static void Set(
            ModProviderVersion version,
            IReadOnlyList<string> tags)
            => Values.Add(version, new Tags(tags));

        internal static IReadOnlyList<string> Get(ModProviderVersion version)
            => Values.TryGetValue(version, out var tags)
                ? tags.Values
                : [];

        private sealed record Tags(IReadOnlyList<string> Values);
    }
}
