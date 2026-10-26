namespace UN.Nexo.Core.Services;

internal static class BoundedLocalFile
{
    internal static async Task<byte[]> ReadAllBytesAsync(
        string path,
        long maxBytes,
        string label,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        if (maxBytes <= 0 || maxBytes > int.MaxValue)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

        var fullPath = Path.GetFullPath(path);
        await using var stream = new FileStream(
            fullPath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            64 * 1024,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        if (stream.Length > maxBytes)
            throw Oversized(label, maxBytes);

        using var output = new MemoryStream(
            checked((int)Math.Min(stream.Length, maxBytes)));
        var buffer = new byte[64 * 1024];

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(
                buffer.AsMemory(),
                cancellationToken);
            if (read == 0)
                break;

            if (output.Length > maxBytes - read)
                throw Oversized(label, maxBytes);

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        return output.ToArray();
    }

    private static InvalidDataException Oversized(
        string label,
        long maxBytes)
        => new(
            $"{label} exceeds the {maxBytes}-byte safety limit.");
}
