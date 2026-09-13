using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Launching;

public sealed partial class MinecraftLaunchPlanBuilder(NexoPathService paths)
{
    public async Task<MinecraftLaunchPlan> BuildAsync(
        GameInstance instance,
        LauncherAccount account,
        IEnumerable<JavaInstallation> installations,
        CancellationToken cancellationToken = default)
    {
        if (!instance.Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("Only Vanilla instances can be launched in this build.");
        if (!account.IsOffline)
            throw new InvalidOperationException(
                "Microsoft sign-in is not available yet. Select an offline profile for local play.");
        if (!Regex.IsMatch(account.DisplayName, "^[A-Za-z0-9_]{3,16}$")
            || !Guid.TryParse(account.Uuid, out var uuid))
            throw new InvalidDataException("The selected offline profile is invalid.");
        if (RuntimeInformation.OSArchitecture != Architecture.X64)
            throw new PlatformNotSupportedException("Vanilla launch currently requires x64.");

        var instanceRoot = Within(paths.GetInstancesRoot(), instance.Id);
        RequireFile(Path.Combine(instanceRoot, "install-state.json"));
        var gameRoot = Within(instanceRoot, "game");
        var versionRoot = Within(Path.Combine(gameRoot, "versions"), instance.VersionId);
        var metadataPath = Within(versionRoot, instance.VersionId + ".json");
        RequireFile(metadataPath);

        using var metadataDocument = await ReadJsonAsync(metadataPath, cancellationToken);
        var root = metadataDocument.RootElement;
        if (root.TryGetProperty("inheritsFrom", out _))
            throw new InvalidDataException("Inherited versions are not supported yet.");
        if (!string.Equals(root.GetProperty("id").GetString(), instance.VersionId,
                StringComparison.Ordinal))
            throw new InvalidDataException("Instance and version metadata do not match.");

        var requiredJava = root.TryGetProperty("javaVersion", out var javaVersion)
            ? javaVersion.GetProperty("majorVersion").GetInt32() : 8;
        var java = installations
            .Where(item => item.Is64Bit && File.Exists(item.JavaPath))
            .OrderBy(item => JavaMajor(item.Version) == requiredJava ? 0 : 1)
            .FirstOrDefault(item => JavaMajor(item.Version) == requiredJava);
        if (java is null)
        {
            java = await new JavaRuntimeProvisionService(paths)
                .EnsureJavaAsync(requiredJava, cancellationToken: cancellationToken);
        }

        var librariesRoot = Within(gameRoot, "libraries");
        var nativesRoot = Within(gameRoot, Path.Combine("natives", instance.VersionId));
        Directory.CreateDirectory(nativesRoot);
        var classpath = new List<string>();

        if (root.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MinecraftRules.Allows(library))
                    continue;
                if (!library.TryGetProperty("downloads", out var downloads))
                    throw new InvalidDataException("A library has no download metadata.");

                if (downloads.TryGetProperty("artifact", out var artifact))
                {
                    var artifactPath = artifact.GetProperty("path").GetString()
                        ?? throw new InvalidDataException("A library artifact has no path.");
                    var file = Within(librariesRoot, artifactPath);
                    RequireFile(file, artifact);
                    classpath.Add(file);
                }

                var classifier = MinecraftRules.NativeClassifier(library);
                if (classifier is null)
                    continue;
                if (!downloads.TryGetProperty("classifiers", out var classifiers)
                    || !classifiers.TryGetProperty(classifier, out var nativeArtifact))
                    throw new InvalidDataException("A native library is missing its classifier.");

                var nativePath = nativeArtifact.GetProperty("path").GetString()
                    ?? throw new InvalidDataException("A native library has no path.");
                RequireFile(Within(librariesRoot, nativePath), nativeArtifact);
            }
        }

        var clientPath = Within(versionRoot, instance.VersionId + ".jar");
        RequireFile(clientPath, root.GetProperty("downloads").GetProperty("client"));
        classpath.Add(clientPath);

        var assetsRoot = Within(gameRoot, "assets");
        var assetIndex = root.GetProperty("assetIndex");
        var assetId = assetIndex.GetProperty("id").GetString()
            ?? throw new InvalidDataException("Asset index has no id.");
        var indexPath = Within(Path.Combine(assetsRoot, "indexes"), assetId + ".json");
        RequireFile(indexPath, assetIndex);
        using var indexDocument = await ReadJsonAsync(indexPath, cancellationToken);
        var index = indexDocument.RootElement;
        var legacyAssets = string.Equals(assetId, "legacy", StringComparison.OrdinalIgnoreCase)
            || (root.TryGetProperty("assets", out var assetsElement)
                && string.Equals(assetsElement.GetString(), "legacy", StringComparison.OrdinalIgnoreCase));
        var virtualAssets = legacyAssets
            || (index.TryGetProperty("virtual", out var virtualElement) && virtualElement.GetBoolean());
        var mapToResources = index.TryGetProperty("map_to_resources", out var resourcesElement)
            && resourcesElement.GetBoolean();
        var virtualRoot = virtualAssets
            ? Within(Path.Combine(assetsRoot, "virtual"), assetId) : assetsRoot;
        var resourceRoot = Within(gameRoot, "resources");
        var gameAssetsRoot = mapToResources
            ? resourceRoot
            : virtualAssets
                ? virtualRoot
                : assetsRoot;

        if (index.TryGetProperty("objects", out var objects))
        {
            foreach (var property in objects.EnumerateObject())
            {
                cancellationToken.ThrowIfCancellationRequested();
                var hash = property.Value.GetProperty("hash").GetString()
                    ?? throw new InvalidDataException("Asset has no hash.");
                if (!Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$"))
                    throw new InvalidDataException("Asset hash is invalid.");
                var objectPath = Within(Path.Combine(assetsRoot, "objects"),
                    Path.Combine(hash[..2], hash));
                RequireFile(objectPath, property.Value);
                if (virtualAssets)
                    CopyAsset(objectPath, Within(virtualRoot, property.Name));
                if (mapToResources)
                    CopyAsset(objectPath, Within(resourceRoot, property.Name));
            }
        }

        var launcherVersion = typeof(MinecraftLaunchPlanBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MinecraftLaunchPlanBuilder).Assembly.GetName().Version?.ToString()
            ?? "dev";

        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = account.DisplayName,
            ["auth_uuid"] = uuid.ToString("N"),
            ["auth_access_token"] = "0",
            ["auth_session"] = "0",
            ["user_type"] = "legacy",
            ["user_properties"] = "{}",
            ["clientid"] = "0",
            ["auth_xuid"] = "0",
            ["version_name"] = instance.VersionId,
            ["version_type"] = root.GetProperty("type").GetString() ?? "release",
            ["game_directory"] = gameRoot,
            ["assets_root"] = assetsRoot,
            ["assets_index_name"] = assetId,
            ["game_assets"] = gameAssetsRoot,
            ["natives_directory"] = nativesRoot,
            ["library_directory"] = librariesRoot,
            ["classpath"] = string.Join(Path.PathSeparator, classpath.Distinct(StringComparer.Ordinal)),
            ["classpath_separator"] = Path.PathSeparator.ToString(),
            ["launcher_name"] = "UN_Nexo",
            ["launcher_version"] = launcherVersion
        };

        string Expand(string value)
        {
            return PlaceholderRegex().Replace(value, match =>
                substitutions.TryGetValue(match.Groups[1].Value, out var replacement)
                    ? replacement
                    : throw new InvalidDataException($"Unsupported launch argument {match.Value}."));
        }

        var arguments = new List<string> { "-Xmx2G" };
        if (root.TryGetProperty("arguments", out var modernArguments)
            && modernArguments.TryGetProperty("jvm", out var jvmArguments))
        {
            arguments.AddRange(ReadArguments(jvmArguments).Select(Expand));
        }
        else
        {
            if (OperatingSystem.IsMacOS())
                arguments.Add("-XstartOnFirstThread");
            arguments.Add("-Djava.library.path=" + nativesRoot);
            arguments.Add("-cp");
            arguments.Add(substitutions["classpath"]);
        }

        if (root.TryGetProperty("logging", out var logging)
            && logging.TryGetProperty("client", out var clientLogging)
            && clientLogging.TryGetProperty("argument", out var logArgument)
            && clientLogging.TryGetProperty("file", out var logFile))
        {
            var configuredPath = Within(
                Path.Combine(assetsRoot, "log_configs"),
                logFile.GetProperty("id").GetString()!);
            if (File.Exists(configuredPath))
                arguments.Add(logArgument.GetString()!.Replace(
                    "${path}", configuredPath, StringComparison.Ordinal));
        }

        arguments.Add(root.GetProperty("mainClass").GetString()
            ?? throw new InvalidDataException("Missing main class."));
        if (root.TryGetProperty("arguments", out modernArguments)
            && modernArguments.TryGetProperty("game", out var gameArguments))
            arguments.AddRange(ReadArguments(gameArguments).Select(Expand));
        else if (root.TryGetProperty("minecraftArguments", out var legacyArguments))
            arguments.AddRange(SplitLegacy(legacyArguments.GetString() ?? string.Empty).Select(Expand));

        return new MinecraftLaunchPlan(
            java.JavaPath, gameRoot, arguments,
            Within(instanceRoot, "launcher-logs"));
    }

    public static int JavaMajor(string version)
    {
        var parts = version.Split('.', '-', '_', '+');
        if (!int.TryParse(parts[0], out var major))
            return 0;
        return major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy)
            ? legacy : major;
    }

    internal static string Within(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Invalid relative game path.");
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(
            directory, relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new InvalidDataException("Game metadata path escapes its directory.");
        return result;
    }

    private static void RequireFile(string file, JsonElement? metadata = null)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0
            || (metadata.HasValue && metadata.Value.TryGetProperty("size", out var size)
                && info.Length != size.GetInt64()))
            throw new FileNotFoundException(
                $"Missing or incomplete file: {file}. Run Prepare Vanilla files again.", file);
    }

    private static async Task<JsonDocument> ReadJsonAsync(string file, CancellationToken cancellationToken)
    {
        await using var stream = File.OpenRead(file);
        return await JsonDocument.ParseAsync(stream, cancellationToken: cancellationToken);
    }

    private static void CopyAsset(string source, string target)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        if (!File.Exists(target)
            || new FileInfo(source).Length != new FileInfo(target).Length)
            File.Copy(source, target, overwrite: true);
    }

    private static IEnumerable<string> ReadArguments(JsonElement list)
    {
        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                yield return item.GetString()!;
                continue;
            }

            if (!MinecraftRules.Allows(item))
                continue;

            var value = item.GetProperty("value");
            if (value.ValueKind == JsonValueKind.String)
                yield return value.GetString()!;
            else
                foreach (var part in value.EnumerateArray())
                    yield return part.GetString()!;
        }
    }

    private static IEnumerable<string> SplitLegacy(string text)
    {
        var current = new StringBuilder();
        char quote = '\0';
        foreach (var character in text)
        {
            if (character == quote)
            {
                quote = '\0';
                continue;
            }
            if (quote == '\0' && (character == '\'' || character == '"'))
            {
                quote = character;
                continue;
            }
            if (quote == '\0' && char.IsWhiteSpace(character))
            {
                if (current.Length > 0)
                {
                    yield return current.ToString();
                    current.Clear();
                }
            }
            else
                current.Append(character);
        }

        if (quote != '\0')
            throw new InvalidDataException("Unterminated quote in legacy game arguments.");
        if (current.Length > 0)
            yield return current.ToString();
    }

    [GeneratedRegex(@"\${([^}]+)\}")]
    private static partial Regex PlaceholderRegex();
}
