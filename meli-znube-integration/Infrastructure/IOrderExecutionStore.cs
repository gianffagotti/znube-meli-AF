namespace meli_znube_integration.Infrastructure;

/// <summary>
/// Order/pack execution store for idempotency and concurrency. Spec 02.
/// </summary>
public interface IOrderExecutionStore
{
    Task<bool> TryStartExecutionAsync(string key, CancellationToken cancellationToken = default);
    Task MarkDoneAsync(string key, CancellationToken cancellationToken = default);
}
