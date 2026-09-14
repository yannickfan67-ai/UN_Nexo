namespace UN.Nexo.Core.Models;

public sealed record ImportVersionCandidate(
    string VersionId,
    string Loader,
    string BaseVersionId,
    string MetadataPath,
    bool HasClientJar,
    bool IsSupportedLoader,
    string Detail)
{
    public string DisplayName => Loader.Equals("vanilla", StringComparison.OrdinalIgnoreCase)
        ? VersionId
        : $"{VersionId} · {Loader} · Minecraft {BaseVersionId}";

    public string SupportLabel => IsSupportedLoader
        ? "Launchable in this build"
        : Loader.Equals("fabric", StringComparison.OrdinalIgnoreCase)
            ? "Importable and launchable after Fabric preparation"
            : Loader.Equals("forge", StringComparison.OrdinalIgnoreCase)
                ? "Importable; Forge launch support is still in progress"
                : "Importable; loader launch support is not available yet";
}

public sealed record GameDirectoryImportPreview(
    string SourceDirectory,
    IReadOnlyList<ImportVersionCandidate> Versions,
    int WorldCount,
    int ModCount,
    int ResourcePackCount,
    int ShaderPackCount,
    long SelectedContentBytes,
    IReadOnlyList<string> Warnings)
{
    public bool HasVersions => Versions.Count > 0;
    public string ContentSummary =>
        $"{WorldCount} world{(WorldCount == 1 ? string.Empty : "s")} · " +
        $"{ModCount} mod{(ModCount == 1 ? string.Empty : "s")} · " +
        $"{ResourcePackCount} resource pack{(ResourcePackCount == 1 ? string.Empty : "s")} · " +
        $"{ShaderPackCount} shader pack{(ShaderPackCount == 1 ? string.Empty : "s")}";
    public string SizeLabel => FormatBytes(SelectedContentBytes);

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var unit = 0;
        var display = (double)Math.Max(0, value);
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.##} {units[unit]}";
    }
}

public sealed record GameDirectoryImportResult(
    GameInstance Instance,
    bool PreparedFromExistingFiles,
    long CopiedBytes,
    int CopiedFiles,
    IReadOnlyList<string> Warnings);
