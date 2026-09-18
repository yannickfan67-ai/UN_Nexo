using System.Diagnostics;
using System.Reflection;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class JavaDiscoveryRegression
{
    internal static async Task RunAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP Java discovery cleanup fixture requires a Unix shell");
            return;
        }

        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-java-discovery-tests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            await TestNormalProbeAsync(root);
            await TestInternalTimeoutKillsProcessAsync(root);
            await TestCallerCancellationKillsAndPropagatesAsync(root);
        }
        finally
        {
            try { Directory.Delete(root, recursive: true); } catch { }
        }
    }

    private static async Task TestNormalProbeAsync(string root)
    {
        var script = Path.Combine(root, "java-normal");
        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\necho 'openjdk version \"21.0.1\"' >&2\necho 'OpenJDK 64-Bit Server VM' >&2\nexit 0\n");
        MakeExecutable(script);

        var service = new JavaDiscoveryService(
            probeTimeout: TimeSpan.FromMilliseconds(200));
        var result = await InvokeProbeAsync(service, script, CancellationToken.None);

        Require(result is not null, "Normal Java probe should return an installation.");
        Require(result!.Version == "21.0.1", "Normal Java probe should parse the version.");
        Require(result.Is64Bit, "Normal Java probe should detect 64-bit output.");
    }

    private static async Task TestInternalTimeoutKillsProcessAsync(string root)
    {
        var pidFile = Path.Combine(root, "timeout.pid");
        var script = Path.Combine(root, "java-timeout");
        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\necho $$ > '" + EscapeShell(pidFile) + "'\nwhile :; do sleep 1; done\n");
        MakeExecutable(script);

        var service = new JavaDiscoveryService(
            probeTimeout: TimeSpan.FromMilliseconds(80));
        var stopwatch = Stopwatch.StartNew();
        var result = await InvokeProbeAsync(service, script, CancellationToken.None);
        stopwatch.Stop();

        Require(result is null, "Timed-out Java probe should be skipped.");
        Require(stopwatch.Elapsed < TimeSpan.FromSeconds(2),
            "Internal Java probe timeout should bound discovery.");
        var pid = await ReadPidAsync(pidFile);
        await RequireProcessGoneAsync(pid,
            "Timed-out Java probe process should be terminated.");
    }

    private static async Task TestCallerCancellationKillsAndPropagatesAsync(string root)
    {
        var pidFile = Path.Combine(root, "cancel.pid");
        var script = Path.Combine(root, "java-cancel");
        await File.WriteAllTextAsync(
            script,
            "#!/bin/sh\necho $$ > '" + EscapeShell(pidFile) + "'\nwhile :; do sleep 1; done\n");
        MakeExecutable(script);

        var service = new JavaDiscoveryService(
            probeTimeout: TimeSpan.FromSeconds(5));
        using var cancellation = new CancellationTokenSource(
            TimeSpan.FromMilliseconds(80));

        try
        {
            await InvokeProbeAsync(service, script, cancellation.Token);
            throw new InvalidOperationException(
                "Caller-cancelled Java probe unexpectedly completed.");
        }
        catch (OperationCanceledException)
        {
        }

        var pid = await ReadPidAsync(pidFile);
        await RequireProcessGoneAsync(pid,
            "Caller-cancelled Java probe process should be terminated before cancellation propagates.");
    }

    private static async Task<JavaInstallation?> InvokeProbeAsync(
        JavaDiscoveryService service,
        string javaPath,
        CancellationToken cancellationToken)
    {
        var method = typeof(JavaDiscoveryService).GetMethod(
            "ProbeAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)
            ?? throw new InvalidOperationException("Java discovery probe hook was not found.");

        var task = method.Invoke(
            service,
            [javaPath, "regression", cancellationToken])
            as Task<JavaInstallation?>
            ?? throw new InvalidOperationException("Java discovery probe hook returned an unexpected task type.");

        return await task;
    }

    private static async Task<int> ReadPidAsync(string path)
    {
        using var guard = new CancellationTokenSource(TimeSpan.FromSeconds(1));
        while (!File.Exists(path))
            await Task.Delay(10, guard.Token);

        var text = await File.ReadAllTextAsync(path, guard.Token);
        if (!int.TryParse(text.Trim(), out var pid) || pid <= 0)
            throw new InvalidOperationException("Probe fixture did not record a valid process id.");
        return pid;
    }

    private static async Task RequireProcessGoneAsync(int pid, string message)
    {
        for (var attempt = 0; attempt < 50; attempt++)
        {
            if (!ProcessExists(pid))
                return;
            await Task.Delay(10);
        }

        throw new InvalidOperationException(message);
    }

    private static bool ProcessExists(int pid)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            return !process.HasExited;
        }
        catch (ArgumentException)
        {
            return false;
        }
    }

    private static void MakeExecutable(string path)
    {
        File.SetUnixFileMode(
            path,
            UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute
            | UnixFileMode.GroupRead | UnixFileMode.GroupExecute
            | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
    }

    private static string EscapeShell(string value)
        => value.Replace("'", "'\\''", StringComparison.Ordinal);

    private static void Require(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
