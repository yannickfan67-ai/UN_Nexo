using System.Runtime.InteropServices;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UN.Nexo.Core.Launching;

public static class MinecraftRules
{
    public static string OsName => OperatingSystem.IsWindows() ? "windows"
        : OperatingSystem.IsMacOS() ? "osx" : "linux";

    public static string ArchitectureName => RuntimeInformation.OSArchitecture switch
    {
        Architecture.X64 => "x86_64",
        Architecture.X86 => "x86",
        Architecture.Arm64 => "aarch64",
        _ => "arm"
    };

    public static bool Allows(JsonElement element)
    {
        if (!element.TryGetProperty("rules", out var rules))
            return true;

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            if (rule.TryGetProperty("os", out var os))
            {
                if (os.TryGetProperty("name", out var name)
                    && !string.Equals(name.GetString(), OsName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (os.TryGetProperty("arch", out var arch)
                    && !string.Equals(arch.GetString(), ArchitectureName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (os.TryGetProperty("version", out var version)
                    && (string.IsNullOrWhiteSpace(version.GetString())
                        || !Regex.IsMatch(Environment.OSVersion.Version.ToString(), version.GetString()!,
                            RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1))))
                    continue;
            }

            if (rule.TryGetProperty("features", out var features)
                && features.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.False))
                continue;

            allowed = string.Equals(rule.GetProperty("action").GetString(), "allow",
                StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    public static string? NativeClassifier(JsonElement library)
    {
        if (!library.TryGetProperty("natives", out var natives)
            || !natives.TryGetProperty(OsName, out var classifier))
            return null;

        return classifier.GetString()?.Replace(
            "${arch}", Environment.Is64BitOperatingSystem ? "64" : "32",
            StringComparison.Ordinal);
    }
}
