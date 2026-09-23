using System.Net;
using System.Text;
using UN.Nexo.Core.Models;
using UN.Nexo.Core.Services;

namespace UN.Nexo.Core.Tests;

internal static class RestrictedRegionRegression
{
    internal static async Task RunAsync()
    {
        await AssertStateAsync("fl=1\nip=203.0.113.5\nloc=CN\n", RegionalAccessState.RestrictedRegion);
        await AssertStateAsync("fl=1\nip=203.0.113.5\nloc=RU\n", RegionalAccessState.RestrictedRegion);
        await AssertStateAsync("fl=1\nip=203.0.113.5\nloc=CA\n", RegionalAccessState.OutsideRestrictedRegion);
        await AssertStateAsync("fl=1\nip=203.0.113.5\n", RegionalAccessState.Unknown);
        await AssertStateAsync("loc=CN\nloc=RU\n", RegionalAccessState.Unknown);
        await AssertStateAsync("loc=CHINA\n", RegionalAccessState.Unknown);
        await AssertOversizedResponseFailsClosedAsync();
        await AssertUnknownLengthOversizedResponseFailsClosedAsync();
        await AssertHttpFailureFailsClosedAsync();
        await AssertFallbackRequiresVerifiedMicrosoftAsync();
    }

    private static async Task AssertStateAsync(string body, RegionalAccessState expected)
    {
        using var client = new HttpClient(new StaticHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(body, Encoding.UTF8, "text/plain")
        }));
        var actual = await new RestrictedRegionService(client).DetectAsync();
        if (actual != expected)
            throw new InvalidOperationException($"Regional access state mismatch: expected {expected}, got {actual}.");
    }

    private static async Task AssertOversizedResponseFailsClosedAsync()
    {
        using var client = new HttpClient(new StaticHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new ByteArrayContent(new byte[16 * 1024 + 1])
        }));
        var actual = await new RestrictedRegionService(client).DetectAsync();
        if (actual != RegionalAccessState.Unknown)
            throw new InvalidOperationException("Oversized regional trace responses must fail closed.");
    }

    private static async Task AssertUnknownLengthOversizedResponseFailsClosedAsync()
    {
        using var client = new HttpClient(new StaticHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new UnknownLengthContent(new byte[16 * 1024 + 1])
        }));
        var actual = await new RestrictedRegionService(client).DetectAsync();
        if (actual != RegionalAccessState.Unknown)
            throw new InvalidOperationException("Unknown-length oversized regional trace responses must fail closed.");
    }

    private static async Task AssertHttpFailureFailsClosedAsync()
    {
        using var client = new HttpClient(new StaticHandler(new HttpResponseMessage(HttpStatusCode.ServiceUnavailable)));
        var actual = await new RestrictedRegionService(client).DetectAsync();
        if (actual != RegionalAccessState.Unknown)
            throw new InvalidOperationException("Regional trace HTTP failures must fail closed.");
    }

    private static async Task AssertFallbackRequiresVerifiedMicrosoftAsync()
    {
        var now = DateTimeOffset.Parse(
            "2026-09-23T12:00:00Z",
            System.Globalization.CultureInfo.InvariantCulture);
        var baseAccount = new LauncherAccount(
            "microsoft:1234567890abcdef1234567890abcdef",
            "microsoft",
            "NexoTester",
            "12345678-90ab-cdef-1234-567890abcdef",
            now)
        {
            AuthenticationId = "home.test"
        };

        if (await CanFallbackAsync(baseAccount, "CN", now))
            throw new InvalidOperationException(
                "A Microsoft profile without entitlement evidence must not gain offline fallback.");

        var fresh = baseAccount with
        {
            EntitlementVerifiedAt = now - TimeSpan.FromDays(1)
        };
        if (!await CanFallbackAsync(fresh, "CN", now))
            throw new InvalidOperationException(
                "Fresh entitlement evidence in CN should allow restricted-region fallback.");

        var boundary = baseAccount with
        {
            EntitlementVerifiedAt =
                now - RestrictedRegionService.MaxEntitlementEvidenceAge
        };
        if (!await CanFallbackAsync(boundary, "RU", now))
            throw new InvalidOperationException(
                "Entitlement evidence exactly at the documented age boundary should remain eligible.");

        var stale = baseAccount with
        {
            EntitlementVerifiedAt =
                now
                - RestrictedRegionService.MaxEntitlementEvidenceAge
                - TimeSpan.FromSeconds(1)
        };
        if (await CanFallbackAsync(stale, "CN", now))
            throw new InvalidOperationException(
                "Stale entitlement evidence must require online re-verification.");

        var future = baseAccount with
        {
            EntitlementVerifiedAt =
                now
                + RestrictedRegionService.MaxFutureClockSkew
                + TimeSpan.FromSeconds(1)
        };
        if (await CanFallbackAsync(future, "CN", now))
            throw new InvalidOperationException(
                "Implausibly future entitlement evidence must be rejected.");

        if (await CanFallbackAsync(fresh, "CA", now))
            throw new InvalidOperationException(
                "Fresh evidence outside a restricted region must remain ineligible.");
        if (await CanFallbackAsync(fresh, "BAD", now))
            throw new InvalidOperationException(
                "Unknown regional state must remain ineligible.");

        var offline = new LauncherAccount(
            "offline:NexoTester",
            "offline",
            "NexoTester",
            "12345678-90ab-cdef-1234-567890abcdef",
            now)
        {
            EntitlementVerifiedAt = now
        };
        if (await CanFallbackAsync(offline, "CN", now))
            throw new InvalidOperationException(
                "A generic offline profile must never qualify for restricted-region fallback.");
    }

    private static async Task<bool> CanFallbackAsync(
        LauncherAccount account,
        string country,
        DateTimeOffset now)
    {
        var body = country.Length == 2
            ? $"ip=203.0.113.5\nloc={country}\n"
            : "ip=203.0.113.5\n";
        using var client = new HttpClient(
            new StaticHandler(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        body,
                        Encoding.UTF8,
                        "text/plain")
                }));
        var service = new RestrictedRegionService(
            client,
            new FixedTimeProvider(now));
        return await service.CanUseOfflineFallbackAsync(account);
    }


    private sealed class FixedTimeProvider(
        DateTimeOffset now) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => now;
    }

    private sealed class UnknownLengthContent(byte[] bytes) : HttpContent
    {
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context)
            => stream.WriteAsync(bytes).AsTask();

        protected override bool TryComputeLength(out long length)
        {
            length = 0;
            return false;
        }
    }

    private sealed class StaticHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            response.RequestMessage = request;
            return Task.FromResult(response);
        }
    }
}
