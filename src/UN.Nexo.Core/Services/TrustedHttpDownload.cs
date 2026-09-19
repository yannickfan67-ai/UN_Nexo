using System.Net;

namespace UN.Nexo.Core.Services;

internal static class TrustedHttpDownload
{
    private const int MaxRedirects = 5;

    internal static async Task<HttpResponseMessage> SendGetAsync(
        HttpClient client,
        string url,
        string label,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(client);
        var current = TrustedDownloadPolicy.RequireTrustedUri(url, label);

        for (var redirectCount = 0; ; redirectCount++)
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            var response = await client.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);

            var effective = response.RequestMessage?.RequestUri;
            if (effective is not null && !UriEquals(effective, current))
            {
                response.Dispose();
                throw new InvalidDataException(
                    $"{label} HTTP client followed a redirect automatically. Automatic redirects must be disabled so Nexo can validate every target before connecting.");
            }

            if (!IsRedirect(response.StatusCode))
                return response;

            if (redirectCount >= MaxRedirects)
            {
                response.Dispose();
                throw new InvalidDataException(
                    $"{label} exceeded the {MaxRedirects}-redirect safety limit.");
            }

            var location = response.Headers.Location;
            if (location is null)
            {
                response.Dispose();
                throw new InvalidDataException($"{label} returned a redirect without a Location header.");
            }

            Uri next;
            try
            {
                next = location.IsAbsoluteUri ? location : new Uri(current, location);
            }
            catch (UriFormatException ex)
            {
                response.Dispose();
                throw new InvalidDataException($"{label} returned an invalid redirect target.", ex);
            }

            response.Dispose();
            current = TrustedDownloadPolicy.RequireTrustedUri(next.AbsoluteUri, $"{label} redirect");
        }
    }

    private static bool UriEquals(Uri left, Uri right)
        => left.Scheme.Equals(right.Scheme, StringComparison.OrdinalIgnoreCase)
           && left.IdnHost.Equals(right.IdnHost, StringComparison.OrdinalIgnoreCase)
           && left.Port == right.Port
           && left.PathAndQuery.Equals(right.PathAndQuery, StringComparison.Ordinal)
           && left.Fragment.Equals(right.Fragment, StringComparison.Ordinal);

    private static bool IsRedirect(HttpStatusCode status)
        => status is HttpStatusCode.MovedPermanently
            or HttpStatusCode.Found
            or HttpStatusCode.SeeOther
            or HttpStatusCode.TemporaryRedirect
            or HttpStatusCode.PermanentRedirect;
}
