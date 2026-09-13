using System.Diagnostics;

namespace UN.Nexo.Core.Launching;

public sealed record MinecraftLaunchPlan(
    string JavaPath,
    string WorkingDirectory,
    IReadOnlyList<string> Arguments,
    string LogDirectory)
{
    public ProcessStartInfo CreateStartInfo()
    {
        var info = new ProcessStartInfo(JavaPath)
        {
            WorkingDirectory = WorkingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        foreach (var argument in Arguments)
            info.ArgumentList.Add(argument);

        return info;
    }
}
