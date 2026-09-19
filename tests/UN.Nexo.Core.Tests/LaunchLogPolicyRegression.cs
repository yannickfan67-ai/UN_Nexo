using UN.Nexo.Core.Launching;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class LaunchLogPolicyRegression
{
    internal static async Task RunAsync()
    {
        TestRetentionOnlyDeletesOwnedLogs();
        await TestNoisyProcessStaysBoundedAsync();
    }

    private static void TestRetentionOnlyDeletesOwnedLogs()
    {
        var root = NewRoot("retention");
        var logs = Path.Combine(root, "logs");
        Directory.CreateDirectory(logs);
        try
        {
            for (var index = 0; index < 6; index++)
            {
                var path = Path.Combine(
                    logs,
                    $"20260919-00{index:D2}00-{Guid.NewGuid():N}.log");
                File.WriteAllBytes(path, new byte[1024]);
                File.SetLastWriteTimeUtc(path, DateTime.UtcNow.AddMinutes(index - 10));
            }

            var unrelated = Path.Combine(logs, "minecraft.log");
            var lookalike = Path.Combine(logs, "20260919-000000-not-a-guid.log");
            File.WriteAllText(unrelated, "user-owned");
            File.WriteAllText(lookalike, "not-nexo-owned");

            MinecraftProcessService.PruneOwnedLogs(
                logs,
                maxCount: 3,
                maxBytes: 2500);

            var remainingOwned = Directory.EnumerateFiles(logs, "*.log")
                .Where(path =>
                {
                    var name = Path.GetFileNameWithoutExtension(path);
                    var pieces = name.Split('-');
                    return pieces.Length == 3
                           && pieces[0].Length == 8
                           && pieces[1].Length == 6
                           && Guid.TryParseExact(pieces[2], "N", out _);
                })
                .ToArray();
            var total = remainingOwned.Sum(path => new FileInfo(path).Length);

            Assert(remainingOwned.Length <= 3,
                "Retention should enforce the owned-log count ceiling.");
            Assert(total <= 2500,
                "Retention should enforce the owned-log byte ceiling.");
            Assert(File.ReadAllText(unrelated) == "user-owned",
                "Retention must not delete or modify Minecraft/user log files.");
            Assert(File.ReadAllText(lookalike) == "not-nexo-owned",
                "Retention must not delete lookalike filenames that are not Nexo-owned.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static async Task TestNoisyProcessStaysBoundedAsync()
    {
        if (OperatingSystem.IsWindows())
        {
            Console.WriteLine("SKIP noisy launch-log fixture requires a Unix shell.");
            return;
        }

        var root = NewRoot("noisy");
        try
        {
            var executable = Path.Combine(root, "noisy-java");
            await File.WriteAllTextAsync(
                executable,
                """
                #!/bin/sh
                (head -c 5000000 /dev/zero | tr '\000' 'O'; printf '\n') &
                (head -c 5000000 /dev/zero | tr '\000' 'E'; printf '\n') >&2 &
                wait
                exit 0
                """);
            File.SetUnixFileMode(
                executable,
                UnixFileMode.UserRead
                | UnixFileMode.UserWrite
                | UnixFileMode.UserExecute);

            var paths = new NexoPathService(Path.Combine(root, "data"));
            var logs = Path.Combine(root, "logs");
            var plan = new MinecraftLaunchPlan(
                executable,
                root,
                [],
                logs);
            var service = new MinecraftProcessService(
                new LauncherRuntimeSettingsService(paths));

            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            var result = await service.RunAsync(
                plan,
                cancellationToken: timeout.Token);

            Assert(result.ExitCode == 0,
                "A process that exceeds the persistence quota should still drain and exit normally.");
            Assert(!timeout.IsCancellationRequested,
                "Noisy process should complete before the fixture timeout.");

            var info = new FileInfo(result.LogPath);
            Assert(info.Exists,
                "Bounded launch log should still be published.");
            Assert(info.Length <= MinecraftProcessService.MaxOwnedLogBytes,
                $"Launch log exceeded the {MinecraftProcessService.MaxOwnedLogBytes}-byte ceiling.");

            var text = await File.ReadAllTextAsync(result.LogPath);
            Assert(
                text.Contains("Log persistence limit reached", StringComparison.Ordinal),
                "Bounded log should explain that persistence was truncated.");
        }
        finally
        {
            TryDelete(root);
        }
    }

    private static string NewRoot(string suffix)
    {
        var root = Path.Combine(
            Path.GetTempPath(),
            "un-nexo-launch-log-tests",
            suffix + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        return root;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new Exception(message);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Directory.Delete(path, recursive: true);
        }
        catch
        {
        }
    }
}
