using System.Diagnostics;
using System.Text.RegularExpressions;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public sealed partial class JavaDiscoveryService
{
    private readonly NexoPathService? _paths;

    public JavaDiscoveryService(NexoPathService? paths = null)
    {
        _paths = paths;
    }

    public async Task<IReadOnlyList<JavaInstallation>> DiscoverAsync(CancellationToken cancellationToken = default)
    {
        var candidates = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        AddJavaHome(candidates);
        AddPathCandidates(candidates);
        AddPlatformCandidates(candidates);
        if (_paths is not null)
            AddRoot(candidates, _paths.GetRuntimesRoot(), "Nexo managed runtime", 4);

        var results = new List<JavaInstallation>();
        foreach (var candidate in candidates)
        {
            var installation = await ProbeAsync(candidate.Key, candidate.Value, cancellationToken);
            if (installation is not null)
                results.Add(installation);
        }

        return results
            .OrderByDescending(x => ParseMajorVersion(x.Version))
            .ThenBy(x => x.JavaPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static void AddJavaHome(IDictionary<string, string> candidates)
    {
        var javaHome = Environment.GetEnvironmentVariable("JAVA_HOME");
        if (string.IsNullOrWhiteSpace(javaHome))
            return;

        AddExecutable(candidates, Path.Combine(javaHome, "bin", JavaExecutableName), "JAVA_HOME");
    }

    private static void AddPathCandidates(IDictionary<string, string> candidates)
    {
        var path = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrWhiteSpace(path))
            return;

        foreach (var entry in path.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            AddExecutable(candidates, Path.Combine(entry, JavaExecutableName), "PATH");
    }

    private static void AddPlatformCandidates(IDictionary<string, string> candidates)
    {
        if (OperatingSystem.IsWindows())
        {
            AddRoot(candidates, Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Program Files", 3);
            var x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
            if (!string.IsNullOrWhiteSpace(x86))
                AddRoot(candidates, x86, "Program Files (x86)", 3);
            return;
        }

        if (OperatingSystem.IsMacOS())
        {
            AddRoot(candidates, "/Library/Java/JavaVirtualMachines", "macOS JavaVirtualMachines", 4);
            return;
        }

        AddRoot(candidates, "/usr/lib/jvm", "/usr/lib/jvm", 3);
        AddRoot(candidates, "/opt", "/opt", 2);
    }

    private static void AddRoot(IDictionary<string, string> candidates, string root, string source, int maxDepth)
    {
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root))
            return;

        Walk(root, 0);
        return;

        void Walk(string directory, int depth)
        {
            AddExecutable(candidates, Path.Combine(directory, "bin", JavaExecutableName), source);
            if (depth >= maxDepth)
                return;

            try
            {
                foreach (var child in Directory.EnumerateDirectories(directory))
                    Walk(child, depth + 1);
            }
            catch (UnauthorizedAccessException) { }
            catch (IOException) { }
        }
    }

    private static void AddExecutable(IDictionary<string, string> candidates, string path, string source)
    {
        try
        {
            if (File.Exists(path))
                candidates.TryAdd(Path.GetFullPath(path), source);
        }
        catch (Exception) when (path.Length > 0) { }
    }

    private static async Task<JavaInstallation?> ProbeAsync(string javaPath, string source, CancellationToken cancellationToken)
    {
        try
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = javaPath,
                Arguments = "-version",
                RedirectStandardError = true,
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(startInfo);
            if (process is null)
                return null;

            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));

            var stdoutTask = process.StandardOutput.ReadToEndAsync(timeout.Token);
            var stderrTask = process.StandardError.ReadToEndAsync(timeout.Token);
            await process.WaitForExitAsync(timeout.Token);
            var text = $"{await stderrTask}\n{await stdoutTask}";

            var match = JavaVersionRegex().Match(text);
            var version = match.Success ? match.Groups["version"].Value : "Unknown";
            var is64Bit = text.Contains("64-Bit", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("amd64", StringComparison.OrdinalIgnoreCase)
                          || text.Contains("aarch64", StringComparison.OrdinalIgnoreCase);
            var home = Directory.GetParent(Path.GetDirectoryName(javaPath) ?? string.Empty)?.FullName
                       ?? Path.GetDirectoryName(javaPath)
                       ?? string.Empty;

            return new JavaInstallation(javaPath, home, version, is64Bit, source);
        }
        catch (OperationCanceledException) { return null; }
        catch (Exception) { return null; }
    }

    private static int ParseMajorVersion(string version)
    {
        if (string.IsNullOrWhiteSpace(version) || version == "Unknown")
            return 0;

        var parts = version.Split('.', '-', '_');
        if (!int.TryParse(parts[0], out var first))
            return 0;
        if (first == 1 && parts.Length > 1 && int.TryParse(parts[1], out var legacy))
            return legacy;
        return first;
    }

    private static string JavaExecutableName => OperatingSystem.IsWindows() ? "java.exe" : "java";

    [GeneratedRegex("\\\"(?<version>[^\\\"]+)\\\"")]
    private static partial Regex JavaVersionRegex();
}
