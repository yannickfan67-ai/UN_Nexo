using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed record ForgeInstallerProcessResult(
    int ExitCode,
    string StandardOutput,
    string StandardError);

public sealed class ForgeInstallService
{
    private const long MaxInstallerBytes = 128L * 1024L * 1024L;
    private const int MaxChecksumBytes = 1024;
    private const int MaxInstallerLogChars = 256 * 1024;

    private readonly HttpClient _httpClient;
    private readonly NexoPathService _paths;
    private readonly MinecraftVanillaInstallService _vanillaInstaller;
    private readonly Func<
        int,
        IProgress<string>?,
        CancellationToken,
        Task<JavaInstallation>> _ensureJava;
    private readonly InstanceOperationCoordinator _operations;
    private readonly TimeSpan _transferIdleTimeout;
    private readonly Func<
        ProcessStartInfo,
        CancellationToken,
        Task<ForgeInstallerProcessResult>> _processRunner;

    public ForgeInstallService(
        HttpClient httpClient,
        NexoPathService paths,
        MinecraftVanillaInstallService vanillaInstaller,
        JavaRuntimeProvisionService? runtimeProvisioner = null,
        Func<
            ProcessStartInfo,
            CancellationToken,
            Task<ForgeInstallerProcessResult>>? processRunner = null,
        TimeSpan? transferIdleTimeout = null,
        Func<
            int,
            IProgress<string>?,
            CancellationToken,
            Task<JavaInstallation>>? javaProvisioner = null)
    {
        _httpClient = httpClient
            ?? throw new ArgumentNullException(nameof(httpClient));
        _paths = paths
            ?? throw new ArgumentNullException(nameof(paths));
        _vanillaInstaller = vanillaInstaller
            ?? throw new ArgumentNullException(nameof(vanillaInstaller));
        var provisioner =
            runtimeProvisioner ?? new JavaRuntimeProvisionService(paths);
        _ensureJava = javaProvisioner
            ?? ((major, progress, cancellation) =>
                provisioner.EnsureJavaAsync(
                    major,
                    progress,
                    cancellation));
        _processRunner =
            processRunner ?? RunProcessAsync;
        _operations = new InstanceOperationCoordinator(paths);
        _transferIdleTimeout = transferIdleTimeout
            ?? TimeSpan.FromSeconds(30);
        if (_transferIdleTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(transferIdleTimeout),
                "Forge transfer idle timeout must be positive.");
        }
    }

    public async Task PrepareAsync(
        GameInstance instance,
        MinecraftVersionInfo baseVersion,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        ArgumentNullException.ThrowIfNull(baseVersion);

        if (!instance.Loader.Equals(
                "forge",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Forge preparation requires a Forge instance.");
        }

        if (string.IsNullOrWhiteSpace(instance.LoaderVersion))
            throw new InvalidOperationException(
                "Forge version is missing from the instance metadata.");
        var forgeVersion = instance.LoaderVersion.Trim();

        if (!string.IsNullOrWhiteSpace(instance.BaseVersionId)
            && !instance.BaseVersionId.Equals(
                baseVersion.Id,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Forge instance expects Minecraft {instance.BaseVersionId}, not {baseVersion.Id}.");
        }

        var expectedLaunchId = ForgeMetaService.GetLaunchVersionId(
            baseVersion.Id,
            forgeVersion);
        if (!instance.VersionId.Equals(
                expectedLaunchId,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                $"Forge launch profile '{instance.VersionId}' does not match expected '{expectedLaunchId}'.");
        }

        await using var operationLease =
            await _operations.AcquireAsync(
                instance.Id,
                "prepare-forge",
                cancellationToken);

        await PrepareWithinOperationAsync(
            instance,
            baseVersion,
            progress,
            cancellationToken);
    }

    internal async Task PrepareWithinOperationAsync(
        GameInstance instance,
        MinecraftVersionInfo baseVersion,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        var instanceRoot =
            _paths.EnsureInstanceDirectoryPhysical(instance.Id);
        var gameRoot =
            EnsurePhysicalChildDirectory(
                instanceRoot,
                "game",
                "instance game directory");
        var statePath =
            Path.Combine(instanceRoot, "install-state.json");

        var previousState =
            await ReadExistingRegularFileAsync(
                statePath,
                cancellationToken);

        try
        {
            Report(
                progress,
                new InstallProgress(
                    "Vanilla base",
                    0,
                    1,
                    baseVersion.Id));

            var vanillaProxy = instance with
            {
                VersionId = baseVersion.Id,
                Loader = "vanilla",
                BaseVersionId = null,
                LoaderVersion = null
            };
            await _vanillaInstaller.InstallWithinOperationAsync(
                vanillaProxy,
                baseVersion,
                progress,
                cancellationToken);

            TryDeleteRegularFile(statePath);
            Report(
                progress,
                new InstallProgress(
                    "Vanilla base",
                    1,
                    1,
                    baseVersion.Id));

            EnsureLauncherProfileFile(
                instanceRoot,
                gameRoot);

            var javaMajor =
                await GetRequiredJavaAsync(
                    gameRoot,
                    baseVersion.Id,
                    cancellationToken);
            Report(
                progress,
                new InstallProgress(
                    "Forge Java",
                    0,
                    1,
                    $"Java {javaMajor}"));

            var javaProgress =
                new Progress<string>(message =>
                    Report(
                        progress,
                        new InstallProgress(
                            "Forge Java",
                            0,
                            1,
                            Detail: message)));
            var java =
                await _ensureJava(
                    javaMajor,
                    javaProgress,
                    cancellationToken);

            Report(
                progress,
                new InstallProgress(
                    "Forge Java",
                    1,
                    1,
                    java.Version));

            Report(
                progress,
                new InstallProgress(
                    "Forge installer",
                    0,
                    1,
                    forgeVersion));

            var installerPath =
                await EnsureInstallerAsync(
                    instance,
                    baseVersion.Id,
                    forgeVersion,
                    cancellationToken);

            Report(
                progress,
                new InstallProgress(
                    "Forge installer",
                    1,
                    1,
                    Path.GetFileName(installerPath)));

            Report(
                progress,
                new InstallProgress(
                    "Forge processors",
                    0,
                    1,
                    instance.VersionId));

            var startInfo = BuildInstallerStartInfo(
                java.JavaPath,
                installerPath,
                gameRoot);
            var result = await _processRunner(
                startInfo,
                cancellationToken);

            await WriteInstallerLogAsync(
                instanceRoot,
                startInfo,
                result,
                cancellationToken);

            if (result.ExitCode != 0)
            {
                var detail =
                    LastUsefulLine(result.StandardError)
                    ?? LastUsefulLine(result.StandardOutput)
                    ?? $"exit code {result.ExitCode}";
                throw new InvalidOperationException(
                    $"Forge installer failed: {detail}");
            }

            ValidateInstalledProfilePath(
                gameRoot,
                instance.VersionId);

            using (var resolved =
                   await new MinecraftVersionMetadataResolver()
                       .ResolveAsync(
                           gameRoot,
                           instance.VersionId,
                           cancellationToken))
            {
                if (!resolved.ClientVersionId.Equals(
                        baseVersion.Id,
                        StringComparison.Ordinal))
                {
                    throw new InvalidDataException(
                        $"Forge profile resolved client '{resolved.ClientVersionId}', expected '{baseVersion.Id}'.");
                }
            }

            Report(
                progress,
                new InstallProgress(
                    "Forge processors",
                    1,
                    1,
                    instance.VersionId));

            var state = new
            {
                instance.Id,
                instance.Name,
                version = instance.VersionId,
                baseVersion = baseVersion.Id,
                loader = "forge",
                loaderVersion = forgeVersion,
                launchVersion = instance.VersionId,
                installedAt = DateTimeOffset.UtcNow,
                source = "minecraftforge-official-installer",
                state = "prepared"
            };

            _paths.EnsureInstanceDirectoryPhysical(
                instance.Id);
            await AtomicJsonFile.WriteAsync(
                statePath,
                state,
                new JsonSerializerOptions
                {
                    WriteIndented = true
                },
                cancellationToken);

            Report(
                progress,
                new InstallProgress(
                    "Ready",
                    1,
                    1,
                    instance.VersionId,
                    Detail:
                        $"Forge {forgeVersion} is ready"));
        }
        catch
        {
            TryDeleteRegularFile(statePath);
            if (previousState is not null)
            {
                await RestoreBytesAtomicAsync(
                    statePath,
                    previousState);
            }
            throw;
        }
    }

    public async Task<string?> GetBaseVersionIdAsync(
        GameInstance instance,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(instance);
        if (!string.IsNullOrWhiteSpace(instance.BaseVersionId))
            return instance.BaseVersionId;

        var profilePath =
            Path.Combine(
                _paths.GetInstanceGameDirectory(instance.Id),
                "versions",
                instance.VersionId,
                instance.VersionId + ".json");
        if (!File.Exists(profilePath))
            return null;

        RejectExistingReparseFile(
            profilePath,
            "Forge launch metadata");
        var info = new FileInfo(profilePath);
        if (info.Length > 8L * 1024L * 1024L)
        {
            throw new InvalidDataException(
                "Forge launch metadata exceeds the 8 MiB local metadata limit.");
        }

        await using var stream =
            new FileStream(
                profilePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous
                | FileOptions.SequentialScan);
        using var document =
            await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                "Forge launch metadata root must be an object.");

        if (!root.TryGetProperty(
                "inheritsFrom",
                out var inherits))
        {
            return null;
        }

        if (inherits.ValueKind != JsonValueKind.String
            || string.IsNullOrWhiteSpace(
                inherits.GetString()))
        {
            throw new InvalidDataException(
                "Forge launch metadata property 'inheritsFrom' must be a non-empty string.");
        }

        return inherits.GetString();
    }

    public static ProcessStartInfo BuildInstallerStartInfo(
        string javaPath,
        string installerPath,
        string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(javaPath))
            throw new ArgumentException(
                "Java path is required.",
                nameof(javaPath));
        if (string.IsNullOrWhiteSpace(installerPath))
            throw new ArgumentException(
                "Installer path is required.",
                nameof(installerPath));
        if (string.IsNullOrWhiteSpace(gameRoot))
            throw new ArgumentException(
                "Game root is required.",
                nameof(gameRoot));

        var startInfo = new ProcessStartInfo
        {
            FileName = javaPath,
            WorkingDirectory = gameRoot,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };
        startInfo.ArgumentList.Add("-jar");
        startInfo.ArgumentList.Add(installerPath);
        startInfo.ArgumentList.Add("--installClient");
        startInfo.ArgumentList.Add(gameRoot);
        return startInfo;
    }

    private async Task<string> EnsureInstallerAsync(
        GameInstance instance,
        string minecraftVersion,
        string forgeVersion,
        CancellationToken cancellationToken)
    {
        var installerUrl =
            ForgeMetaService.GetInstallerUrl(
                minecraftVersion,
                forgeVersion);
        var expectedSha1 =
            await DownloadSha1Async(
                installerUrl + ".sha1",
                cancellationToken);

        var instanceRoot =
            _paths.EnsureInstanceDirectoryPhysical(
                instance.Id);
        var cacheRoot =
            EnsurePhysicalChildDirectory(
                instanceRoot,
                "loader-installers",
                "loader installer cache");
        var forgeRoot =
            EnsurePhysicalChildDirectory(
                cacheRoot,
                "forge",
                "Forge installer cache");

        var coordinate =
            minecraftVersion + "-" + forgeVersion;
        var fileName =
            $"forge-{coordinate}-installer.jar";
        var target =
            Path.Combine(forgeRoot, fileName);

        if (await VerifySha1Async(
                target,
                expectedSha1,
                cancellationToken))
        {
            return target;
        }

        RejectExistingReparseFile(
            target,
            "Forge installer cache file");

        var temp =
            target + "." + Guid.NewGuid().ToString("N") + ".part";
        try
        {
            using var response =
                await TrustedHttpDownload.SendGetAsync(
                    _httpClient,
                    installerUrl,
                    "Forge installer",
                    cancellationToken);
            response.EnsureSuccessStatusCode();

            if (response.Content.Headers.ContentLength
                is > MaxInstallerBytes)
            {
                throw new InvalidDataException(
                    $"Forge installer exceeds the {MaxInstallerBytes}-byte limit.");
            }

            await using var input =
                await response.Content.ReadAsStreamAsync(
                    cancellationToken);
            await using (var output =
                         new FileStream(
                             temp,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous
                             | FileOptions.SequentialScan))
            {
                await CopyBoundedAsync(
                    input,
                    output,
                    "Forge installer",
                    MaxInstallerBytes,
                    cancellationToken);
                await output.FlushAsync(cancellationToken);
            }

            if (!await VerifySha1Async(
                    temp,
                    expectedSha1,
                    cancellationToken))
            {
                throw new InvalidDataException(
                    "Forge installer SHA-1 verification failed.");
            }

            _paths.EnsureInstanceDirectoryPhysical(
                instance.Id);
            EnsurePhysicalDirectory(
                forgeRoot,
                "Forge installer cache");
            File.Move(
                temp,
                target,
                overwrite: true);
            return target;
        }
        catch
        {
            TryDeleteRegularFile(temp);
            throw;
        }
    }

    private async Task<string> DownloadSha1Async(
        string url,
        CancellationToken cancellationToken)
    {
        using var response =
            await TrustedHttpDownload.SendGetAsync(
                _httpClient,
                url,
                "Forge installer checksum",
                cancellationToken);
        response.EnsureSuccessStatusCode();

        if (response.Content.Headers.ContentLength
            is > MaxChecksumBytes)
        {
            throw new InvalidDataException(
                "Forge installer checksum response is too large.");
        }

        await using var input =
            await response.Content.ReadAsStreamAsync(
                cancellationToken);
        await using var output = new MemoryStream();
        await CopyBoundedAsync(
            input,
            output,
            "Forge installer checksum",
            MaxChecksumBytes,
            cancellationToken);

        var text =
            Encoding.ASCII
                .GetString(output.ToArray())
                .Trim();
        var token =
            text.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault();

        if (token is null
            || token.Length != 40
            || token.Any(character =>
                !Uri.IsHexDigit(character)))
        {
            throw new InvalidDataException(
                "Forge Maven returned an invalid installer SHA-1.");
        }

        return token.ToLowerInvariant();
    }

    private async Task CopyBoundedAsync(
        Stream input,
        Stream output,
        string label,
        long maxBytes,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[128 * 1024];
        long total = 0;

        while (true)
        {
            using var idle =
                CancellationTokenSource
                    .CreateLinkedTokenSource(
                        cancellationToken);
            idle.CancelAfter(_transferIdleTimeout);

            int read;
            try
            {
                read = await input.ReadAsync(
                    buffer.AsMemory(),
                    idle.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new TimeoutException(
                    $"{label} made no progress for {_transferIdleTimeout.TotalSeconds:0.###} seconds.");
            }

            if (read == 0)
                return;

            total = checked(total + read);
            if (total > maxBytes)
            {
                throw new InvalidDataException(
                    $"{label} exceeded the {maxBytes}-byte limit.");
            }

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }
    }

    private static async Task<int> GetRequiredJavaAsync(
        string gameRoot,
        string versionId,
        CancellationToken cancellationToken)
    {
        using var resolved =
            await new MinecraftVersionMetadataResolver()
                .ResolveAsync(
                    gameRoot,
                    versionId,
                    cancellationToken);
        var root = resolved.Document.RootElement;
        if (!root.TryGetProperty(
                "javaVersion",
                out var javaVersion))
        {
            return 8;
        }

        if (javaVersion.ValueKind != JsonValueKind.Object
            || !javaVersion.TryGetProperty(
                "majorVersion",
                out var major)
            || major.ValueKind != JsonValueKind.Number
            || !major.TryGetInt32(out var value)
            || value <= 0)
        {
            throw new InvalidDataException(
                "Minecraft base profile contains an invalid Java requirement.");
        }

        return value;
    }

    private static void EnsureLauncherProfileFile(
        string instanceRoot,
        string gameRoot)
    {
        EnsurePhysicalDirectory(
            instanceRoot,
            "managed instance directory");
        EnsurePhysicalDirectory(
            gameRoot,
            "instance game directory");

        var standard =
            Path.Combine(
                gameRoot,
                "launcher_profiles.json");
        var store =
            Path.Combine(
                gameRoot,
                "launcher_profiles_microsoft_store.json");

        RejectExistingReparseFile(
            standard,
            "launcher profile file");
        RejectExistingReparseFile(
            store,
            "Microsoft Store launcher profile file");

        if (File.Exists(standard)
            || File.Exists(store))
        {
            return;
        }

        File.WriteAllText(
            standard,
            "{\"profiles\":{}}",
            new UTF8Encoding(false));
    }

    private static void ValidateInstalledProfilePath(
        string gameRoot,
        string launchVersionId)
    {
        EnsurePhysicalDirectory(
            gameRoot,
            "instance game directory");

        var versionsRoot =
            Path.Combine(gameRoot, "versions");
        EnsurePhysicalDirectory(
            versionsRoot,
            "Minecraft versions directory");

        var versionRoot =
            Path.Combine(
                versionsRoot,
                launchVersionId);
        EnsurePhysicalDirectory(
            versionRoot,
            "Forge version directory");

        var metadata =
            Path.Combine(
                versionRoot,
                launchVersionId + ".json");
        if (!File.Exists(metadata))
        {
            throw new InvalidDataException(
                $"Forge installer completed but did not create {launchVersionId}.json.");
        }

        RejectExistingReparseFile(
            metadata,
            "Forge launch metadata");
    }

    private static string EnsurePhysicalChildDirectory(
        string parent,
        string name,
        string label)
    {
        EnsurePhysicalDirectory(
            parent,
            "managed parent directory");

        if (name.Length == 0
            || name.IndexOfAny(
                [
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar
                ]) >= 0)
        {
            throw new InvalidDataException(
                $"{label} has an invalid directory name.");
        }

        var child =
            Path.GetFullPath(
                Path.Combine(parent, name));
        var expectedParent =
            Path.GetFullPath(parent)
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        var actualParent =
            Path.GetDirectoryName(child)?
                .TrimEnd(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar);
        var comparison =
            OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal;

        if (actualParent is null
            || !actualParent.Equals(
                expectedParent,
                comparison))
        {
            throw new InvalidDataException(
                $"{label} must remain a direct child of its managed parent.");
        }

        if (!Directory.Exists(child))
        {
            if (File.Exists(child))
            {
                throw new InvalidDataException(
                    $"{label} is occupied by a file.");
            }
            Directory.CreateDirectory(child);
        }

        EnsurePhysicalDirectory(
            child,
            label);
        EnsurePhysicalDirectory(
            parent,
            "managed parent directory");
        return child;
    }

    private static void EnsurePhysicalDirectory(
        string path,
        string label)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException(
                $"{label} is missing: {path}");

        var attributes =
            File.GetAttributes(path);
        if ((attributes & FileAttributes.Directory) == 0
            || (attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{label} must be a physical non-reparse directory.");
        }
    }

    private static void RejectExistingReparseFile(
        string path,
        string label)
    {
        if (!File.Exists(path))
            return;

        var attributes =
            File.GetAttributes(path);
        if ((attributes & FileAttributes.ReparsePoint) != 0)
        {
            throw new InvalidDataException(
                $"{label} must not be a symbolic link or reparse point.");
        }
    }

    private static async Task<byte[]?> ReadExistingRegularFileAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return null;

        RejectExistingReparseFile(
            path,
            "install state");
        return await File.ReadAllBytesAsync(
            path,
            cancellationToken);
    }

    private static async Task RestoreBytesAtomicAsync(
        string path,
        byte[] bytes)
    {
        var directory =
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "State path has no parent directory.");
        EnsurePhysicalDirectory(
            directory,
            "managed instance directory");

        var temp =
            path + "." + Guid.NewGuid().ToString("N") + ".restore";
        try
        {
            await File.WriteAllBytesAsync(
                temp,
                bytes,
                CancellationToken.None);
            EnsurePhysicalDirectory(
                directory,
                "managed instance directory");
            File.Move(
                temp,
                path,
                overwrite: true);
        }
        finally
        {
            TryDeleteRegularFile(temp);
        }
    }

    private static async Task<bool> VerifySha1Async(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
            return false;

        RejectExistingReparseFile(
            path,
            "Forge installer cache file");
        var info = new FileInfo(path);
        if (info.Length <= 0
            || info.Length > MaxInstallerBytes)
        {
            return false;
        }

        await using var stream =
            new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                128 * 1024,
                FileOptions.Asynchronous
                | FileOptions.SequentialScan);
        var hash =
            await SHA1.HashDataAsync(
                stream,
                cancellationToken);
        return Convert.ToHexString(hash)
            .Equals(
                expected,
                StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ForgeInstallerProcessResult>
        RunProcessAsync(
            ProcessStartInfo startInfo,
            CancellationToken cancellationToken)
    {
        using var process =
            new Process
            {
                StartInfo = startInfo
            };
        if (!process.Start())
        {
            throw new InvalidOperationException(
                "Failed to start the Forge installer process.");
        }

        var stdoutTask =
            process.StandardOutput.ReadToEndAsync(
                cancellationToken);
        var stderrTask =
            process.StandardError.ReadToEndAsync(
                cancellationToken);

        try
        {
            await process.WaitForExitAsync(
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(
                        entireProcessTree: true);
            }
            catch
            {
            }
            throw;
        }

        return new ForgeInstallerProcessResult(
            process.ExitCode,
            await stdoutTask,
            await stderrTask);
    }

    private static async Task WriteInstallerLogAsync(
        string instanceRoot,
        ProcessStartInfo startInfo,
        ForgeInstallerProcessResult result,
        CancellationToken cancellationToken)
    {
        var logs =
            EnsurePhysicalChildDirectory(
                instanceRoot,
                "launcher-logs",
                "launcher log directory");
        var path =
            Path.Combine(
                logs,
                "forge-installer-latest.log");
        RejectExistingReparseFile(
            path,
            "Forge installer log");

        var text =
            new StringBuilder()
                .AppendLine(
                    $"Java: {startInfo.FileName}")
                .AppendLine(
                    $"Exit code: {result.ExitCode}")
                .AppendLine("--- stdout ---")
                .AppendLine(
                    Limit(result.StandardOutput))
                .AppendLine("--- stderr ---")
                .AppendLine(
                    Limit(result.StandardError))
                .ToString();

        await AtomicWriteTextAsync(
            path,
            text,
            cancellationToken);
    }

    private static async Task AtomicWriteTextAsync(
        string path,
        string text,
        CancellationToken cancellationToken)
    {
        var directory =
            Path.GetDirectoryName(path)
            ?? throw new InvalidOperationException(
                "Text destination has no parent.");
        EnsurePhysicalDirectory(
            directory,
            "managed output directory");

        var temp =
            path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temp,
                text,
                new UTF8Encoding(false),
                cancellationToken);
            EnsurePhysicalDirectory(
                directory,
                "managed output directory");
            File.Move(
                temp,
                path,
                overwrite: true);
        }
        finally
        {
            TryDeleteRegularFile(temp);
        }
    }

    private static string Limit(string value)
        => value.Length <= MaxInstallerLogChars
            ? value
            : value[^MaxInstallerLogChars..];

    private static string? LastUsefulLine(string value)
        => value.Split(
                ['\r', '\n'],
                StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .LastOrDefault(item =>
                item.Length > 0);

    private static void TryDeleteRegularFile(
        string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var attributes =
                File.GetAttributes(path);
            if ((attributes
                 & FileAttributes.ReparsePoint) == 0)
            {
                File.Delete(path);
            }
        }
        catch
        {
        }
    }

    private static void Report(
        IProgress<InstallProgress>? progress,
        InstallProgress value)
        => progress?.Report(value);
}
