using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Launching;

public sealed partial class MinecraftLaunchPlanBuilder(NexoPathService paths)
{
    internal const long MaxLocalAssetIndexBytes = 64L * 1024 * 1024;
    public Task<MinecraftLaunchPlan> BuildAsync(
        GameInstance instance,
        LauncherAccount account,
        IEnumerable<JavaInstallation> installations,
        MinecraftLaunchCredentials? credentials,
        CancellationToken cancellationToken = default)
        => BuildForArchitectureAsync(
            instance,
            account,
            installations,
            credentials,
            RuntimeInformation.OSArchitecture,
            featureContext: null,
            cancellationToken);

    internal async Task<MinecraftLaunchPlan> BuildForArchitectureAsync(
        GameInstance instance,
        LauncherAccount account,
        IEnumerable<JavaInstallation> installations,
        MinecraftLaunchCredentials? credentials,
        Architecture architecture,
        IReadOnlyDictionary<string, bool>? featureContext = null,
        CancellationToken cancellationToken = default)
    {
        if (!instance.Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
            && !instance.Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase)
            && !instance.Loader.Equals("quilt", StringComparison.OrdinalIgnoreCase)
            && !instance.Loader.Equals("forge", StringComparison.OrdinalIgnoreCase)
            && !instance.Loader.Equals("neoforge", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Loader '{instance.Loader}' cannot be launched in this build yet.");

        string playerName;
        Guid uuid;
        string authAccessToken;
        string authSession;
        string userType;
        string clientId;
        string xuid;

        if (!account.IsMicrosoft)
            throw new InvalidDataException(
                "Minecraft launch requires an authenticated Microsoft profile.");

        if (credentials is null
            || !string.Equals(credentials.AccountId, account.Id, StringComparison.Ordinal)
            || !string.Equals(credentials.PlayerName, account.DisplayName, StringComparison.Ordinal)
            || !Guid.TryParse(account.Uuid, out uuid)
            || !Guid.TryParse(credentials.Uuid, out var credentialUuid)
            || credentialUuid != uuid
            || string.IsNullOrWhiteSpace(credentials.AccessToken)
            || !string.Equals(
                credentials.ClientId,
                MsalMicrosoftAccessTokenProvider.ClientId,
                StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(credentials.Xuid))
            throw new InvalidDataException(
                "The selected Microsoft profile does not have valid Minecraft launch credentials.");

        playerName = credentials.PlayerName;
        authAccessToken = credentials.AccessToken;
        authSession = $"token:{credentials.AccessToken}:{uuid:N}";
        userType = "msa";
        clientId = credentials.ClientId;
        xuid = credentials.Xuid;

        if (architecture is not (Architecture.X64 or Architecture.Arm64))
            throw new PlatformNotSupportedException(
                $"Minecraft launch supports x64 and ARM64, but this environment reports {architecture}.");

        var instanceRoot = Within(paths.GetInstancesRoot(), instance.Id);
        var gameRoot = Within(instanceRoot, "game");
        using var resolved = await new MinecraftVersionMetadataResolver()
            .ResolveAsync(gameRoot, instance.VersionId, cancellationToken);
        var root = resolved.Document.RootElement;
        ValidateResolvedMetadataForLaunch(root);
        if (!string.Equals(root.GetProperty("id").GetString(), instance.VersionId,
                StringComparison.Ordinal))
            throw new InvalidDataException("Instance and resolved version metadata do not match.");

        var requiredJava = root.TryGetProperty("javaVersion", out var javaVersion)
            && javaVersion.TryGetProperty("majorVersion", out var majorVersion)
            ? majorVersion.GetInt32()
            : 8;
        var java = installations
            .Where(item => item.Is64Bit && File.Exists(item.JavaPath))
            .OrderBy(item => JavaMajor(item.Version) == requiredJava ? 0 : 1)
            .FirstOrDefault(item => JavaMajor(item.Version) == requiredJava);
        if (java is null)
        {
            if (architecture != Architecture.X64)
                throw new PlatformNotSupportedException(
                    $"No compatible 64-bit Java {requiredJava} installation was found for {MinecraftRules.ArchitectureNameFor(architecture)}. "
                    + "Automatic Java acquisition currently supports x64 only; install a matching ARM64 Java runtime manually.");

            java = await new JavaRuntimeProvisionService(paths)
                .EnsureJavaAsync(requiredJava, cancellationToken: cancellationToken);
        }

        var librariesRoot = Within(gameRoot, "libraries");
        var nativesRoot = Within(gameRoot, Path.Combine("natives", resolved.ClientVersionId));
        Directory.CreateDirectory(nativesRoot);
        var classpath = new List<string>();
        var nativeArchives = new List<NativeArchiveExpectation>();

        if (root.TryGetProperty("libraries", out var libraries))
        {
            foreach (var library in libraries.EnumerateArray())
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!MinecraftRules.Allows(library, architecture))
                    continue;

                var artifactAdded = false;
                if (library.TryGetProperty("downloads", out var downloads)
                    && downloads.TryGetProperty("artifact", out var artifact))
                {
                    var artifactPath = artifact.GetProperty("path").GetString()
                        ?? throw new InvalidDataException("A library artifact has no path.");
                    var file = Within(librariesRoot, artifactPath);
                    await RequireFileAsync(file, artifact, cancellationToken);
                    classpath.Add(file);
                    artifactAdded = true;
                }

                if (!artifactAdded
                    && library.TryGetProperty("name", out var libraryName)
                    && !string.IsNullOrWhiteSpace(libraryName.GetString()))
                {
                    var relative = MavenArtifactPath.FromCoordinate(libraryName.GetString()!);
                    var file = Within(librariesRoot, relative);
                    RequireFile(file);
                    classpath.Add(file);
                }

                var classifier = MinecraftRules.NativeClassifier(library);
                if (classifier is null)
                    continue;
                if (!library.TryGetProperty("downloads", out downloads)
                    || !downloads.TryGetProperty("classifiers", out var classifiers)
                    || !classifiers.TryGetProperty(classifier, out var nativeArtifact))
                    throw new InvalidDataException("A native library is missing its classifier.");

                var nativePath = nativeArtifact.GetProperty("path").GetString()
                    ?? throw new InvalidDataException("A native library has no path.");
                var nativeArchivePath = Within(
                    librariesRoot,
                    nativePath);
                await RequireFileAsync(
                    nativeArchivePath,
                    nativeArtifact,
                    cancellationToken);

                var excludes = new List<string>
                {
                    "META-INF/"
                };
                if (library.TryGetProperty(
                        "extract",
                        out var extract)
                    && extract.ValueKind == JsonValueKind.Object
                    && extract.TryGetProperty(
                        "exclude",
                        out var excludeArray)
                    && excludeArray.ValueKind == JsonValueKind.Array)
                {
                    foreach (var excluded in excludeArray.EnumerateArray())
                    {
                        if (excluded.ValueKind == JsonValueKind.String
                            && !string.IsNullOrWhiteSpace(
                                excluded.GetString()))
                            excludes.Add(excluded.GetString()!);
                    }
                }

                nativeArchives.Add(
                    new NativeArchiveExpectation(
                        nativeArchivePath,
                        excludes));
            }
        }

        await ValidateExtractedNativesAsync(
            nativesRoot,
            nativeArchives,
            cancellationToken);

        var clientVersionRoot = Within(
            Path.Combine(gameRoot, "versions"),
            resolved.ClientVersionId);
        var clientPath = Within(clientVersionRoot, resolved.ClientVersionId + ".jar");
        if (!root.TryGetProperty("downloads", out var rootDownloads)
            || !rootDownloads.TryGetProperty("client", out var clientMetadata))
            throw new InvalidDataException("Resolved version metadata has no Minecraft client download.");
        await RequireFileAsync(
            clientPath,
            clientMetadata,
            cancellationToken);
        classpath.Add(clientPath);

        var assetsRoot = Within(gameRoot, "assets");
        if (!root.TryGetProperty("assetIndex", out var assetIndex))
            throw new InvalidDataException("Resolved version metadata has no asset index.");
        var assetId = MetadataPath.RequireSingleComponent(
            assetIndex.GetProperty("id").GetString(),
            "assetIndex.id");
        var indexPath = MetadataPath.ResolveSingleComponent(
            Path.Combine(assetsRoot, "indexes"),
            assetId,
            ".json",
            "assetIndex.id");
        await RequireFileAsync(
            indexPath,
            assetIndex,
            cancellationToken);
        using var indexDocument = await ReadJsonAsync(indexPath, cancellationToken);
        var index = indexDocument.RootElement;
        ValidateAssetIndexForLaunch(index);
        var legacyAssets = string.Equals(assetId, "legacy", StringComparison.OrdinalIgnoreCase)
            || (root.TryGetProperty("assets", out var assetsElement)
                && string.Equals(assetsElement.GetString(), "legacy", StringComparison.OrdinalIgnoreCase));
        var virtualAssets = legacyAssets
            || (index.TryGetProperty("virtual", out var virtualElement) && virtualElement.GetBoolean());
        var mapToResources = index.TryGetProperty("map_to_resources", out var resourcesElement)
            && resourcesElement.GetBoolean();
        var virtualRoot = virtualAssets
            ? MetadataPath.ResolveSingleComponent(
                Path.Combine(assetsRoot, "virtual"),
                assetId,
                string.Empty,
                "assetIndex.id")
            : assetsRoot;
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
                var objectPath = Within(
                    Path.Combine(assetsRoot, "objects"),
                    Path.Combine(hash[..2], hash));
                await RequireAssetObjectAsync(
                    objectPath,
                    property.Value,
                    hash,
                    cancellationToken);
                if (virtualAssets)
                    await CopyAssetAsync(
                        objectPath,
                        Within(virtualRoot, property.Name),
                        cancellationToken);
                if (mapToResources)
                    await CopyAssetAsync(
                        objectPath,
                        Within(resourceRoot, property.Name),
                        cancellationToken);
            }
        }

        var launcherVersion = typeof(MinecraftLaunchPlanBuilder).Assembly
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? typeof(MinecraftLaunchPlanBuilder).Assembly.GetName().Version?.ToString()
            ?? "dev";
        var versionType = root.TryGetProperty("type", out var typeElement)
            ? typeElement.GetString() ?? "release"
            : "release";

        var substitutions = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["auth_player_name"] = playerName,
            ["auth_uuid"] = uuid.ToString("N"),
            ["auth_access_token"] = authAccessToken,
            ["auth_session"] = authSession,
            ["user_type"] = userType,
            ["user_properties"] = "{}",
            ["clientid"] = clientId,
            ["auth_xuid"] = xuid,
            ["version_name"] = resolved.LaunchVersionId,
            ["version_type"] = versionType,
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
            arguments.AddRange(
                ReadArguments(jvmArguments, architecture, featureContext)
                    .Select(Expand));
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
            && clientLogging.TryGetProperty("file", out var logFile))
        {
            var logId = MetadataPath.RequireSingleComponent(
                logFile.GetProperty("id").GetString(),
                "logging.client.file.id");
            var configuredPath = MetadataPath.ResolveSingleComponent(
                Path.Combine(assetsRoot, "log_configs"),
                logId,
                string.Empty,
                "logging.client.file.id");
            await RequireFileAsync(
                configuredPath,
                logFile,
                cancellationToken);

            if (clientLogging.TryGetProperty(
                    "argument",
                    out var logArgument))
            {
                var argument = logArgument.GetString()
                    ?? throw InvalidMetadata(
                        "logging.client.argument",
                        "a string");
                arguments.Add(argument.Replace(
                    "${path}",
                    configuredPath,
                    StringComparison.Ordinal));
            }
        }

        arguments.Add(root.GetProperty("mainClass").GetString()
            ?? throw new InvalidDataException("Missing main class."));
        if (root.TryGetProperty("arguments", out modernArguments)
            && modernArguments.TryGetProperty("game", out var gameArguments))
            arguments.AddRange(
                ReadArguments(gameArguments, architecture, featureContext)
                    .Select(Expand));
        else if (root.TryGetProperty("minecraftArguments", out var legacyArguments))
            arguments.AddRange(SplitLegacy(legacyArguments.GetString() ?? string.Empty).Select(Expand));

        return new MinecraftLaunchPlan(
            java.JavaPath,
            gameRoot,
            arguments,
            Within(instanceRoot, "launcher-logs"))
        {
            InstanceId = instance.Id
        };
    }

    public static int JavaMajor(string version)
    {
        var parts = version.Split('.', '-', '_', '+');
        if (!int.TryParse(parts[0], out var major))
            return 0;
        return major == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy)
            ? legacy
            : major;
    }

    internal static string Within(string directory, string relative)
    {
        if (string.IsNullOrWhiteSpace(relative) || Path.IsPathRooted(relative))
            throw new InvalidDataException("Invalid relative game path.");
        var root = Path.GetFullPath(directory) + Path.DirectorySeparatorChar;
        var result = Path.GetFullPath(Path.Combine(
            directory,
            relative.Replace('/', Path.DirectorySeparatorChar)));
        if (!result.StartsWith(root, OperatingSystem.IsWindows()
                ? StringComparison.OrdinalIgnoreCase
                : StringComparison.Ordinal))
            throw new InvalidDataException("Game metadata path escapes its directory.");
        return result;
    }

    private static void ValidateResolvedMetadataForLaunch(JsonElement root)
    {
        RequireObject(root, "root");
        RequireString(root, "id", required: true);

        if (root.TryGetProperty("javaVersion", out var javaVersion))
        {
            RequireObject(javaVersion, "javaVersion");
            if (javaVersion.TryGetProperty("majorVersion", out var majorVersion)
                && (majorVersion.ValueKind != JsonValueKind.Number || !majorVersion.TryGetInt32(out _)))
                throw InvalidMetadata("javaVersion.majorVersion", "an integer");
        }

        if (root.TryGetProperty("libraries", out var libraries))
        {
            if (libraries.ValueKind != JsonValueKind.Array)
                throw InvalidMetadata("libraries", "an array");

            var index = 0;
            foreach (var library in libraries.EnumerateArray())
            {
                RequireObject(library, $"libraries[{index}]");
                MinecraftRules.Allows(library);
                _ = MinecraftRules.NativeClassifier(library);
                RequireString(library, "name", required: false);

                if (library.TryGetProperty("downloads", out var downloads))
                {
                    RequireObject(downloads, $"libraries[{index}].downloads");
                    if (downloads.TryGetProperty("artifact", out var artifact))
                        ValidateArtifact(artifact, $"libraries[{index}].downloads.artifact");
                    if (downloads.TryGetProperty("classifiers", out var classifiers))
                    {
                        RequireObject(classifiers, $"libraries[{index}].downloads.classifiers");
                        foreach (var classifier in classifiers.EnumerateObject())
                            ValidateArtifact(
                                classifier.Value,
                                $"libraries[{index}].downloads.classifiers.{classifier.Name}");
                    }
                }

                index++;
            }
        }

        if (root.TryGetProperty("downloads", out var rootDownloads))
        {
            RequireObject(rootDownloads, "downloads");
            if (rootDownloads.TryGetProperty("client", out var client))
                RequireObject(client, "downloads.client");
        }

        if (root.TryGetProperty("assetIndex", out var assetIndex))
        {
            RequireObject(assetIndex, "assetIndex");
            RequireString(assetIndex, "id", required: true);
        }

        RequireString(root, "assets", required: false);
        RequireString(root, "type", required: false);
        RequireString(root, "minecraftArguments", required: false);
        RequireString(root, "mainClass", required: true);

        if (root.TryGetProperty("arguments", out var arguments))
        {
            RequireObject(arguments, "arguments");
            if (arguments.TryGetProperty("jvm", out var jvm))
                ValidateArguments(jvm, "arguments.jvm");
            if (arguments.TryGetProperty("game", out var game))
                ValidateArguments(game, "arguments.game");
        }

        if (root.TryGetProperty("logging", out var logging))
        {
            RequireObject(logging, "logging");
            if (logging.TryGetProperty("client", out var clientLogging))
            {
                RequireObject(clientLogging, "logging.client");
                RequireString(clientLogging, "argument", required: false);
                if (clientLogging.TryGetProperty("file", out var file))
                {
                    RequireObject(file, "logging.client.file");
                    var id = RequireString(
                        file,
                        "id",
                        required: true);
                    _ = MetadataPath.RequireSingleComponent(
                        id,
                        "logging.client.file.id");
                    RequireString(
                        file,
                        "url",
                        required: false);
                    if (file.TryGetProperty("sha1", out var sha1))
                    {
                        if (sha1.ValueKind != JsonValueKind.String
                            || string.IsNullOrWhiteSpace(sha1.GetString())
                            || !Regex.IsMatch(
                                sha1.GetString()!,
                                "^[a-fA-F0-9]{40}$"))
                            throw InvalidMetadata(
                                "logging.client.file.sha1",
                                "a 40-character hexadecimal SHA-1");
                    }
                    if (file.TryGetProperty("size", out var size)
                        && (size.ValueKind != JsonValueKind.Number
                            || !size.TryGetInt64(out var parsedSize)
                            || parsedSize <= 0))
                        throw InvalidMetadata(
                            "logging.client.file.size",
                            "a positive integer");
                }
            }
        }
    }

    private static void ValidateAssetIndexForLaunch(JsonElement root)
    {
        RequireObject(root, "asset index root");
        if (!root.TryGetProperty("objects", out var objects)
            || objects.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata("asset index objects", "an object");

        RequireBoolean(root, "virtual");
        RequireBoolean(root, "map_to_resources");

        foreach (var property in objects.EnumerateObject())
        {
            RequireObject(property.Value, $"asset '{property.Name}'");
            var hash = RequireString(property.Value, "hash", required: true);
            if (hash is null || !Regex.IsMatch(hash, "^[a-fA-F0-9]{40}$"))
                throw new InvalidDataException(
                    $"Minecraft metadata property 'asset {property.Name}.hash' must be a 40-character hexadecimal SHA-1.");
        }
    }

    private static void ValidateArtifact(JsonElement artifact, string name)
    {
        RequireObject(artifact, name);
        RequireString(artifact, "path", required: true);
        if (artifact.TryGetProperty("size", out var size)
            && (size.ValueKind != JsonValueKind.Number || !size.TryGetInt64(out var parsed) || parsed < 0))
            throw InvalidMetadata($"{name}.size", "a non-negative integer");
    }

    private static void ValidateArguments(JsonElement list, string name)
    {
        if (list.ValueKind != JsonValueKind.Array)
            throw InvalidMetadata(name, "an array");

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
                continue;
            if (item.ValueKind != JsonValueKind.Object)
                throw InvalidMetadata($"{name}[]", "a string or object");

            MinecraftRules.Allows(item);
            if (!item.TryGetProperty("value", out var value))
                throw InvalidMetadata($"{name}[].value", "a string or array of strings");
            if (value.ValueKind == JsonValueKind.String)
                continue;
            if (value.ValueKind != JsonValueKind.Array)
                throw InvalidMetadata($"{name}[].value", "a string or array of strings");
            foreach (var part in value.EnumerateArray())
                if (part.ValueKind != JsonValueKind.String)
                    throw InvalidMetadata($"{name}[].value[]", "a string");
        }
    }

    private static void RequireObject(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata(name, "an object");
    }

    private static string? RequireString(JsonElement element, string propertyName, bool required)
    {
        if (!element.TryGetProperty(propertyName, out var value))
        {
            if (required)
                throw InvalidMetadata(propertyName, "a string");
            return null;
        }

        if (value.ValueKind != JsonValueKind.String)
            throw InvalidMetadata(propertyName, "a string");
        return value.GetString();
    }

    private static void RequireBoolean(JsonElement element, string propertyName)
    {
        if (!element.TryGetProperty(propertyName, out var value))
            return;
        if (value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw InvalidMetadata(propertyName, "a boolean");
    }

    private static InvalidDataException InvalidMetadata(string propertyName, string expected)
        => new($"Minecraft metadata property '{propertyName}' must be {expected}.");

    private static void RequireFile(string file)
    {
        var info = new FileInfo(file);
        if (!info.Exists || info.Length == 0)
            throw MissingOrCorrupt(file);
    }

    private static async Task RequireFileAsync(
        string file,
        JsonElement metadata,
        CancellationToken cancellationToken)
    {
        if (metadata.ValueKind != JsonValueKind.Object)
            throw InvalidMetadata("file metadata", "an object");

        long? expectedSize = null;
        if (metadata.TryGetProperty("size", out var size))
        {
            if (size.ValueKind != JsonValueKind.Number
                || !size.TryGetInt64(out var parsedSize)
                || parsedSize < 0)
                throw InvalidMetadata("size", "a non-negative integer");
            expectedSize = parsedSize;
        }

        string? expectedSha1 = null;
        if (metadata.TryGetProperty("sha1", out var sha1))
        {
            if (sha1.ValueKind != JsonValueKind.String)
                throw InvalidMetadata("sha1", "a string");
            expectedSha1 = sha1.GetString();
            if (string.IsNullOrWhiteSpace(expectedSha1)
                || !Regex.IsMatch(
                    expectedSha1,
                    "^[a-fA-F0-9]{40}$"))
                throw InvalidMetadata(
                    "sha1",
                    "a 40-character hexadecimal SHA-1");
        }

        var info = new FileInfo(file);
        if (!info.Exists
            || info.Length == 0
            || (expectedSize.HasValue
                && info.Length != expectedSize.Value))
            throw MissingOrCorrupt(file);

        if (expectedSha1 is not null)
        {
            var actual = await ComputeSha1Async(
                file,
                cancellationToken);
            if (!actual.Equals(
                    expectedSha1,
                    StringComparison.OrdinalIgnoreCase))
                throw MissingOrCorrupt(file);
        }
    }

    private static async Task ValidateExtractedNativesAsync(
        string nativesRoot,
        IReadOnlyList<NativeArchiveExpectation> archives,
        CancellationToken cancellationToken)
    {
        if (archives.Count == 0)
            return;

        var expected = new Dictionary<string, string>(
            OperatingSystem.IsWindows()
                ? StringComparer.OrdinalIgnoreCase
                : StringComparer.Ordinal);

        foreach (var native in archives)
        {
            cancellationToken.ThrowIfCancellationRequested();
            using var archive = ZipFile.OpenRead(
                native.ArchivePath);
            foreach (var entry in archive.Entries)
            {
                var relative = ValidateNativeRelativePath(
                    entry.FullName);
                if (IsZipSymlink(entry))
                    throw new InvalidDataException(
                        $"Native archive contains a symbolic-link entry: {entry.FullName}");

                if (string.IsNullOrWhiteSpace(entry.Name)
                    || entry.FullName.EndsWith(
                        "/",
                        StringComparison.Ordinal)
                    || native.Excludes.Any(prefix =>
                        relative.StartsWith(
                            prefix.Replace('\\', '/'),
                            StringComparison.OrdinalIgnoreCase)))
                    continue;

                await using var stream = entry.Open();
                expected[relative] =
                    await ComputeStreamSha1Async(
                        stream,
                        cancellationToken);
            }
        }

        var actual = EnumerateNativeFilesSafe(
                nativesRoot)
            .ToDictionary(
                item => item.Relative,
                item => item.FullPath,
                OperatingSystem.IsWindows()
                    ? StringComparer.OrdinalIgnoreCase
                    : StringComparer.Ordinal);

        foreach (var item in expected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!actual.TryGetValue(
                    item.Key,
                    out var fullPath))
                throw MissingOrCorrupt(
                    Within(
                        nativesRoot,
                        item.Key));

            var digest = await ComputeSha1Async(
                fullPath,
                cancellationToken);
            if (!digest.Equals(
                    item.Value,
                    StringComparison.OrdinalIgnoreCase))
                throw MissingOrCorrupt(fullPath);
        }

        foreach (var item in actual)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!expected.ContainsKey(item.Key))
                throw new FileNotFoundException(
                    $"Stale extracted native file is not declared by the verified native archives: {item.Value}. Run Prepare instance files again.",
                    item.Value);
        }
    }

    private static IEnumerable<(string Relative, string FullPath)>
        EnumerateNativeFilesSafe(string root)
    {
        if (!Directory.Exists(root))
            yield break;

        if ((File.GetAttributes(root)
             & FileAttributes.ReparsePoint) != 0)
            throw new InvalidDataException(
                $"Native runtime directory is a symbolic link/reparse point: {root}");

        foreach (var file in Directory.EnumerateFiles(root))
        {
            if ((File.GetAttributes(file)
                 & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    $"Native runtime file is a symbolic link/reparse point: {file}");

            yield return (
                Path.GetRelativePath(root, file)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/'),
                file);
        }

        foreach (var directory in Directory.EnumerateDirectories(root))
        {
            if ((File.GetAttributes(directory)
                 & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException(
                    $"Native runtime directory is a symbolic link/reparse point: {directory}");

            foreach (var file
                     in EnumerateNativeFilesSafe(directory))
            {
                var relative = Path.GetRelativePath(
                        root,
                        file.FullPath)
                    .Replace(
                        Path.DirectorySeparatorChar,
                        '/');
                yield return (
                    relative,
                    file.FullPath);
            }
        }
    }

    private static string ValidateNativeRelativePath(
        string value)
    {
        var normalized = value.Replace('\\', '/');
        if (string.IsNullOrWhiteSpace(normalized)
            || normalized.StartsWith(
                "/",
                StringComparison.Ordinal)
            || Path.IsPathRooted(normalized)
            || normalized.Any(char.IsControl))
            throw new InvalidDataException(
                $"Unsafe native archive entry: {value}");

        var parts = normalized.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0
            || parts.Any(part =>
                part is "." or ".."))
            throw new InvalidDataException(
                $"Unsafe native archive entry: {value}");

        return string.Join('/', parts);
    }

    private static bool IsZipSymlink(
        ZipArchiveEntry entry)
    {
        var unixMode =
            (entry.ExternalAttributes >> 16) & 0xF000;
        return unixMode == 0xA000;
    }

    private static async Task<string> ComputeStreamSha1Async(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var sha1 = SHA1.Create();
        var digest = await sha1.ComputeHashAsync(
            stream,
            cancellationToken);
        return Convert.ToHexString(digest)
            .ToLowerInvariant();
    }

    private static async Task RequireAssetObjectAsync(
        string file,
        JsonElement metadata,
        string expectedSha1,
        CancellationToken cancellationToken)
    {
        long? expectedSize = null;
        if (metadata.TryGetProperty("size", out var size))
        {
            if (size.ValueKind != JsonValueKind.Number
                || !size.TryGetInt64(out var parsedSize)
                || parsedSize < 0)
                throw InvalidMetadata(
                    "asset size",
                    "a non-negative integer");
            expectedSize = parsedSize;
        }

        var info = new FileInfo(file);
        if (!info.Exists
            || info.Length == 0
            || (expectedSize.HasValue
                && info.Length != expectedSize.Value))
            throw MissingOrCorrupt(file);

        var actual = await ComputeSha1Async(
            file,
            cancellationToken);
        if (!actual.Equals(
                expectedSha1,
                StringComparison.OrdinalIgnoreCase))
            throw MissingOrCorrupt(file);
    }

    private static FileNotFoundException MissingOrCorrupt(
        string file)
        => new(
            $"Missing, incomplete or corrupted file: {file}. Run Prepare instance files again.",
            file);

    private static async Task<string> ComputeSha1Async(
        string file,
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            file,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            128 * 1024,
            FileOptions.Asynchronous
            | FileOptions.SequentialScan);
        using var sha1 = SHA1.Create();
        var digest = await sha1.ComputeHashAsync(
            stream,
            cancellationToken);
        return Convert.ToHexString(digest)
            .ToLowerInvariant();
    }

    private static async Task<JsonDocument> ReadJsonAsync(
        string file,
        CancellationToken cancellationToken)
    {
        try
        {
            var bytes = await BoundedLocalFile.ReadAllBytesAsync(
                file,
                MaxLocalAssetIndexBytes,
                "Local Minecraft asset index",
                cancellationToken);
            return JsonDocument.Parse(bytes);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException(
                $"Minecraft metadata JSON is malformed: {file}",
                ex);
        }
    }

    private static async Task CopyAssetAsync(
        string source,
        string target,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(
            Path.GetDirectoryName(target)!);

        if (File.Exists(target))
        {
            var sourceHash = await ComputeSha1Async(
                source,
                cancellationToken);
            var targetHash = await ComputeSha1Async(
                target,
                cancellationToken);
            if (sourceHash.Equals(
                    targetHash,
                    StringComparison.OrdinalIgnoreCase))
                return;
        }

        var temporaryPath =
            target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await using (var input = new FileStream(
                             source,
                             FileMode.Open,
                             FileAccess.Read,
                             FileShare.Read,
                             128 * 1024,
                             FileOptions.Asynchronous
                             | FileOptions.SequentialScan))
            await using (var output = new FileStream(
                             temporaryPath,
                             FileMode.CreateNew,
                             FileAccess.Write,
                             FileShare.None,
                             128 * 1024,
                             FileOptions.Asynchronous
                             | FileOptions.WriteThrough))
            {
                await input.CopyToAsync(
                    output,
                    cancellationToken);
                await output.FlushAsync(
                    cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();
            File.Move(
                temporaryPath,
                target,
                overwrite: true);
        }
        finally
        {
            try
            {
                if (File.Exists(temporaryPath))
                    File.Delete(temporaryPath);
            }
            catch
            {
            }
        }
    }

    private sealed record NativeArchiveExpectation(
        string ArchivePath,
        IReadOnlyList<string> Excludes);

    private static IEnumerable<string> ReadArguments(
        JsonElement list,
        Architecture architecture,
        IReadOnlyDictionary<string, bool>? featureContext)
    {
        if (list.ValueKind != JsonValueKind.Array)
            throw InvalidMetadata("launch arguments", "an array");

        foreach (var item in list.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                yield return item.GetString()!;
                continue;
            }

            if (item.ValueKind != JsonValueKind.Object)
                throw InvalidMetadata("launch argument item", "a string or object");
            if (!MinecraftRules.Allows(item, architecture, featureContext))
                continue;
            if (!item.TryGetProperty("value", out var value))
                throw InvalidMetadata("launch argument value", "a string or array of strings");

            if (value.ValueKind == JsonValueKind.String)
            {
                yield return value.GetString()!;
                continue;
            }

            if (value.ValueKind != JsonValueKind.Array)
                throw InvalidMetadata("launch argument value", "a string or array of strings");

            foreach (var part in value.EnumerateArray())
            {
                if (part.ValueKind != JsonValueKind.String)
                    throw InvalidMetadata("launch argument value[]", "a string");
                yield return part.GetString()!;
            }
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
            {
                current.Append(character);
            }
        }

        if (quote != '\0')
            throw new InvalidDataException("Unterminated quote in legacy game arguments.");
        if (current.Length > 0)
            yield return current.ToString();
    }

    [GeneratedRegex(@"\${([^}]+)\}")]
    private static partial Regex PlaceholderRegex();
}
