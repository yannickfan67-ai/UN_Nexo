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
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        await using var buffered = new MemoryStream();
        await CopyWithIdleTimeoutAsync(stream, buffered, "api.adoptium.net", cancellationToken);
        buffered.Position = 0;
        using var document = await JsonDocument.ParseAsync(buffered, cancellationToken: cancellationToken);
        if (document.RootElement.ValueKind != JsonValueKind.Array
            || document.RootElement.GetArrayLength() == 0)
            throw new InvalidOperationException($"No supported Java {major} runtime was returned by Adoptium.");

        var release = document.RootElement[0];
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

        var link = package.TryGetProperty("link", out var linkElement) ? linkElement.GetString() : null;
        var checksum = package.TryGetProperty("checksum", out var checksumElement) ? checksumElement.GetString() : null;
        var version = release.TryGetProperty("version_data", out var versionData)
                      && versionData.TryGetProperty("semver", out var semver)
            ? semver.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(link)
            || !Uri.TryCreate(link, UriKind.Absolute, out var packageUri)
            || packageUri.Scheme != Uri.UriSchemeHttps)
            throw new InvalidDataException("Adoptium returned an invalid Java package URL.");
        if (string.IsNullOrWhiteSpace(checksum)
            || checksum.Length != 64
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
        CancellationToken cancellationToken)
    {
        var buffer = new byte[64 * 1024];
        while (true)
        {
            var read = await ReadWithIdleTimeoutAsync(input, buffer, source, cancellationToken);
            if (read == 0)
                return;
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

    private static void ExtractArchive(string archivePath, string destination)
    {
        if (archivePath.EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
        {
            ZipFile.ExtractToDirectory(archivePath, destination, overwriteFiles: true);
            return;
        }

        using var file = File.OpenRead(archivePath);
        using var gzip = new GZipStream(file, CompressionMode.Decompress);
        TarFile.ExtractToDirectory(gzip, destination, overwriteFiles: true);
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
