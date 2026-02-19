using meli_znube_integration.Models;

namespace meli_znube_integration.Services;

/// <summary>
/// Shared stock sync logic: Znube as source of truth (enrichment) and Anti-FULL/hybrid guard. Spec 03.
/// </summary>
public interface IStockSyncSourceService
{
    /// <summary>Overwrites AvailableQuantity on source items with Znube stock. Strategy: fromWorker→ProductId; !fromWorker + FULL/Combo→SKU; !fromWorker + PACK→ProductId. 404→0; 5xx/timeout→throws. Spec 03.</summary>
    Task EnrichSourceItemsWithZnubeStockAsync(List<MeliItem> sourceItems, string ruleType, bool fromWorker, CancellationToken cancellationToken = default);

    /// <summary>True if target is fulfillment and NOT hybrid (no selling_address). Skip updates in that case. Spec 03.</summary>
    Task<bool> ShouldSkipFulfillmentTargetAsync(MeliItem targetItem, CancellationToken cancellationToken = default);
}
