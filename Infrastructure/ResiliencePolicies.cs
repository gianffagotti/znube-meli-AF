using Polly;
using Polly.Extensions.Http;
using Polly.Contrib.WaitAndRetry;

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
            .OrResult(msg => msg.StatusCode == System.Net.HttpStatusCode.TooManyRequests) // 429
            .WaitAndRetryAsync(delay);
    }

    private static IAsyncPolicy<HttpResponseMessage> GetBulkheadPolicy()
    {
        // MaxParallelization: 10 concurrent requests
        // MaxQueuingActions: 50 requests waiting in queue
        // If both are exceeded, it throws BulkheadRejectedException
        return Policy.BulkheadAsync<HttpResponseMessage>(10, 50);
    }
}
