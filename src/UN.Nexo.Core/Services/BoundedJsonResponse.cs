using System.Text.Json;

namespace UN.Nexo.Core.Services;

internal static class BoundedJsonResponse
{
    internal static async Task<JsonDocument> ReadAsync(
        HttpContent content,
        int maxBytes,
        string source,
        CancellationToken cancellationToken,
        TimeSpan? idleTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));
        if (idleTimeout is not null && idleTimeout <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(idleTimeout));

        if (content.Headers.ContentLength is > 0
            && content.Headers.ContentLength.Value > maxBytes)
            throw new InvalidDataException(
                $"{source} metadata exceeds the {maxBytes}-byte limit.");

        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        await using var bufferStream = new MemoryStream();
        var buffer = new byte[64 * 1024];
        var total = 0;

        while (true)
        {
            int read;
            if (idleTimeout is null)
            {
                read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
            }
            else
            {
                using var idle = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                idle.CancelAfter(idleTimeout.Value);
                try
                {
                    read = await input.ReadAsync(buffer.AsMemory(), idle.Token);
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"{source} made no body-read progress for {idleTimeout.Value.TotalSeconds:0.###} seconds.");
                }
            }

            if (read == 0)
                break;

            total += read;
            if (total > maxBytes)
                throw new InvalidDataException(
                    $"{source} metadata exceeds the {maxBytes}-byte limit.");

            await bufferStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        bufferStream.Position = 0;
        try
        {
            return await JsonDocument.ParseAsync(
                bufferStream,
                cancellationToken: cancellationToken);
        }
        catch (JsonException ex)
        {
            throw new InvalidDataException($"{source} returned malformed JSON.", ex);
        }
    }
}
