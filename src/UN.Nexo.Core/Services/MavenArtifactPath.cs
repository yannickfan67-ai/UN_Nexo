namespace UN.Nexo.Core.Services;

public static class MavenArtifactPath
{
    public static string FromCoordinate(string coordinate)
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
            throw new InvalidDataException($"Unsupported Maven coordinate: {coordinate}");

        var group = parts[0];
        var artifact = parts[1];
        var version = parts[2];
        var classifier = parts.Length == 4 ? parts[3] : null;
        foreach (var part in parts)
            ValidateSegment(part, coordinate);
        ValidateSegment(extension, coordinate);

        var groupPath = string.Join(Path.DirectorySeparatorChar,
            group.Split('.', StringSplitOptions.RemoveEmptyEntries));
        var fileName = $"{artifact}-{version}";
        if (!string.IsNullOrWhiteSpace(classifier))
            fileName += $"-{classifier}";
        fileName += $".{extension}";

        return Path.Combine(groupPath, artifact, version, fileName);
    }

    private static void ValidateSegment(string value, string coordinate)
    {
        if (string.IsNullOrWhiteSpace(value)
            || value is "." or ".."
            || value.Contains('/')
            || value.Contains('\\')
            || value.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
            throw new InvalidDataException($"Unsafe Maven coordinate: {coordinate}");
    }
}
