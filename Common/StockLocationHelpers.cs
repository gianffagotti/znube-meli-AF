using meli_znube_integration.Models;

namespace meli_znube_integration.Common;

public static class StockLocationHelpers
{
    public static string? ExtractSku(MeliItem item, MeliVariation? variation)
    {
        if (variation != null)
        {
            if (!string.IsNullOrWhiteSpace(variation.SellerSku)) return variation.SellerSku.ToUpper();

            var skuAttr = variation.Attributes.FirstOrDefault(a => a.Id == MeliConstants.SellerSkuAttributeId);
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName.ToUpper();

            if (!string.IsNullOrWhiteSpace(variation.SellerCustomField)) return variation.SellerCustomField.ToUpper();
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(item.SellerSku)) return item.SellerSku.ToUpper();

            var skuAttr = item.Attributes.FirstOrDefault(a => a.Id == MeliConstants.SellerSkuAttributeId);
            if (skuAttr != null && !string.IsNullOrWhiteSpace(skuAttr.ValueName)) return skuAttr.ValueName.ToUpper();

            if (!string.IsNullOrWhiteSpace(item.SellerCustomField)) return item.SellerCustomField.ToUpper();
        }

        return null;
    }

    public static string ExtractUserProductId(string path)
    {
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 && string.Equals(parts[0], "user-products", StringComparison.OrdinalIgnoreCase))
        {
            return parts[1];
        }
        return string.Empty;
    }
}
