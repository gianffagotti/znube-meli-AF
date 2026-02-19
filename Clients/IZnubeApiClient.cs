using meli_znube_integration.Models;

namespace meli_znube_integration.Clients;

public interface IZnubeApiClient
{
    /// <summary>
    /// GET Omnichannel/GetStock?sku={sku}. Caller should pass normalized SKU. Returns null on 404 or when API indicates SKU does not exist.
    /// </summary>
    Task<OmnichannelResponse?> GetStockBySkuAsync(string sku, CancellationToken cancellationToken = default);

    /// <summary>
    /// GET Omnichannel/GetStock?sku={productId}# with limit and offset. Returns all SKUs for the product by paginating (limit max 100). Spec 03.
    /// </summary>
    Task<OmnichannelResponse?> GetStockByProductIdAsync(string productId, CancellationToken cancellationToken = default);
}
