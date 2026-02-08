using meli_znube_integration.Models;

namespace meli_znube_integration.Clients;

public interface IZnubeApiClient
{
    /// <summary>
    /// GET Omnichannel/GetStock?sku={sku}. Caller should pass normalized SKU. Returns null on 404 or when API indicates SKU does not exist.
    /// </summary>
    Task<OmnichannelResponse?> GetStockBySkuAsync(string sku, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET Omnichannel/GetStock?sku={productId}#. Used when assignment cannot be resolved by SKU alone.
    /// </summary>
    Task<OmnichannelResponse?> GetStockByProductIdAsync(string productId, CancellationToken cancellationToken = default);
}
