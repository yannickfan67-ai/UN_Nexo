using System.Text.Json;

namespace UN.Nexo.Core.Services;

internal static class BoundedJsonResponse
{
    internal static async Task<JsonDocument> ReadAsync(
        HttpContent content,
        int maxBytes,
        string source,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (maxBytes <= 0)
            throw new ArgumentOutOfRangeException(nameof(maxBytes));

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
            var read = await input.ReadAsync(buffer.AsMemory(), cancellationToken);
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
