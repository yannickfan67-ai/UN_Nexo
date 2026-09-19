namespace UN.Nexo.Core.Services;

public static class LauncherLaunchMode
{
    public const string TestOfflineArgument = "--test-offline";

    public static bool IsTestOfflineEnabled(IEnumerable<string> arguments)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        return arguments.Any(argument =>
            string.Equals(
                argument,
                TestOfflineArgument,
                StringComparison.Ordinal));
    }
}
