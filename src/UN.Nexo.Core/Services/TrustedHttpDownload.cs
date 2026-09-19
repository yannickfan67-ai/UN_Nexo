using System.Net;

namespace UN.Nexo.Core.Services;

internal static class TrustedHttpDownload
{
    private const int MaxRedirects = 5;
    private const int MaxTransientRetries = 2;
    private static readonly TimeSpan BaseRetryDelay =
        TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan MaxRetryDelay =
        TimeSpan.FromSeconds(5);

    internal static async Task<HttpResponseMessage> SendGetAsync(
        HttpClient client,
        string url,
        string label,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var current =
            TrustedDownloadPolicy.RequireTrustedUri(
                url,
                label);

        for (var redirectCount = 0; ; redirectCount++)
        {
            HttpResponseMessage response;
            for (var retryAttempt = 0; ; retryAttempt++)
            {
                try
                {
                    using var request =
                        new HttpRequestMessage(
                            HttpMethod.Get,
                            current);
                    response =
                        await client.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            cancellationToken);
                }
                catch (HttpRequestException) when (
                    retryAttempt < MaxTransientRetries)
                {
                    await DelayBeforeRetryAsync(
                        response: null,
                        retryAttempt,
                        cancellationToken);
                    continue;
                }
                catch (OperationCanceledException) when (
                    !cancellationToken.IsCancellationRequested
                    && retryAttempt < MaxTransientRetries)
                {
                    await DelayBeforeRetryAsync(
                        response: null,
                        retryAttempt,
                        cancellationToken);
                    continue;
                }

                var effective =
                    response.RequestMessage?.RequestUri;
                if (effective is not null
                    && !UriEquals(
                        effective,
                        current))
                {
                    response.Dispose();
                    throw new InvalidDataException(
                        $"{label} HTTP client followed a redirect automatically. Automatic redirects must be disabled so Nexo can validate every target before connecting.");
                }

                if (IsTransientStatus(response.StatusCode)
                    && retryAttempt < MaxTransientRetries)
                {
                    await DelayBeforeRetryAsync(
                        response,
                        retryAttempt,
                        cancellationToken);
                    response.Dispose();
                    continue;
                }

                break;
            }

            if (!IsRedirect(response.StatusCode))
                return response;

            if (redirectCount >= MaxRedirects)
            {
                response.Dispose();
                throw new InvalidDataException(
                    $"{label} exceeded the {MaxRedirects}-redirect safety limit.");
            }

            var location =
                response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                throw new InvalidDataException(
                    $"{label} returned a redirect without a Location header.");
            }

            Uri next;
            try
            {
                next =
                    location.IsAbsoluteUri
                        ? location
                        : new Uri(
                            current,
                            location);
            }
            catch (UriFormatException ex)
            {
                response.Dispose();
                throw new InvalidDataException(
                    $"{label} returned an invalid redirect target.",
                    ex);
            }

            response.Dispose();
            current =
                TrustedDownloadPolicy.RequireTrustedUri(
                    next.AbsoluteUri,
                    $"{label} redirect");
        }
    }

    private static async Task DelayBeforeRetryAsync(
        HttpResponseMessage? response,
        int retryAttempt,
        CancellationToken cancellationToken)
    {
        var delay =
            GetRetryDelay(
                response,
                retryAttempt);
        if (delay <= TimeSpan.Zero)
            return;

        await Task.Delay(
            delay,
            cancellationToken);
    }

    private static TimeSpan GetRetryDelay(
        HttpResponseMessage? response,
        int retryAttempt)
    {
        var retryAfter =
            response?.Headers.RetryAfter;
        if (retryAfter?.Delta is { } delta)
            return ClampRetryDelay(delta);

        if (retryAfter?.Date is { } date)
        {
            var delay =
                date - DateTimeOffset.UtcNow;
            return delay <= TimeSpan.Zero
                ? TimeSpan.Zero
                : ClampRetryDelay(delay);
        }

        var multiplier =
            1 << Math.Clamp(
                retryAttempt,
                0,
                10);
        return ClampRetryDelay(
            TimeSpan.FromMilliseconds(
                BaseRetryDelay.TotalMilliseconds
                * multiplier));
    }

    private static TimeSpan ClampRetryDelay(
        TimeSpan delay)
    {
        if (delay <= TimeSpan.Zero)
            return TimeSpan.Zero;
        return delay > MaxRetryDelay
            ? MaxRetryDelay
            : delay;
    }

    private static bool UriEquals(
        Uri left,
        Uri right)
        => left.Scheme.Equals(
               right.Scheme,
               StringComparison.OrdinalIgnoreCase)
           && left.IdnHost.Equals(
               right.IdnHost,
               StringComparison.OrdinalIgnoreCase)
           && left.Port == right.Port
           && left.PathAndQuery.Equals(
               right.PathAndQuery,
               StringComparison.Ordinal)
           && left.Fragment.Equals(
               right.Fragment,
               StringComparison.Ordinal);

    private static bool IsRedirect(
        HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;

    private static bool IsTransientStatus(
        HttpStatusCode status)
        => status is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;
}
