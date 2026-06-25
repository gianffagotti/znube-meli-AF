using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;

namespace meli_znube_integration.Clients;

public interface IMeliApiClient
{
    Task<MeliShipmentDto?> GetShipmentAsync(string shipmentId, CancellationToken cancellationToken = default);
    Task<MeliOrderDto?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default);
    Task<List<MeliOrderDto>> GetPackOrdersAsync(string packId, CancellationToken cancellationToken = default);
    Task<bool> CreateOrderNoteAsync(string orderId, string note, CancellationToken cancellationToken = default);
    Task<List<string>> GetOrderNotesAsync(string orderId, CancellationToken cancellationToken = default);
    Task<MeliSearchResponseDto?> SearchOrdersAsync(long sellerId, string buyerNickname, DateTime from, DateTime to, CancellationToken cancellationToken = default);
    Task<bool> SendMessageAsync(string packOrOrderId, string text, string optionId = MeliConstants.MessageOptionIdOther, CancellationToken cancellationToken = default);
    Task<MeliScanResponseDto?> ScanItemsAsync(long userId, string? scrollId, CancellationToken cancellationToken = default);
    Task<List<MeliItem>> GetItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);
    Task<(int Quantity, string Version)?> GetUserProductStockAsync(string userProductId, CancellationToken cancellationToken = default);
    /// <summary>Returns full stock response with locations. Used for hybrid (selling_address) check. Spec 03.</summary>
    Task<MeliUserProductStockResponseDto?> GetUserProductStockResponseAsync(string userProductId, CancellationToken cancellationToken = default);
    Task<bool> UpdateUserProductStockAsync(string userProductId, int quantity, string version, CancellationToken cancellationToken = default);
    Task<MeliSearchResponseDto?> SearchItemsAsync(long sellerId, MeliItemSearchQuery query, CancellationToken cancellationToken = default);

    /// <summary>Fetches a User Products Family (new catalog model) by id. Returns null on blank id or non-success.</summary>
    Task<MeliUserProductsFamilyDto?> GetUserProductsFamilyAsync(string familyId, CancellationToken cancellationToken = default);

    /// <summary>Resolves a set of MLAU ids to technical detail (SKU) via GET /user_products?ids=…. Returns empty on failure.</summary>
    Task<List<MeliUserProductDto>> ResolveUserProductsAsync(IEnumerable<string> userProductIds, CancellationToken cancellationToken = default);
}
