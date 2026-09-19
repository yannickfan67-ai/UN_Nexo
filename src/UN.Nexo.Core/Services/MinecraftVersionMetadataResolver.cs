using System.Text.Json;
using System.Text.Json.Nodes;

namespace UN.Nexo.Core.Services;

public sealed class ResolvedMinecraftVersion : IDisposable
{
    public ResolvedMinecraftVersion(JsonDocument document, string launchVersionId, string clientVersionId)
    {
        Document = document;
        LaunchVersionId = launchVersionId;
        ClientVersionId = clientVersionId;
    }

    public JsonDocument Document { get; }
    public string LaunchVersionId { get; }
    public string ClientVersionId { get; }

    public void Dispose() => Document.Dispose();
}

public sealed class MinecraftVersionMetadataResolver
{
    private const int MaxInheritanceDepth = 8;

    public async Task<ResolvedMinecraftVersion> ResolveAsync(
        string gameRoot,
        string launchVersionId,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(gameRoot))
            throw new ArgumentException("Game root is required.", nameof(gameRoot));
        ValidateVersionId(launchVersionId);

        var versionsRoot = Path.Combine(Path.GetFullPath(gameRoot), "versions");
        var visited = new HashSet<string>(StringComparer.Ordinal);
        var resolved = await ResolveNodeAsync(
            versionsRoot,
            launchVersionId,
            visited,
            0,
            cancellationToken);
        var document = JsonDocument.Parse(resolved.Metadata.ToJsonString());
        return new ResolvedMinecraftVersion(document, launchVersionId, resolved.ClientVersionId);
    }

    private async Task<ResolvedNode> ResolveNodeAsync(
        string versionsRoot,
        string versionId,
        HashSet<string> visited,
        int depth,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ValidateVersionId(versionId);
        if (depth > MaxInheritanceDepth)
            throw new InvalidDataException("Minecraft version inheritance is too deep.");
        if (!visited.Add(versionId))
            throw new InvalidDataException($"Minecraft version inheritance loop detected at {versionId}.");

        try
        {
            var metadataPath = Path.Combine(versionsRoot, versionId, versionId + ".json");
            if (!File.Exists(metadataPath))
                throw new FileNotFoundException($"Version metadata is missing: {metadataPath}", metadataPath);

            await using var stream = new FileStream(
                metadataPath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.Read,
                64 * 1024,
                FileOptions.Asynchronous | FileOptions.SequentialScan);
            var node = await JsonNode.ParseAsync(stream, cancellationToken: cancellationToken)
                ?? throw new InvalidDataException($"Version metadata is empty: {versionId}");
            if (node is not JsonObject child)
                throw new InvalidDataException($"Version metadata root must be an object: {versionId}");

            var declaredId = RequireStringProperty(
                child,
                "id",
                versionId);
            if (!string.Equals(declaredId, versionId, StringComparison.Ordinal))
                throw new InvalidDataException($"Version metadata id '{declaredId}' does not match directory '{versionId}'.");

            var inheritsFrom = OptionalStringProperty(
                child,
                "inheritsFrom",
                versionId);
            if (string.IsNullOrWhiteSpace(inheritsFrom))
            {
                var clientVersion = HasClientDownload(child) ? versionId : versionId;
                return new ResolvedNode((JsonObject)child.DeepClone(), clientVersion);
            }

            ValidateVersionId(inheritsFrom);
            var parent = await ResolveNodeAsync(
                versionsRoot,
                inheritsFrom,
                visited,
                depth + 1,
                cancellationToken);
            var merged = Merge(parent.Metadata, child);
            var clientVersionId = HasClientDownload(child) ? versionId : parent.ClientVersionId;
            return new ResolvedNode(merged, clientVersionId);
        }
        finally
        {
            visited.Remove(versionId);
        }
    }

    private static JsonObject Merge(JsonObject parent, JsonObject child)
    {
        var result = (JsonObject)parent.DeepClone();
        foreach (var property in child)
        {
            if (property.Key.Equals("libraries", StringComparison.Ordinal)
                && property.Value is JsonArray childLibraries)
            {
                result[property.Key] = MergeLibraries(
                    result[property.Key] as JsonArray,
                    childLibraries);
                continue;
            }

            if (property.Key.Equals("arguments", StringComparison.Ordinal)
                && property.Value is JsonObject childArguments)
            {
                result[property.Key] = MergeArguments(
                    result[property.Key] as JsonObject,
                    childArguments);
                continue;
            }

            result[property.Key] = property.Value?.DeepClone();
        }
        return result;
    }

    private static JsonArray MergeLibraries(JsonArray? parent, JsonArray child)
    {
        var items = new List<JsonNode?>();
        var indexes = new Dictionary<string, int>(StringComparer.Ordinal);

        void Add(JsonNode? value, bool replace)
        {
            var clone = value?.DeepClone();
            var key = clone is JsonObject obj
                ? OptionalStringProperty(obj, "name", "library entry")
                : null;
            if (!string.IsNullOrWhiteSpace(key) && indexes.TryGetValue(key, out var index))
            {
                if (replace)
                    items[index] = clone;
                return;
            }
            if (!string.IsNullOrWhiteSpace(key))
                indexes[key] = items.Count;
            items.Add(clone);
        }

        if (parent is not null)
            foreach (var item in parent)
                Add(item, replace: false);
        foreach (var item in child)
            Add(item, replace: true);

        var result = new JsonArray();
        foreach (var item in items)
            result.Add(item);
        return result;
    }

    private static JsonObject MergeArguments(JsonObject? parent, JsonObject child)
    {
        var result = parent is null ? new JsonObject() : (JsonObject)parent.DeepClone();
        foreach (var property in child)
        {
            if ((property.Key.Equals("game", StringComparison.Ordinal)
                 || property.Key.Equals("jvm", StringComparison.Ordinal))
                && property.Value is JsonArray childArray)
            {
                var combined = new JsonArray();
                if (result[property.Key] is JsonArray parentArray)
                    foreach (var item in parentArray)
                        combined.Add(item?.DeepClone());
                foreach (var item in childArray)
                    combined.Add(item?.DeepClone());
                result[property.Key] = combined;
            }
            else
            {
                result[property.Key] = property.Value?.DeepClone();
            }
        }
        return result;
    }

    private static string RequireStringProperty(
        JsonObject metadata,
        string propertyName,
        string context)
    {
        if (!metadata.TryGetPropertyValue(propertyName, out var node)
            || node is null)
            throw InvalidTypedProperty(context, propertyName, "a string");

        if (node is JsonValue value
            && value.TryGetValue<string>(out var text)
            && text is not null)
            return text;

        throw InvalidTypedProperty(context, propertyName, "a string");
    }

    private static string? OptionalStringProperty(
        JsonObject metadata,
        string propertyName,
        string context)
    {
        if (!metadata.TryGetPropertyValue(propertyName, out var node)
            || node is null)
            return null;

        if (node is JsonValue value
            && value.TryGetValue<string>(out var text))
            return text;

        throw InvalidTypedProperty(context, propertyName, "a string");
    }

    private static InvalidDataException InvalidTypedProperty(
        string context,
        string propertyName,
        string expected)
        => new($"Version metadata '{context}' property '{propertyName}' must be {expected}.");

    private static bool HasClientDownload(JsonObject metadata)
        => metadata["downloads"] is JsonObject downloads
           && downloads["client"] is JsonObject;

    private static void ValidateVersionId(string value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || Path.IsPathRooted(value)
            || value.Contains('/')
            || value.Contains('\\')
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"Unsafe Minecraft version id: {value}");
    }

    private sealed record ResolvedNode(JsonObject Metadata, string ClientVersionId);
}
