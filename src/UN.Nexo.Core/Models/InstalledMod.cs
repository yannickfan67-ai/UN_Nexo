namespace UN.Nexo.Core.Models;

public sealed record InstalledMod(
    string FileName,
    bool IsEnabled,
    long SizeBytes,
    DateTimeOffset LastModified)
{
    private const string DisabledSuffix = ".disabled";

    public string DisplayName => IsEnabled
        ? FileName
        : FileName.EndsWith(DisabledSuffix, StringComparison.OrdinalIgnoreCase)
            ? FileName[..^DisabledSuffix.Length]
            : FileName;

    public string StatusLabel => IsEnabled ? "Enabled" : "Disabled";

    public string SizeLabel => SizeBytes switch
    {
        < 1024 => $"{SizeBytes} B",
        < 1024 * 1024 => $"{SizeBytes / 1024d:0.0} KiB",
        _ => $"{SizeBytes / (1024d * 1024d):0.0} MiB"
    };
}
