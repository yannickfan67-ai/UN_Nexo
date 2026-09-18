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
        RequireObject(element, "rule-bearing metadata");
        if (!element.TryGetProperty("rules", out var rules))
            return true;
        if (rules.ValueKind != JsonValueKind.Array)
            throw Invalid("rules", "an array");

        var allowed = false;
        foreach (var rule in rules.EnumerateArray())
        {
            RequireObject(rule, "rules[]");
            if (!rule.TryGetProperty("action", out var action) || action.ValueKind != JsonValueKind.String)
                throw Invalid("rules[].action", "a string");

            if (rule.TryGetProperty("os", out var os))
            {
                RequireObject(os, "rules[].os");
                if (TryGetOptionalString(os, "name", out var name)
                    && !string.Equals(name, OsName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (TryGetOptionalString(os, "arch", out var arch)
                    && !string.Equals(arch, ArchitectureName, StringComparison.OrdinalIgnoreCase))
                    continue;
                if (TryGetOptionalString(os, "version", out var version))
                {
                    if (string.IsNullOrWhiteSpace(version))
                        continue;
                    try
                    {
                        if (!Regex.IsMatch(Environment.OSVersion.Version.ToString(), version,
                                RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1)))
                            continue;
                    }
                    catch (ArgumentException ex)
                    {
                        throw new InvalidDataException("Minecraft metadata property 'rules[].os.version' contains an invalid regular expression.", ex);
                    }
                    catch (RegexMatchTimeoutException ex)
                    {
                        throw new InvalidDataException("Minecraft metadata property 'rules[].os.version' regular expression timed out.", ex);
                    }
                }
            }

            if (rule.TryGetProperty("features", out var features))
            {
                RequireObject(features, "rules[].features");
                foreach (var feature in features.EnumerateObject())
                {
                    if (feature.Value.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                        throw Invalid($"rules[].features.{feature.Name}", "a boolean");
                }
                if (features.EnumerateObject().Any(item => item.Value.ValueKind != JsonValueKind.False))
                    continue;
            }

            allowed = string.Equals(action.GetString(), "allow", StringComparison.OrdinalIgnoreCase);
        }

        return allowed;
    }

    public static string? NativeClassifier(JsonElement library)
    {
        RequireObject(library, "library metadata");
        if (!library.TryGetProperty("natives", out var natives))
            return null;
        RequireObject(natives, "natives");
        if (!natives.TryGetProperty(OsName, out var classifier))
            return null;
        if (classifier.ValueKind != JsonValueKind.String)
            throw Invalid($"natives.{OsName}", "a string");

        return classifier.GetString()?.Replace(
            "${arch}", Environment.Is64BitOperatingSystem ? "64" : "32",
            StringComparison.Ordinal);
    }

    private static bool TryGetOptionalString(JsonElement element, string propertyName, out string value)
    {
        if (!element.TryGetProperty(propertyName, out var property))
        {
            value = string.Empty;
            return false;
        }
        if (property.ValueKind != JsonValueKind.String)
            throw Invalid(propertyName, "a string");
        value = property.GetString() ?? string.Empty;
        return true;
    }

    private static void RequireObject(JsonElement element, string propertyName)
    {
        if (element.ValueKind != JsonValueKind.Object)
            throw Invalid(propertyName, "an object");
    }

    private static InvalidDataException Invalid(string propertyName, string expected) =>
        new($"Minecraft metadata property '{propertyName}' must be {expected}.");
}
