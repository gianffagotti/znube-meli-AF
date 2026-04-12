namespace meli_znube_integration.Core.Domain;

public sealed class ProductVariant
{
    public ProductVariant(string normalizedSku, int requiredQuantity, int currentStock)
    {
        if (string.IsNullOrWhiteSpace(normalizedSku))
            throw new ArgumentException("Normalized SKU is required.", nameof(normalizedSku));

        NormalizedSku = normalizedSku.Trim();
        RequiredQuantity = requiredQuantity;
        CurrentStock = currentStock;
    }

    public string NormalizedSku { get; }

    public int RequiredQuantity { get; }

    public int CurrentStock { get; }
}
