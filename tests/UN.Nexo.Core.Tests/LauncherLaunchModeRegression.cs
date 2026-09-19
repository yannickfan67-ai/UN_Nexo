using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class LauncherLaunchModeRegression
{
    internal static Task RunAsync()
    {
        Assert(
            LauncherLaunchMode.IsTestOfflineEnabled(["--test-offline"]),
            "The exact --test-offline switch must enable test offline mode.");
        Assert(
            LauncherLaunchMode.IsTestOfflineEnabled(["--other", "--test-offline"]),
            "The switch should be recognized when other arguments are present.");

        foreach (var arguments in new[]
                 {
                     Array.Empty<string>(),
                     new[] { "--offline" },
                     new[] { "--test-offline=true" },
                     new[] { "--TEST-OFFLINE" },
                     new[] { "prefix--test-offline" }
                 })
        {
            Assert(
                !LauncherLaunchMode.IsTestOfflineEnabled(arguments),
                "Only the exact --test-offline switch may enable test offline mode.");
        }

        return Task.CompletedTask;
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition)
            throw new InvalidOperationException(message);
    }
}
