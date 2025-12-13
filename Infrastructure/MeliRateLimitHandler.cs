using System.Net;

namespace meli_znube_integration.Infrastructure;

public class MeliRateLimitHandler : DelegatingHandler
{
    private readonly MeliRateLimiter _rateLimiter;

    public MeliRateLimitHandler(MeliRateLimiter rateLimiter)
    {
        _rateLimiter = rateLimiter;
    }

    protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
    {
        // 1. Proactive Rate Limiting Check
        await _rateLimiter.WaitIfRequiredAsync(cancellationToken);

        // 2. Send Request
        var response = await base.SendAsync(request, cancellationToken);

        // 3. Inspect Headers to Update Quota
        _rateLimiter.UpdateQuota(response.Headers);

        return response;
    }
}
