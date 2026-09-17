using System.Collections.Concurrent;

namespace UN.Nexo.Core.Services;

internal static class SettingsSaveGate
{
    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates = new(
        OperatingSystem.IsWindows() ? StringComparer.OrdinalIgnoreCase : StringComparer.Ordinal);

    public static SemaphoreSlim ForPath(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var canonicalPath = Path.GetFullPath(path);
        return Gates.GetOrAdd(canonicalPath, static _ => new SemaphoreSlim(1, 1));
    }
}
