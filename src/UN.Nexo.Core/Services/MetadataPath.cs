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

    public static string ResolveRelativePath(
        string root,
        string? value,
        string fieldName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        if (string.IsNullOrWhiteSpace(value))
            throw InvalidRelative(fieldName);

        var relative = value;
        if (!string.Equals(relative, relative.Trim(), StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            || relative.StartsWith('/', StringComparison.Ordinal)
            || relative.EndsWith('/', StringComparison.Ordinal)
            || relative.Contains('\\')
            || relative.Any(char.IsControl))
            throw InvalidRelative(fieldName);

        var segments = relative.Split('/');
        if (segments.Length == 0)
            throw InvalidRelative(fieldName);

        foreach (var segment in segments)
        {
            if (string.IsNullOrWhiteSpace(segment)
                || segment is "." or ".."
                || segment.IndexOfAny(CrossPlatformInvalidComponentChars) >= 0
                || segment.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0
                || segment.Any(char.IsControl)
                || segment.EndsWith(' ')
                || segment.EndsWith('.')
                || !string.Equals(Path.GetFileName(segment), segment, StringComparison.Ordinal))
                throw InvalidRelative(fieldName);
        }

        var fullRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        var result = Path.GetFullPath(
            Path.Combine(fullRoot, Path.Combine(segments)));
        var comparison = OperatingSystem.IsWindows()
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;

        if (!result.StartsWith(fullRoot + Path.DirectorySeparatorChar, comparison))
            throw new InvalidDataException(
                $"{fieldName} resolves outside its expected directory.");

        return result;
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

    private static InvalidDataException InvalidRelative(string fieldName)
        => new($"{fieldName} must be a safe relative path beneath its expected directory.");
}
