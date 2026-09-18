using System.Diagnostics;
using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed class JavaRuntimeProvisionService
{
    private const int MaxAdoptiumMetadataBytes = 1024 * 1024;
    private static readonly HttpClient SharedClient = CreateSharedClient();
    private readonly HttpClient _httpClient;
    private readonly NexoPathService _paths;
    private readonly TimeSpan _transferIdleTimeout;
    private readonly Func<string, int, CancellationToken, Task<bool>> _runtimeValidator;
    private readonly HashSet<string> _trustedThisSession = new(StringComparer.OrdinalIgnoreCase);

    public JavaRuntimeProvisionService(NexoPathService paths)
        : this(SharedClient, paths)
    {
    }

    public JavaRuntimeProvisionService(
        HttpClient httpClient,
        NexoPathService paths,
        TimeSpan? transferIdleTimeout = null,
        Func<string, int, CancellationToken, Task<bool>>? runtimeValidator = null)
    {
        _httpClient = httpClient;
        _paths = paths;
        _transferIdleTimeout = transferIdleTimeout ?? TimeSpan.FromSeconds(30);
        if (_transferIdleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(transferIdleTimeout), "Transfer idle timeout must be positive.");
        _runtimeValidator = runtimeValidator ?? ValidateRuntimeAsync;
    }

    public async Task<JavaInstallation> EnsureJavaAsync(
        int major,
        IProgress<string>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (major < 8)
            throw new PlatformNotSupportedException($"Automatic Java acquisition does not support Java {major}.");
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Automatic Java acquisition currently supports x64 only.");

        var os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsLinux() ? "linux"
            : throw new PlatformNotSupportedException(
                "Automatic Java acquisition currently supports Windows and Linux only.");

        _paths.EnsureDirectories();
        var runtimesRoot = _paths.GetRuntimesRoot();
        Directory.CreateDirectory(runtimesRoot);
        var targetRoot = Path.Combine(runtimesRoot, $"temurin-{major}-{os}-x64");

        var existing = await TryLoadExistingAsync(targetRoot, major, cancellationToken);
        if (existing is not null)
            return existing;

        progress?.Report($"Finding Java {major} runtime…");
        var asset = await ResolveAssetAsync(major, os, cancellationToken);
        var downloadRoot = Path.Combine(runtimesRoot, ".downloads");
        Directory.CreateDirectory(downloadRoot);
        var archiveExtension = asset.Link.EndsWith(".zip", StringComparison.OrdinalIgnoreCase)
            ? ".zip"
            : asset.Link.EndsWith(".tar.gz", StringComparison.OrdinalIgnoreCase)
                ? ".tar.gz"
                : throw new InvalidDataException("Java runtime package has an unsupported archive type.");
        var archivePath = Path.Combine(downloadRoot, $"temurin-{major}-{os}-x64{archiveExtension}");
        var partPath = archivePath + ".part";

        try
        {
            await DownloadAsync(asset.Link, partPath, major, progress, cancellationToken);
            await VerifySha256Async(partPath, asset.Sha256, cancellationToken);
            File.Move(partPath, archivePath, overwrite: true);

            progress?.Report($"Installing Java {major} runtime…");
            var stagingRoot = Path.Combine(runtimesRoot, $".staging-{major}-{Guid.NewGuid():N}");
            Directory.CreateDirectory(stagingRoot);
            try
            {
                ExtractArchive(archivePath, stagingRoot);
                var javaPath = FindJavaExecutable(stagingRoot)
                    ?? throw new InvalidDataException("Downloaded Java runtime does not contain bin/java.");
                var binDirectory = Path.GetDirectoryName(javaPath)
                    ?? throw new InvalidDataException("Downloaded Java runtime has an invalid bin directory.");
                var runtimeHome = Directory.GetParent(binDirectory)?.FullName
                    ?? throw new InvalidDataException("Downloaded Java runtime has an invalid home directory.");
                var relativeJavaPath = Path.GetRelativePath(runtimeHome, javaPath);

                if (Directory.Exists(targetRoot))
                    Directory.Delete(targetRoot, recursive: true);
                Directory.Move(runtimeHome, targetRoot);

                var finalJavaPath = Path.Combine(targetRoot, relativeJavaPath);
                EnsureUnixExecutable(finalJavaPath);
                var spawnHelper = Path.Combine(targetRoot, "lib", "jspawnhelper");
                if (File.Exists(spawnHelper))
                    EnsureUnixExecutable(spawnHelper);

                var manifest = new ManagedRuntimeManifest(
                    major,
                    asset.Version,
                    "Eclipse Temurin",
                    asset.Link,
                    asset.Sha256,
                    DateTimeOffset.UtcNow);
                await File.WriteAllTextAsync(
                    Path.Combine(targetRoot, "nexo-runtime.json"),
                    JsonSerializer.Serialize(manifest, JsonOptions),
                    cancellationToken);

                _trustedThisSession.Add(targetRoot);
                progress?.Report($"Java {major} runtime ready.");
                return new JavaInstallation(
                    finalJavaPath,
                    targetRoot,
                    asset.Version,
                    true,
                    "Nexo managed · Eclipse Temurin");
            }
            finally
            {
                if (Directory.Exists(stagingRoot))
                    Directory.Delete(stagingRoot, recursive: true);
            }
        }
        finally
        {
            if (File.Exists(partPath))
                File.Delete(partPath);
            if (File.Exists(archivePath))
                File.Delete(archivePath);
        }
    }

    private async Task<JavaInstallation?> TryLoadExistingAsync(
        string targetRoot,
        int expectedMajor,
        CancellationToken cancellationToken)
    {
        if (!Directory.Exists(targetRoot))
            return null;

        var javaPath = FindJavaExecutable(targetRoot);
        if (javaPath is null)
            return null;

        var manifestPath = Path.Combine(targetRoot, "nexo-runtime.json");
        if (!File.Exists(manifestPath))
            return null;

        ManagedRuntimeManifest? manifest;
        try
        {
            await using var stream = File.OpenRead(manifestPath);
            manifest = await JsonSerializer.DeserializeAsync<ManagedRuntimeManifest>(
                stream,
                JsonOptions,
                cancellationToken);
        }
        catch (JsonException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }

        if (manifest is null
            || manifest.Major != expectedMajor
            || string.IsNullOrWhiteSpace(manifest.Version))
            return null;

        EnsureUnixExecutable(javaPath);
        if (!_trustedThisSession.Contains(targetRoot)
            && !await _runtimeValidator(javaPath, expectedMajor, cancellationToken))
            return null;

        _trustedThisSession.Add(targetRoot);
        return new JavaInstallation(
            javaPath,
            targetRoot,
            manifest.Version,
            true,
            "Nexo managed · Eclipse Temurin");
    }

    private async Task<RuntimeAsset> ResolveAssetAsync(
        int major,
        string os,
        CancellationToken cancellationToken)
    {
        var url = $"https://api.adoptium.net/v3/assets/feature_releases/{major}/ga" +
                  $"?architecture=x64&heap_size=normal&image_type=jre&jvm_impl=hotspot&os={os}" +
                  "&page=0&page_size=1&project=jdk&sort_method=DEFAULT&sort_order=DESC&vendor=eclipse";
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaxAdoptiumMetadataBytes)
            throw new InvalidDataException(
                $"Adoptium metadata response exceeds the {MaxAdoptiumMetadataBytes}-byte limit.");

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffered = new MemoryStream();
        await CopyWithIdleTimeoutAsync(
            stream,
            buffered,
            "api.adoptium.net",
            cancellationToken,
            MaxAdoptiumMetadataBytes);
        buffered.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffered, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException($"No supported Java {major} runtime was returned by Adoptium.");

        var release = document.RootElement[0];
        if (release.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Adoptium release metadata must be an object.");

        JsonElement binary = default;
        if (release.TryGetProperty("binary", out var legacyBinary)
            && legacyBinary.ValueKind == JsonValueKind.Object)
        {
            binary = legacyBinary;
        }
        else if (release.TryGetProperty("binaries", out var binaries)
                 && binaries.ValueKind == JsonValueKind.Array)
        {
            foreach (var candidate in binaries.EnumerateArray())
            {
                if (candidate.ValueKind == JsonValueKind.Object
                    && candidate.TryGetProperty("package", out _))
                {
                    binary = candidate;
                    break;
                }
            }
        }

        if (binary.ValueKind != JsonValueKind.Object
            || !binary.TryGetProperty("package", out var package)
            || package.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("Adoptium returned no downloadable Java package.");

        var link = RequireMetadataString(package, "link", "package.link");
        var checksum = RequireMetadataString(package, "checksum", "package.checksum");

        string? version = null;
        if (release.TryGetProperty("version_data", out var versionData))
        {
            if (versionData.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException(
                    "Adoptium metadata property 'version_data' must be an object.");
            if (versionData.TryGetProperty("semver", out var semver))
            {
                if (semver.ValueKind != JsonValueKind.String)
                    throw new InvalidDataException(
                        "Adoptium metadata property 'version_data.semver' must be a string.");
                version = semver.GetString();
            }
        }

        if (!Uri.TryCreate(link, UriKind.Absolute, out var packageUri)
            || packageUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Adoptium returned an invalid Java package URL.");
        if (checksum.Length != 64
            || checksum.Any(character => !Uri.IsHexDigit(character)))
            throw new InvalidDataException("Adoptium returned an invalid Java package checksum.");
        if (string.IsNullOrWhiteSpace(version))
            version = $"{major}.0.0";

        return new RuntimeAsset(link, checksum.ToLowerInvariant(), version);
    }

    private async Task DownloadAsync(
        string url,
        string path,
        int major,
        IProgress<string>? progress,
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(url, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var length = response.Content.Headers.ContentLength;
        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var output = new FileStream(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None,
            128 * 1024,
            useAsync: true);
        var buffer = new byte[128 * 1024];
        long total = 0;
        var lastPercent = -1;
        while (true)
        {
            var read = await ReadWithIdleTimeoutAsync(input, buffer, new Uri(url).Host, cancellationToken);
            if (read == 0)
                break;
            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
            total += read;
            if (length is > 0)
            {
                var percent = (int)Math.Clamp(total * 100 / length.Value, 0, 100);
                if (percent != lastPercent && (percent == 100 || percent >= lastPercent + 5))
                {
                    lastPercent = percent;
                    progress?.Report($"Downloading Java {major} · {percent}%");
                }
            }
        }
    }

    private async Task CopyWithIdleTimeoutAsync(
        Stream input,
        Stream output,
        string source,
        CancellationToken cancellationToken,
        long? maxBytes = null)
    {
        var buffer = new byte[64 * 1024];
        long total = 0;
        while (true)
        {
            var read = await ReadWithIdleTimeoutAsync(input, buffer, source, cancellationToken);
            if (read == 0)
                return;

            total += read;
            if (maxBytes is not null && total > maxBytes.Value)
                throw new InvalidDataException(
                    $"Transfer from {source} exceeded the {maxBytes.Value}-byte metadata limit.");

            await output.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }
    }

    private async ValueTask<int> ReadWithIdleTimeoutAsync(
        Stream input,
        Memory<byte> buffer,
        string source,
        CancellationToken cancellationToken)
    {
        using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        idle.CancelAfter(_transferIdleTimeout);
        try
        {
            return await input.ReadAsync(buffer, idle.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Transfer from {source} made no progress for {_transferIdleTimeout.TotalSeconds:0.#} seconds.");
        }
    }

    private static string RequireMetadataString(
        JsonElement element,
        string propertyName,
        string displayName)
    {
        if (!element.TryGetProperty(propertyName, out var value)
            || value.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException(
                $"Adoptium metadata property '{displayName}' must be a non-empty string.");

        return value.GetString()!;
    }

    private static async Task<bool> ValidateRuntimeAsync(
        string javaPath,
        int expectedMajor,
        CancellationToken cancellationToken)
    {
        try
        {
            var file = new FileInfo(javaPath);
            if (!file.Exists || file.Length == 0)
                return false;

            EnsureUnixExecutable(javaPath);
            var startInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            startInfo.ArgumentList.Add("-version");

            using var process = Process.Start(startInfo);
            if (process is null)
                return false;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(5));
            try
            {
                var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
                var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
                await process.WaitForExitAsync(timeout.Token);
                var text = $"{await stderrTask}\n{await stdoutTask}";
                if (process.ExitCode != 0)
                    return false;

                var version = ExtractQuotedVersion(text);
                if (MinecraftLaunchPlanBuilder.JavaMajor(version) != expectedMajor)
                    return false;

                return text.Contains("64-Bit", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("amd64", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("x86_64", StringComparison.OrdinalIgnoreCase)
                       || text.Contains("aarch64", StringComparison.OrdinalIgnoreCase);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
                return false;
            }
            catch (OperationCanceledException)
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill(entireProcessTree: true);
                }
                catch { }
                throw;
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return false;
        }
    }

    private static string ExtractQuotedVersion(string text)
    {
        var first = text.IndexOf('"');
        if (first < 0)
            return "Unknown";
        var second = text.IndexOf('"', first + 1);
        return second > first + 1 ? text[(first + 1)..second] : "Unknown";
    }

    private static async Task VerifySha256Async(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(path);
        using var sha256 = SHA256.Create();
        var hash = await sha256.ComputeHashAsync(stream, cancellationToken);
        var actual = Convert.ToHexString(hash).ToLowerInvariant();
        if (!string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Java runtime SHA-256 verification failed.");
    }

    internal static void ExtractArchive(string archivePath, string destination)
    {
        var root = Path.GetFullPath(destination);
        Directory.CreateDirectory(root);
        RejectReparsePoint(root);

        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            using var archive = ZipFile.OpenRead(archivePath);
            foreach (var entry in archive.Entries)
            {
                var target = ResolveArchiveEntry(root, entry.FullName);
                if (IsZipSymlink(entry))
                    throw new InvalidDataException(
                        $"Managed Java ZIP contains a symbolic-link entry: {entry.FullName}");

                if (entry.FullName.EndsWith("/", StringComparison.Ordinal)
                    || string.IsNullOrEmpty(entry.Name))
                {
                    CreateDirectoryTreeSafe(root, target);
                    continue;
                }

                CreateDirectoryTreeSafe(
                    root,
                    Path.GetDirectoryName(target)
                    ?? throw new InvalidDataException("Managed Java ZIP entry has no parent directory."));
                RejectExistingReparsePoint(target);
                using var input = entry.Open();
                using var output = new FileStream(
                    target,
                    FileMode.CreateNew,
                    FileAccess.Write,
                    FileShare.None);
                input.CopyTo(output);
            }
            return;
        }

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        using var reader = new TarReader(gzip, leaveOpen: false);
        TarEntry? tarEntry;
        while ((tarEntry = reader.GetNextEntry()) is not null)
        {
            var target = ResolveArchiveEntry(root, tarEntry.Name);
            switch (tarEntry.EntryType)
            {
                case TarEntryType.Directory:
                    CreateDirectoryTreeSafe(root, target);
                    break;

                case TarEntryType.RegularFile:
                case TarEntryType.V7RegularFile:
                    CreateDirectoryTreeSafe(
                        root,
                        Path.GetDirectoryName(target)
                        ?? throw new InvalidDataException("Managed Java TAR entry has no parent directory."));
                    RejectExistingReparsePoint(target);
                    using (var output = new FileStream(
                               target,
                               FileMode.CreateNew,
                               FileAccess.Write,
                               FileShare.None))
                    {
                        tarEntry.DataStream?.CopyTo(output);
                    }
                    break;

                case TarEntryType.SymbolicLink:
                    CreateSafeTarSymlink(root, target, tarEntry);
                    break;

                default:
                    throw new InvalidDataException(
                        $"Managed Java TAR contains unsupported entry type {tarEntry.EntryType}: {tarEntry.Name}");
            }
        }
    }

    private static string ResolveArchiveEntry(string root, string name)
    {
        if (string.IsNullOrWhiteSpace(name)
            || name.Contains('\\')
            || name.StartsWith("/", StringComparison.Ordinal)
            || Path.IsPathRooted(name)
            || name.Any(char.IsControl))
            throw new InvalidDataException($"Unsafe managed Java archive entry: {name}");

        var segments = name.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (segments.Length == 0 || segments.Any(segment => segment is "." or ".."))
            throw new InvalidDataException($"Unsafe managed Java archive entry: {name}");

        var target = Path.GetFullPath(
            Path.Combine(root, string.Join(Path.DirectorySeparatorChar, segments)));
        EnsureContained(root, target, $"Managed Java archive entry '{name}'");
        return target;
    }

    private static void CreateDirectoryTreeSafe(string root, string directory)
    {
        EnsureContained(root, directory, "Managed Java extraction directory");
        var relative = Path.GetRelativePath(root, directory);
        if (relative == ".")
            return;

        var current = root;
        foreach (var component in relative.Split(
                     Path.DirectorySeparatorChar,
                     StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, component);
            if (File.Exists(current) && !Directory.Exists(current))
                throw new InvalidDataException(
                    $"Managed Java extraction path collides with a file: {current}");
            if (Directory.Exists(current))
            {
                RejectReparsePoint(current);
                continue;
            }

            Directory.CreateDirectory(current);
            RejectReparsePoint(current);
        }
    }

    private static void CreateSafeTarSymlink(string root, string linkPath, TarEntry entry)
    {
        if (string.IsNullOrWhiteSpace(entry.LinkName)
            || Path.IsPathRooted(entry.LinkName)
            || entry.LinkName.Contains('\\')
            || entry.LinkName.Any(char.IsControl))
            throw new InvalidDataException(
                $"Managed Java TAR contains an unsafe symbolic-link target: {entry.Name}");

        var parent = Path.GetDirectoryName(linkPath)
            ?? throw new InvalidDataException("Managed Java TAR symbolic link has no parent directory.");
        CreateDirectoryTreeSafe(root, parent);
        RejectExistingReparsePoint(linkPath);

        var resolvedTarget = Path.GetFullPath(
            Path.Combine(
                parent,
                entry.LinkName.Replace('/', Path.DirectorySeparatorChar)));
        EnsureContained(
            root,
            resolvedTarget,
            $"Managed Java TAR symbolic link '{entry.Name}'");

        File.CreateSymbolicLink(linkPath, entry.LinkName);
    }

    private static void EnsureContained(string root, string candidate, string label)
    {
        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var fullCandidate = Path.GetFullPath(candidate);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (fullCandidate.Equals(fullRoot, comparison))
            return;

        var prefix = fullRoot + Path.DirectorySeparatorChar;
        if (!fullCandidate.StartsWith(prefix, comparison))
            throw new InvalidDataException($"{label} escapes the managed Java staging directory.");
    }

    private static void RejectExistingReparsePoint(string path)
    {
        if ((File.Exists(path) || Directory.Exists(path))
            && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"Managed Java extraction refuses to overwrite a symbolic link/reparse point: {path}");
        if (File.Exists(path) || Directory.Exists(path))
            throw new InvalidDataException(
                $"Managed Java archive contains a duplicate/colliding entry: {path}");
    }

    private static void RejectReparsePoint(string path)
    {
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"Managed Java extraction path contains a symbolic link/reparse point: {path}");
    }

    private static bool IsZipSymlink(ZipArchiveEntry entry)
    {
        var unixMode = (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static string? FindJavaExecutable(string root)
    {
        if (!Directory.Exists(root))
            return null;
        var executableName = OperatingSystem.IsWindows() ? "java.exe" : "java";
        try
        {
            return Directory.EnumerateFiles(root, executableName, SearchOption.AllDirectories)
                .FirstOrDefault(path => string.Equals(
                    Path.GetFileName(Path.GetDirectoryName(path)),
                    "bin",
                    StringComparison.OrdinalIgnoreCase));
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void EnsureUnixExecutable(string path)
    {
        if (OperatingSystem.IsWindows() || !File.Exists(path))
            return;
        try
        {
            File.SetUnixFileMode(
                path,
                UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
                | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
                | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
        }
        catch (PlatformNotSupportedException)
        {
        }
    }

    private static HttpClient CreateSharedClient()
    {
        var client = new HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("UN_Nexo-managed-runtime/1.0");
        return client;
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = true
    };

    private sealed record RuntimeAsset(string Link, string Sha256, string Version);

    private sealed record ManagedRuntimeManifest(
        int Major,
        string Version,
        string Vendor,
        string PackageUrl,
        string Sha256,
        DateTimeOffset InstalledAt);
}
