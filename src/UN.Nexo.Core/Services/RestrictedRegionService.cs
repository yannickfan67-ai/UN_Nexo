using System.Net;
using UN.Nexo.Core.Models;

namespace UN.Nexo.Core.Services;

public enum RegionalAccessState
{
    RestrictedRegion,
    OutsideRestrictedRegion,
    Unknown
}

public sealed class RestrictedRegionService
{
    private const int MaxTraceBytes = 16 * 1024;
    private static readonly Uri TraceUri =
        new("https://www.cloudflare.com/cdn-cgi/trace");
    private static readonly HashSet<string> RestrictedCountryCodes =
        new(StringComparer.Ordinal) { "CN", "RU" };

    private readonly HttpClient _httpClient;

    public RestrictedRegionService(HttpClient httpClient)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public async Task<RegionalAccessState> DetectAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, TraceUri);
            using var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            if (response.RequestMessage?.RequestUri is { } effective
                && !Uri.Equals(effective, TraceUri))
                return RegionalAccessState.Unknown;

            if (!response.IsSuccessStatusCode)
                return RegionalAccessState.Unknown;

            if (response.Content.Headers.ContentLength is > MaxTraceBytes)
                return RegionalAccessState.Unknown;

            var body = await ReadBoundedAsync(
                response.Content,
                cancellationToken);

            string? country = null;
            foreach (var rawLine in body.Split('\n'))
            {
                var line = rawLine.TrimEnd('\r');
                if (!line.StartsWith("loc=", StringComparison.Ordinal))
                    continue;

                if (country is not null)
                    return RegionalAccessState.Unknown;

                country = line[4..].Trim().ToUpperInvariant();
            }

            if (country is null
                || country.Length != 2
                || country.Any(character => character is < 'A' or > 'Z'))
                return RegionalAccessState.Unknown;

            return RestrictedCountryCodes.Contains(country)
                ? RegionalAccessState.RestrictedRegion
                : RegionalAccessState.OutsideRestrictedRegion;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return RegionalAccessState.Unknown;
        }
        catch (Exception ex) when (
            ex is HttpRequestException
            or IOException)
        {
            return RegionalAccessState.Unknown;
        }
    }

    public async Task<bool> CanUseOfflineFallbackAsync(
        LauncherAccount account,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (!account.IsMicrosoft || account.EntitlementVerifiedAt is null)
            return false;

        return await DetectAsync(cancellationToken)
               == RegionalAccessState.RestrictedRegion;
    }

    private static async Task<string> ReadBoundedAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var input = await content.ReadAsStreamAsync(cancellationToken);
        using var reader = new MemoryStream();
        var buffer = new byte[4096];

        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
                break;
            if (reader.Length + read > MaxTraceBytes)
                throw new InvalidDataException(
                    "Regional trace response exceeded the size limit.");
            await reader.WriteAsync(buffer.AsMemory(0, read), cancellationToken);
        }

        return System.Text.Encoding.UTF8.GetString(reader.ToArray());
    }
}
