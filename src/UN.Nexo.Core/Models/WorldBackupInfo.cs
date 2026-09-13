namespace UN.Nexo.Core.Models;

public sealed record WorldBackupWorld(
    string Name,
    string ArchivePrefix,
    long UncompressedBytes,
    int FileCount)
{
    public string SizeLabel => FormatBytes(UncompressedBytes);

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = Math.Max(0, value);
        var unit = 0;
        var display = (double)size;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.##} {units[unit]}";
    }
}

public sealed record WorldBackupInfo(
    string Id,
    string InstanceId,
    string InstanceName,
    string Kind,
    DateTimeOffset CreatedAt,
    string FilePath,
    long ArchiveBytes,
    IReadOnlyList<WorldBackupWorld> Worlds)
{
    public string DisplayName => $"{CreatedAt.LocalDateTime:yyyy-MM-dd HH:mm:ss} · {Kind}";
    public string WorldsSummary => Worlds.Count == 0
        ? "No worlds"
        : string.Join(", ", Worlds.Select(world => world.Name));
    public string ArchiveSizeLabel => FormatBytes(ArchiveBytes);

    private static string FormatBytes(long value)
    {
        string[] units = ["B", "KiB", "MiB", "GiB", "TiB"];
        var size = Math.Max(0, value);
        var unit = 0;
        var display = (double)size;
        while (display >= 1024 && unit < units.Length - 1)
        {
            display /= 1024;
            unit++;
        }
        return $"{display:0.##} {units[unit]}";
    }
}

public sealed record WorldRestoreResult(
    string WorldName,
    string? SafetyCopyPath,
    DateTimeOffset RestoredAt);
