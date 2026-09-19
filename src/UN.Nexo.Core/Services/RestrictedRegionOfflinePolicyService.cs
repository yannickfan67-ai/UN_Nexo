using System.Net;
using System.Text;

namespace UN.Nexo.Core.Services;

public sealed class RestrictedRegionOfflinePolicyService
{
    private const int MaxTraceBytes = 16 * 1024;
    private static readonly Uri TraceUri = new("https://www.cloudflare.com/cdn-cgi/trace");
    private static readonly HashSet<string> OfflineFallbackRegions =
        new(StringComparer.OrdinalIgnoreCase) { "CN", "RU" };

    private readonly HttpClient _httpClient;

    public RestrictedRegionOfflinePolicyService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<bool> IsOfflineFallbackRegionAsync(
        CancellationToken cancellationToken = default)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, TraceUri);
        request.Headers.Accept.ParseAdd("text/plain");

        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);

        if (response.RequestMessage?.RequestUri is { } effective
            && !Uri.Equals(effective, TraceUri))
            throw new InvalidDataException(
                "Region check unexpectedly redirected to another endpoint.");

        if (response.StatusCode != HttpStatusCode.OK)
            throw new HttpRequestException(
                $"Region check returned HTTP {(int)response.StatusCode} ({response.StatusCode}).",
                null,
                response.StatusCode);

        if (response.Content.Headers.ContentLength is > MaxTraceBytes)
            throw new InvalidDataException("Region check returned an oversized response.");

        await using var input = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var buffer = new MemoryStream();
        var chunk = new byte[2048];
        while (true)
        {
            var read = await input.ReadAsync(chunk, cancellationToken);
            if (read == 0)
                break;
            if (buffer.Length + read > MaxTraceBytes)
                throw new InvalidDataException("Region check response exceeded the size limit.");
            await buffer.WriteAsync(chunk.AsMemory(0, read), cancellationToken);
        }

        var text = Encoding.ASCII.GetString(buffer.ToArray());
        string? countryCode = null;
        foreach (var line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (!line.StartsWith("loc=", StringComparison.Ordinal))
                continue;

            var value = line[4..].Trim();
            if (value.Length != 2 || !value.All(char.IsAsciiLetter))
                throw new InvalidDataException("Region check returned an invalid country code.");

            if (countryCode is not null
                && !string.Equals(countryCode, value, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Region check returned conflicting country codes.");

            countryCode = value.ToUpperInvariant();
        }

        if (countryCode is null)
            throw new InvalidDataException("Region check did not return a country code.");

        return OfflineFallbackRegions.Contains(countryCode);
    }
}
