using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed record ForgeInstallerProcessResult(int ExitCode, string StandardOutput, string StandardError);

public sealed class ForgeInstallService
{
    private readonly HttpClient _httpClient;
    private readonly NexoPathService _paths;
    private readonly MinecraftVanillaInstallService _vanillaInstaller;
    private readonly JavaRuntimeProvisionService _runtimeProvisioner;
    private readonly Func<ProcessStartInfo, CancellationToken, Task<ForgeInstallerProcessResult>> _processRunner;

    public ForgeInstallService(
        HttpClient httpClient,
        NexoPathService paths,
        MinecraftVanillaInstallService vanillaInstaller,
        JavaRuntimeProvisionService? runtimeProvisioner = null,
        Func<ProcessStartInfo, CancellationToken, Task<ForgeInstallerProcessResult>>? processRunner = null)
    {
        _httpClient = httpClient;
        _paths = paths;
        _vanillaInstaller = vanillaInstaller;
        _runtimeProvisioner = runtimeProvisioner ?? new JavaRuntimeProvisionService(paths);
        _processRunner = processRunner ?? RunProcessAsync;
    }

    public async Task PrepareAsync(
        GameInstance instance,
        MinecraftVersionInfo baseVersion,
        IProgress<InstallProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (!instance.Loader.Equals("forge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Forge preparation requires a Forge instance.");
        if (string.IsNullOrWhiteSpace(instance.LoaderVersion))
            throw new InvalidOperationException("Forge version is missing from the instance metadata.");
        if (!string.IsNullOrWhiteSpace(instance.BaseVersionId)
            && !instance.BaseVersionId.Equals(baseVersion.Id, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Forge instance expects Minecraft {instance.BaseVersionId}, not {baseVersion.Id}.");

        var expectedLaunchId = ForgeMetaService.GetLaunchVersionId(baseVersion.Id, instance.LoaderVersion);
        if (!instance.VersionId.Equals(expectedLaunchId, StringComparison.Ordinal))
            throw new InvalidDataException(
                $"Forge launch profile '{instance.VersionId}' does not match expected '{expectedLaunchId}'.");

        var instanceRoot = _paths.GetInstanceDirectory(instance.Id);
        var gameRoot = _paths.GetInstanceGameDirectory(instance.Id);
        Directory.CreateDirectory(gameRoot);
        var statePath = Path.Combine(instanceRoot, "install-state.json");
        byte[]? previousState = File.Exists(statePath)
            ? await File.ReadAllBytesAsync(statePath, cancellationToken)
            : null;

        try
        {
            progress?.Report(new InstallProgress("Vanilla base", 0, 1, baseVersion.Id));
            var vanillaProxy = instance with
            {
                VersionId = baseVersion.Id,
                Loader = "vanilla",
                BaseVersionId = null,
                LoaderVersion = null
            };
            await _vanillaInstaller.InstallAsync(vanillaProxy, baseVersion, progress, cancellationToken);
            TryDeleteFile(statePath);
            progress?.Report(new InstallProgress("Vanilla base", 1, 1, baseVersion.Id));

            EnsureLauncherProfileFile(gameRoot);
            var javaMajor = await GetRequiredJavaAsync(gameRoot, baseVersion.Id, cancellationToken);
            progress?.Report(new InstallProgress("Forge Java", 0, 1, $"Java {javaMajor}"));
            var java = await _runtimeProvisioner.EnsureJavaAsync(
                javaMajor,
                message => progress?.Report(new InstallProgress("Forge Java", 0, 1, Detail: message)),
                cancellationToken);
            progress?.Report(new InstallProgress("Forge Java", 1, 1, java.Version));

            progress?.Report(new InstallProgress("Forge installer", 0, 1, instance.LoaderVersion));
            var installerPath = await EnsureInstallerAsync(
                baseVersion.Id,
                instance.LoaderVersion,
                cancellationToken);
            progress?.Report(new InstallProgress("Forge installer", 1, 1, Path.GetFileName(installerPath)));

            progress?.Report(new InstallProgress("Forge processors", 0, 1, instance.VersionId));
            var startInfo = BuildInstallerStartInfo(java.JavaPath, installerPath, gameRoot);
            var result = await _processRunner(startInfo, cancellationToken);
            await WriteInstallerLogAsync(instanceRoot, startInfo, result, cancellationToken);
            if (result.ExitCode != 0)
            {
                var detail = LastUsefulLine(result.StandardError)
                    ?? LastUsefulLine(result.StandardOutput)
                    ?? $"exit code {result.ExitCode}";
                throw new InvalidOperationException($"Forge installer failed: {detail}");
            }

            var launchMetadata = Path.Combine(
                gameRoot,
                "versions",
                instance.VersionId,
                instance.VersionId + ".json");
            if (!File.Exists(launchMetadata))
                throw new InvalidDataException(
                    $"Forge installer completed but did not create {instance.VersionId}.json.");
            using (var resolved = await new MinecraftVersionMetadataResolver()
                       .ResolveAsync(gameRoot, instance.VersionId, cancellationToken))
            {
                if (!resolved.ClientVersionId.Equals(baseVersion.Id, StringComparison.Ordinal))
                    throw new InvalidDataException(
                        $"Forge profile resolved client '{resolved.ClientVersionId}', expected '{baseVersion.Id}'.");
            }
            progress?.Report(new InstallProgress("Forge processors", 1, 1, instance.VersionId));

            var state = new
            {
                instance.Id,
                instance.Name,
                version = instance.VersionId,
                baseVersion = baseVersion.Id,
                loader = "forge",
                loaderVersion = instance.LoaderVersion,
                launchVersion = instance.VersionId,
                installedAt = DateTimeOffset.UtcNow,
                source = "minecraftforge-official-installer",
                state = "prepared"
            };
            await WriteJsonAtomicAsync(statePath, state, cancellationToken);
            progress?.Report(new InstallProgress(
                "Ready", 1, 1, instance.VersionId,
                Detail: $"Forge {instance.LoaderVersion} is ready"));
        }
        catch
        {
            TryDeleteFile(statePath);
            if (previousState is not null)
                await File.WriteAllBytesAsync(statePath, previousState, CancellationToken.None);
            throw;
        }
    }

    public static ProcessStartInfo BuildInstallerStartInfo(
        string javaPath,
        string installerPath,
        string gameRoot)
    {
        if (string.IsNullOrWhiteSpace(javaPath))
            throw new ArgumentException("Java path is required.", nameof(javaPath));
        if (string.IsNullOrWhiteSpace(installerPath))
            throw new ArgumentException("Forge installer path is required.", nameof(installerPath));
        if (string.IsNullOrWhiteSpace(gameRoot))
            throw new ArgumentException("Game root is required.", nameof(gameRoot));

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
        string minecraftVersion,
        string forgeVersion,
        CancellationToken cancellationToken)
    {
        var url = ForgeMetaService.GetInstallerUrl(minecraftVersion, forgeVersion);
        var sha1 = (await _httpClient.GetStringAsync(url + ".sha1", cancellationToken)).Trim();
        var whitespace = sha1.IndexOfAny([' ', '\t', '\r', '\n']);
        if (whitespace >= 0)
            sha1 = sha1[..whitespace];
        if (!Regex.IsMatch(sha1, "^[a-fA-F0-9]{40}$"))
            throw new InvalidDataException("Forge Maven returned an invalid installer SHA-1.");

        var cacheRoot = Path.Combine(
            _paths.GetDataRoot(),
            "cache",
            "forge",
            minecraftVersion + "-" + forgeVersion);
        Directory.CreateDirectory(cacheRoot);
        var fileName = $"forge-{minecraftVersion}-{forgeVersion}-installer.jar";
        var target = Path.Combine(cacheRoot, fileName);
        if (await VerifySha1Async(target, sha1, cancellationToken))
            return target;

        var temp = target + ".part";
        TryDeleteFile(temp);
        try
        {
            using var response = await _httpClient.GetAsync(
                url,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
            await using var output = new FileStream(
                temp,
                FileMode.CreateNew,
                FileAccess.Write,
                FileShare.None,
                128 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            await input.CopyToAsync(output, cancellationToken);
            await output.FlushAsync(cancellationToken);
            output.Close();
            if (!await VerifySha1Async(temp, sha1, cancellationToken))
                throw new InvalidDataException("Forge installer SHA-1 verification failed.");
            File.Move(temp, target, overwrite: true);
            return target;
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private static async Task<int> GetRequiredJavaAsync(
        string gameRoot,
        string versionId,
        CancellationToken cancellationToken)
    {
        using var resolved = await new MinecraftVersionMetadataResolver()
            .ResolveAsync(gameRoot, versionId, cancellationToken);
        var root = resolved.Document.RootElement;
        return root.TryGetProperty("javaVersion", out var javaVersion)
               && javaVersion.TryGetProperty("majorVersion", out var major)
            ? major.GetInt32()
            : 8;
    }

    private static void EnsureLauncherProfileFile(string gameRoot)
    {
        var standard = Path.Combine(gameRoot, "launcher_profiles.json");
        var store = Path.Combine(gameRoot, "launcher_profiles_microsoft_store.json");
        if (File.Exists(standard) || File.Exists(store))
            return;
        File.WriteAllText(standard, "{\"profiles\":{}}");
    }

    private static async Task<bool> VerifySha1Async(
        string path,
        string expected,
        CancellationToken cancellationToken)
    {
        var info = new FileInfo(path);
        if (!info.Exists || info.Length == 0)
            return false;
        await using var stream = File.OpenRead(path);
        var hash = await SHA1.HashDataAsync(stream, cancellationToken);
        return Convert.ToHexString(hash).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<ForgeInstallerProcessResult> RunProcessAsync(
        ProcessStartInfo startInfo,
        CancellationToken cancellationToken)
    {
        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
            throw new InvalidOperationException("Failed to start the Forge installer process.");
        var stdoutTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        var stderrTask = process.StandardError.ReadToEndAsync(cancellationToken);
        try
        {
            await process.WaitForExitAsync(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            try
            {
                if (!process.HasExited)
                    process.Kill(entireProcessTree: true);
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
        var logs = Path.Combine(instanceRoot, "launcher-logs");
        Directory.CreateDirectory(logs);
        var path = Path.Combine(logs, "forge-installer-latest.log");
        var text = new StringBuilder()
            .AppendLine($"Java: {startInfo.FileName}")
            .AppendLine($"Exit code: {result.ExitCode}")
            .AppendLine("--- stdout ---")
            .AppendLine(Limit(result.StandardOutput))
            .AppendLine("--- stderr ---")
            .AppendLine(Limit(result.StandardError))
            .ToString();
        await File.WriteAllTextAsync(path, text, cancellationToken);
    }

    private static string Limit(string value)
    {
        const int max = 256 * 1024;
        return value.Length <= max ? value : value[^max..];
    }

    private static string? LastUsefulLine(string value)
        => value.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Select(item => item.Trim())
            .LastOrDefault(item => item.Length > 0);

    private static async Task WriteJsonAtomicAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var temp = path + ".tmp";
        try
        {
            await File.WriteAllTextAsync(
                temp,
                JsonSerializer.Serialize(value, new JsonSerializerOptions { WriteIndented = true }),
                cancellationToken);
            File.Move(temp, path, overwrite: true);
        }
        catch
        {
            TryDeleteFile(temp);
            throw;
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch
        {
        }
    }
}
