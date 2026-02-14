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
    Task<bool> SendMessageAsync(string packOrOrderId, string text, string optionId = "OTHER", CancellationToken cancellationToken = default);
    Task<MeliScanResponseDto?> ScanItemsAsync(long userId, string? scrollId, CancellationToken cancellationToken = default);
    Task<List<MeliItem>> GetItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default);
    Task<(int Quantity, string Version)?> GetUserProductStockAsync(string userProductId, CancellationToken cancellationToken = default);
    /// <summary>Returns full stock response with locations. Used for hybrid (selling_address) check. Spec 03.</summary>
    Task<MeliUserProductStockResponseDto?> GetUserProductStockResponseAsync(string userProductId, CancellationToken cancellationToken = default);
    Task<bool> UpdateUserProductStockAsync(string userProductId, int quantity, string version, CancellationToken cancellationToken = default);
    Task<MeliSearchResponseDto?> SearchItemsAsync(long sellerId, MeliItemSearchQuery query, CancellationToken cancellationToken = default);
}
