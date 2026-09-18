namespace UN.Nexo.Core.Services;

internal static class MetadataPath
{
    private static readonly char[] CrossPlatformInvalidComponentChars =
        ['<', '>', ':', '"', '/', '\\', '|', '?', '*'];

    public static string RequireSingleComponent(string? value, string fieldName)
    {
        if (string.IsNullOrWhiteSpace(value))
            throw Invalid(fieldName);

        var component = value;
        if (!string.Equals(component, component.Trim(), StringComparison.Ordinal)
            || component is "." or ".."
            || Path.IsPathRooted(component)
            || component.IndexOfAny(CrossPlatformInvalidComponentChars) >= 0
            || component.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
            || component.Any(char.IsControl)
            || component.EndsWith(' ')
            || component.EndsWith('.')
            || !string.Equals(Path.GetFileName(component), component, StringComparison.Ordinal))
            throw Invalid(fieldName);

        return component;
    }

    public static string ResolveSingleComponent(
        string root,
        string? value,
        string suffix,
        string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(suffix);

        var component = RequireSingleComponent(value, fieldName);
        var fullRoot = Path.GetFullPath(root);
        var result = Path.GetFullPath(Path.Combine(fullRoot, component + suffix));
        var parent = Path.GetDirectoryName(result);
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (parent is null
            || !string.Equals(
                Path.TrimEndingDirectorySeparator(parent),
                Path.TrimEndingDirectorySeparator(fullRoot),
                comparison))
            throw new InvalidDataException(
                $"{fieldName} resolves outside its expected directory.");

        return result;
    }

    private static InvalidDataException Invalid(string fieldName)
        => new($"{fieldName} must be one non-empty safe filesystem component.");
}
