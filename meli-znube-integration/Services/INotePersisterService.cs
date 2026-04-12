namespace meli_znube_integration.Services;

/// <summary>
/// Persists order notes via MELI API. Supports DRY_RUN (log only). Spec 02.
/// </summary>
public interface INotePersisterService
{
    /// <summary>Calls MELI CreateOrderNote. If DRY_RUN=true, only logs and returns true.</summary>
    Task<bool> CreateOrderNoteAsync(string orderId, string noteText, CancellationToken cancellationToken = default);
}
