namespace UN.Nexo.Core.Services;

internal static class LoaderLibraryDownloadPolicy
{
    internal const long DefaultMaxArtifactBytes =
        512L * 1024L * 1024L;

    internal static long RequirePositiveLimit(
        long value,
        string parameterName)
    {
        if (value <= 0)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                "Loader library size limit must be positive.");
        }

        return value;
    }

    internal static void ValidateDeclaredLength(
        long? contentLength,
        long maxBytes,
        string label,
        string url)
    {
        if (contentLength is not null
            && contentLength.Value > maxBytes)
        {
            throw new InvalidDataException(
                $"{label} declared {contentLength.Value} bytes, exceeding the {maxBytes}-byte loader library limit: {url}");
        }
    }
}
