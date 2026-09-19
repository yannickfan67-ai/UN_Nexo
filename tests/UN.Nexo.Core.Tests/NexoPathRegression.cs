using System.Runtime.CompilerServices;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class NexoPathRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        var root = Path.Combine(Path.GetTempPath(), "nexo-path-regression", Guid.NewGuid().ToString("N"));
        var paths = new NexoPathService(root);
        var id = Guid.NewGuid().ToString("N");
        var resolved = paths.GetInstanceDirectory(id);
        var expected = Path.Combine(Path.GetFullPath(root), "instances", id);
        if (!resolved.Equals(expected, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new Exception("Canonical instance ID did not resolve to one direct child of instances/.");

        foreach (var invalid in new[]
                 {
                     "", " ", "foo", "foo/bar", "foo\\bar", "foo/../bar", "./bar", ".", "..",
                     id.ToUpperInvariant(), id + "/child"
                 })
        {
            try
            {
                _ = paths.GetInstanceDirectory(invalid);
                throw new Exception($"Non-canonical instance ID was accepted: {invalid}");
            }
            catch (ArgumentException)
            {
            }
        }

        var other = Guid.NewGuid().ToString("N");
        if (paths.GetInstanceDirectory(other).Equals(resolved, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal))
            throw new Exception("Distinct canonical instance IDs resolved to the same path.");
    }
}
