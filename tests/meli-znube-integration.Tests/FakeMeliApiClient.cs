using meli_znube_integration.Clients;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;

namespace meli_znube_integration.Tests;

public sealed class FakeMeliApiClient : IMeliApiClient
{
    public List<string> FamilyRequests { get; } = [];
    public List<string> UserProductRequests { get; } = [];
    public List<MeliItemSearchQuery> Searches { get; } = [];
    public List<string[]> ItemRequests { get; } = [];

    public Func<string, IReadOnlyList<string>>? OnFamily { get; set; }
    public Func<string, MeliUserProductDto?>? OnUserProduct { get; set; }
    public Func<MeliItemSearchQuery, MeliSearchResponseDto?>? OnSearch { get; set; }
    public Func<IReadOnlyList<string>, List<MeliItem>>? OnItems { get; set; }

    public Task<IReadOnlyList<string>> GetUserProductFamilyAsync(string familyId, CancellationToken cancellationToken = default)
    {
        FamilyRequests.Add(familyId);
        IReadOnlyList<string> ids = OnFamily?.Invoke(familyId) ?? [];
        return Task.FromResult(ids);
    }

    public Task<MeliUserProductDto?> GetUserProductAsync(string userProductId, CancellationToken cancellationToken = default)
    {
        UserProductRequests.Add(userProductId);
        return Task.FromResult(OnUserProduct?.Invoke(userProductId));
    }

    public Task<MeliSearchResponseDto?> SearchItemsAsync(long sellerId, MeliItemSearchQuery query, CancellationToken cancellationToken = default)
    {
        Searches.Add(query);
        return Task.FromResult(OnSearch?.Invoke(query));
    }

    public Task<List<MeliItem>> GetItemsAsync(IEnumerable<string> ids, CancellationToken cancellationToken = default)
    {
        var list = ids.ToList();
        ItemRequests.Add(list.ToArray());
        return Task.FromResult(OnItems?.Invoke(list) ?? []);
    }

    public Task<MeliShipmentDto?> GetShipmentAsync(string shipmentId, CancellationToken cancellationToken = default) => Task.FromResult<MeliShipmentDto?>(null);
    public Task<MeliOrderDto?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default) => Task.FromResult<MeliOrderDto?>(null);
    public Task<List<MeliOrderDto>> GetPackOrdersAsync(string packId, CancellationToken cancellationToken = default) => Task.FromResult(new List<MeliOrderDto>());
    public Task<bool> CreateOrderNoteAsync(string orderId, string note, CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<List<string>> GetOrderNotesAsync(string orderId, CancellationToken cancellationToken = default) => Task.FromResult(new List<string>());
    public Task<MeliSearchResponseDto?> SearchOrdersAsync(long sellerId, string buyerNickname, DateTime from, DateTime to, CancellationToken cancellationToken = default) => Task.FromResult<MeliSearchResponseDto?>(null);
    public Task<bool> SendMessageAsync(string packOrOrderId, string text, string optionId = "OTHER", CancellationToken cancellationToken = default) => Task.FromResult(false);
    public Task<MeliScanResponseDto?> ScanItemsAsync(long userId, string? scrollId, CancellationToken cancellationToken = default) => Task.FromResult<MeliScanResponseDto?>(null);
    public Task<(int Quantity, string Version)?> GetUserProductStockAsync(string userProductId, CancellationToken cancellationToken = default) => Task.FromResult<(int, string)?>(null);
    public Task<MeliUserProductStockResponseDto?> GetUserProductStockResponseAsync(string userProductId, CancellationToken cancellationToken = default) => Task.FromResult<MeliUserProductStockResponseDto?>(null);
    public Task<bool> UpdateUserProductStockAsync(string userProductId, int quantity, string version, CancellationToken cancellationToken = default) => Task.FromResult(false);
}
