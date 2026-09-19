using System.Diagnostics;
using System.Globalization;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class InstallerProcessOutputRegression
{
    private const int RetainedCharacters = 4096;

    internal static async Task RunAsync()
    {
        await TestBoundedReaderAsync();
        await TestRealProcessCaptureAsync();
        await TestCancellationKillsProcessAsync();
    }

    internal static async Task<int> RunHelperAsync(string[] args)
    {
        if (args.Length < 4
            || !int.TryParse(
                args[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var characters)
            || characters < 0)
        {
            return 2;
        }

        var pidPath = args[2];
        var hold = args[3].Equals(
            "hold",
            StringComparison.Ordinal);

        if (!pidPath.Equals("-", StringComparison.Ordinal))
        {
            await File.WriteAllTextAsync(
                pidPath,
                Environment.ProcessId.ToString(
                    CultureInfo.InvariantCulture));
        }

        var stdoutChunk = new string('O', 8192);
        var stderrChunk = new string('E', 8192);
        var remaining = characters;
        while (remaining > 0)
        {
            var count = Math.Min(
                remaining,
                stdoutChunk.Length);
            await Console.Out.WriteAsync(
                stdoutChunk.AsMemory(0, count));
            await Console.Error.WriteAsync(
                stderrChunk.AsMemory(0, count));
            remaining -= count;
        }

        await Console.Out.WriteLineAsync("OUT-END");
        await Console.Error.WriteLineAsync("ERR-END");
        await Console.Out.FlushAsync();
        await Console.Error.FlushAsync();

        if (hold)
            await Task.Delay(Timeout.InfiniteTimeSpan);

        return 0;
    }

    private static async Task TestBoundedReaderAsync()
    {
        var capture =
            await InstallerProcessRunner.CaptureReaderAsync(
                new StringReader(
                    new string('A', 100_000)
                    + "TAIL"),
                512);

        Assert(
            capture.Truncated,
            "Large installer text should report truncation.");
        Assert(
            capture.Text.Length <= 512,
            "Bounded installer text exceeded its configured retention limit.");
        Assert(
            capture.Text.Contains(
                "output truncated",
                StringComparison.OrdinalIgnoreCase),
            "Bounded installer text should contain a truncation marker.");
        Assert(
            capture.Text.EndsWith(
                "TAIL",
                StringComparison.Ordinal),
            "Bounded installer text must preserve the useful tail.");
    }

    private static async Task TestRealProcessCaptureAsync()
    {
        var startInfo =
            BuildHelperStartInfo(
                1024 * 1024,
                "-",
                hold: false);

        using var timeout =
            new CancellationTokenSource(
                TimeSpan.FromSeconds(20));
        var capture =
            await InstallerProcessRunner.RunAsync(
                startInfo,
                "installer output regression helper",
                RetainedCharacters,
                timeout.Token);

        Assert(
            capture.ExitCode == 0,
            "Installer output helper should exit successfully.");
        Assert(
            capture.StandardOutput.Truncated
            && capture.StandardError.Truncated,
            "Both noisy installer streams should be reported as truncated.");
        Assert(
            capture.StandardOutput.Text.Length <= RetainedCharacters
            && capture.StandardError.Text.Length <= RetainedCharacters,
            "Real installer stream capture exceeded the configured memory bound.");
        Assert(
            capture.StandardOutput.Text.EndsWith(
                "OUT-END" + Environment.NewLine,
                StringComparison.Ordinal)
            || capture.StandardOutput.Text.EndsWith(
                "OUT-END\n",
                StringComparison.Ordinal),
            "Installer stdout tail was not preserved.");
        Assert(
            capture.StandardError.Text.EndsWith(
                "ERR-END" + Environment.NewLine,
                StringComparison.Ordinal)
            || capture.StandardError.Text.EndsWith(
                "ERR-END\n",
                StringComparison.Ordinal),
            "Installer stderr tail was not preserved.");
    }

    private static async Task TestCancellationKillsProcessAsync()
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-installer-output-"
            + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        var pidPath = Path.Combine(root, "pid.txt");
        var pid = 0;

        try
        {
            using var cancellation =
                new CancellationTokenSource();
            var runTask =
                InstallerProcessRunner.RunAsync(
                    BuildHelperStartInfo(
                        0,
                        pidPath,
                        hold: true),
                    "installer cancellation regression helper",
                    RetainedCharacters,
                    cancellation.Token);

            for (var attempt = 0;
                 attempt < 100 && !File.Exists(pidPath);
                 attempt++)
            {
                await Task.Delay(20);
            }

            Assert(
                File.Exists(pidPath),
                "Installer cancellation helper did not publish its PID.");
            pid = int.Parse(
                await File.ReadAllTextAsync(pidPath),
                CultureInfo.InvariantCulture);

            cancellation.Cancel();

            try
            {
                await runTask;
                throw new Exception(
                    "Cancelled installer process unexpectedly completed normally.");
            }
            catch (OperationCanceledException)
            {
            }

            Assert(
                await IsProcessExitedAsync(pid),
                "Cancelling installer capture must terminate the child process.");
        }
        finally
        {
            if (pid > 0)
            {
                try
                {
                    using var process =
                        Process.GetProcessById(pid);
                    if (!process.HasExited)
                        process.Kill(
                            entireProcessTree: true);
                }
                catch (ArgumentException)
                {
                }
            }

            try
            {
                Directory.Delete(
                    root,
                    recursive: true);
            }
            catch
            {
            }
        }
    }

    private static ProcessStartInfo BuildHelperStartInfo(
        int characters,
        string pidPath,
        bool hold)
    {
        var executable =
            Environment.ProcessPath
            ?? throw new InvalidOperationException(
                "Current test executable path is unavailable.");

        var startInfo =
            new ProcessStartInfo
            {
                FileName = executable,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };

        if (Path.GetFileNameWithoutExtension(executable)
            .Equals(
                "dotnet",
                StringComparison.OrdinalIgnoreCase))
        {
            startInfo.ArgumentList.Add(
                typeof(InstallerProcessOutputRegression)
                    .Assembly
                    .Location);
        }

        startInfo.ArgumentList.Add(
            "--installer-output-helper");
        startInfo.ArgumentList.Add(
            characters.ToString(
                CultureInfo.InvariantCulture));
        startInfo.ArgumentList.Add(pidPath);
        startInfo.ArgumentList.Add(
            hold ? "hold" : "exit");
        return startInfo;
    }

    private static async Task<bool> IsProcessExitedAsync(int pid)
    {
        for (var attempt = 0; attempt < 100; attempt++)
        {
            try
            {
                using var process =
                    Process.GetProcessById(pid);
                if (process.HasExited)
                    return true;
            }
            catch (ArgumentException)
            {
                return true;
            }

            await Task.Delay(20);
        }

        return false;
    }

    private static void Assert(
        bool condition,
        string message)
    {
        if (!condition)
            throw new Exception(message);
    }
}
