using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace meli_znube_integration.Infrastructure;

public class MeliRateLimiter
{
    // Semaphore to control the delay logic execution to ensure thread safety when updating state
    private readonly SemaphoreSlim _lock = new(1, 1);
    
    // How long to wait before the next request. 
    // This is updated based on the x-ratelimit-remaining header.
    private DateTimeOffset _nextRequestAllowedAt = DateTimeOffset.MinValue;
    
    private const int MinRemainingThreshold = 5; // If remaining requests are below this, we slow down.
    private const int DelaySecondsWhenLow = 2;   // Delay to inject when quota is low.

    public async Task WaitIfRequiredAsync(CancellationToken cancellationToken)
    {
        // Quick check without lock first
        var delay = _nextRequestAllowedAt - DateTimeOffset.UtcNow;
        if (delay <= TimeSpan.Zero)
        {
            return;
        }

        // If we need to wait, we wait. 
        // Note: Multiple threads might wait here. This is intended (Bulkhead will limit total concurrency anyway).
        await Task.Delay(delay, cancellationToken);
    }

    public void UpdateQuota(HttpResponseHeaders headers)
    {
        // Try to get x-ratelimit-remaining
        if (headers.TryGetValues("x-ratelimit-remaining", out var values))
        {
            var remainingStr = values.FirstOrDefault();
            if (int.TryParse(remainingStr, out var remaining))
            {
                if (remaining <= MinRemainingThreshold)
                {
                    // If we are running low on quota, force a small delay for subsequent requests
                    // to allow the bucket to refill (assuming a sliding window or leaky bucket).
                    // We don't know the exact reset time always, so a conservative delay helps.
                    UpdateNextRequestTime(TimeSpan.FromSeconds(DelaySecondsWhenLow));
                }
            }
        }
    }

    private void UpdateNextRequestTime(TimeSpan delay)
    {
        var nextTime = DateTimeOffset.UtcNow.Add(delay);
        
        // Only update if the new time is further in the future
        if (nextTime > _nextRequestAllowedAt)
        {
            _nextRequestAllowedAt = nextTime;
        }
    }
}
