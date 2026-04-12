using meli_znube_integration.Clients;
using meli_znube_integration.Core.Ports;

namespace meli_znube_integration.Infrastructure.Adapters.Znube;

public sealed class ZnubeInventoryAdapter : IInventoryPort
{
    private readonly IZnubeApiClient _znubeApiClient;

    public ZnubeInventoryAdapter(IZnubeApiClient znubeApiClient)
    {
        _znubeApiClient = znubeApiClient ?? throw new ArgumentNullException(nameof(znubeApiClient));
    }

    public async Task<int> GetAvailableStockAsync(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return 0;

        var response = await _znubeApiClient.GetStockBySkuAsync(sku).ConfigureAwait(false);
        if (response?.Data?.Stock == null)
            return 0;

        var skuItem = response.Data.Stock.FirstOrDefault(s =>
            !string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, sku, StringComparison.OrdinalIgnoreCase));
        if (skuItem?.Stock == null)
            return 0;

        var sum = skuItem.Stock.Sum(d => d.Quantity);
        return (int)Math.Max(0, sum);
    }
}
