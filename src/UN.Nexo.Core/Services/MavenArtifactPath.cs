namespace UN.Nexo.Core.Services;

public static class MavenArtifactPath
{
    public static string FromCoordinate(string coordinate)
    {
        var parsed = Parse(coordinate);
        var groupPath = string.Join(
            Path.DirectorySeparatorChar,
            parsed.Group.Split('.'));

        var fileName = $"{parsed.Artifact}-{parsed.Version}";
        if (!string.IsNullOrWhiteSpace(parsed.Classifier))
            fileName += $"-{parsed.Classifier}";
        fileName += $".{parsed.Extension}";

        return Path.Combine(
            groupPath,
            parsed.Artifact,
            parsed.Version,
            fileName);
    }

    public static string InheritanceIdentity(string coordinate)
    {
        var parsed = Parse(coordinate);
        return string.Concat(
            parsed.Group,
            ":",
            parsed.Artifact,
            ":",
            parsed.Classifier ?? string.Empty,
            "@",
            parsed.Extension);
    }

    private static ParsedCoordinate Parse(string coordinate)
    {
        if (string.IsNullOrWhiteSpace(coordinate))
            throw new InvalidDataException("Maven coordinate is empty.");

        var value = coordinate.Trim();
        var extension = "jar";
        var at = value.LastIndexOf('@');
        if (at >= 0)
        {
            extension = value[(at + 1)..];
            value = value[..at];
        }

        var parts = value.Split(':');
        if (parts.Length is < 3 or > 4)
            throw new InvalidDataException(
                $"Unsupported Maven coordinate: {coordinate}");

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length == 4 ? parts[3] : null;
        foreach (var part in parts)
            ValidateSegment(part, coordinate);
        ValidateSegment(extension, coordinate);

        var groupParts = group.Split('.');
        foreach (var groupPart in groupParts)
            ValidateSegment(groupPart, coordinate);

        return new ParsedCoordinate(
            group,
            artifact,
            version,
            classifier,
            extension);
    }

    private static void ValidateSegment(string value, string coordinate)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.Contains('/')
            || value.Contains('\\')
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException(
                $"Unsafe Maven coordinate: {coordinate}");
    }

    private sealed record ParsedCoordinate(
        string Group,
        string Artifact,
        string Version,
        string? Classifier,
        string Extension);
}
