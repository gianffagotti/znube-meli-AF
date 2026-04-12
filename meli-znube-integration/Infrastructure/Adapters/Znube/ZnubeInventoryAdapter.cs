using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Core.Domain.Orders;
using meli_znube_integration.Core.Ports;
using meli_znube_integration.Models;

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

        var normalized = ZnubeLogicExtensions.NormalizeSellerSku(sku);
        var response = await _znubeApiClient.GetStockBySkuAsync(normalized).ConfigureAwait(false);
        if (response?.Data?.Stock == null)
            return 0;

        var skuItem = response.Data.Stock.FirstOrDefault(s =>
            !string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, normalized, StringComparison.OrdinalIgnoreCase));
        if (skuItem?.Stock == null)
            return 0;

        var sum = skuItem.Stock.Sum(d => d.Quantity);
        return (int)Math.Max(0, sum);
    }

    public async Task<IReadOnlyList<ResourceStock>> GetResourceStocksBySkuAsync(string sku, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(sku))
            return Array.Empty<ResourceStock>();

        var normalizedSku = ZnubeLogicExtensions.NormalizeSellerSku(sku);
        var dto = await _znubeApiClient.GetStockBySkuAsync(normalizedSku, cancellationToken).ConfigureAwait(false);
        if (dto?.Data == null)
            return Array.Empty<ResourceStock>();

        var data = dto.Data;
        var resources = ZnubeLogicExtensions.BuildResourcesMap(data);
        OmnichannelStockItem? skuItem = null;
        foreach (var s in data.Stock ?? [])
        {
            if (!string.IsNullOrWhiteSpace(s.Sku) && string.Equals(s.Sku, normalizedSku, StringComparison.OrdinalIgnoreCase))
            {
                skuItem = s;
                break;
            }
        }

        if (skuItem == null)
            return Array.Empty<ResourceStock>();

        var list = new List<ResourceStock>();
        foreach (var st in skuItem.Stock ?? [])
        {
            if (st.Quantity <= 0.0 || string.IsNullOrWhiteSpace(st.ResourceId) || !resources.TryGetValue(st.ResourceId, out var info))
                continue;

            var available = (int)Math.Floor(st.Quantity);
            if (available > 0)
                list.Add(new ResourceStock { ResourceName = info.Name ?? string.Empty, AvailableQuantity = available });
        }

        return list;
    }
}
