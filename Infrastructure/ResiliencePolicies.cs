using Polly;
using Polly.Contrib.WaitAndRetry;
using Polly.Extensions.Http;
using System.Net;

namespace meli_znube_integration.Infrastructure;

public static class ResiliencePolicies
{
    public static IAsyncPolicy<HttpResponseMessage> GetMeliResiliencePolicy()
    {
        var retryPolicy = GetRetryPolicy();
        var bulkheadPolicy = GetBulkheadPolicy();

        // Wrap policies: Bulkhead (outer) -> Retry (inner)
        // Note: In Polly, the policy on the left wraps the policy on the right.
        // However, Policy.WrapAsync(outer, inner) is the standard way.
        // For HttpClientFactory's AddPolicyHandler, we usually return a single policy.
        // If we want Bulkhead to limit concurrency of *executions* (including retries), it should be outside.
        // But typically we want to limit the number of *concurrent network requests*.
        // Actually, for HttpClient, the policies are applied in the order they are added or wrapped.
        // A common pattern is Retry wrapping the execution.
        // But for Bulkhead, we want to limit the total number of active requests.
        
        // Let's use a Wrap.
        return Policy.WrapAsync(retryPolicy, bulkheadPolicy);
    }

    private static IAsyncPolicy<HttpResponseMessage> GetRetryPolicy()
    {
        // Jittered Backoff
        // MedianFirstRetryDelay: 2 seconds
        // RetryCount: 5
        var delay = Backoff.DecorrelatedJitterBackoffV2(medianFirstRetryDelay: TimeSpan.FromSeconds(2), retryCount: 5);

        return HttpPolicyExtensions
            .HandleTransientHttpError() // 5xx, 408
            .OrResult(msg => msg.StatusCode == HttpStatusCode.TooManyRequests) // 429
            .OrResult(msg => msg.StatusCode == HttpStatusCode.Conflict) // 409
            .WaitAndRetryAsync(delay);
    }

    private static IAsyncPolicy<HttpResponseMessage> GetBulkheadPolicy()
    {
        // MaxParallelization: 10 concurrent requests
        // MaxQueuingActions: 50 requests waiting in queue
        // If both are exceeded, it throws BulkheadRejectedException
        return Policy.BulkheadAsync<HttpResponseMessage>(10, 50);
    }

    /// <summary>Retry on 5xx for Znube. Spec 03: Znube 5xx → retry.</summary>
    public static IAsyncPolicy<HttpResponseMessage> GetZnubeResiliencePolicy()
    {
        var delay = Backoff.DecorrelatedJitterBackoffV2(medianFirstRetryDelay: TimeSpan.FromSeconds(2), retryCount: 3);
        return HttpPolicyExtensions
            .HandleTransientHttpError()
            .WaitAndRetryAsync(delay);
    }
}
