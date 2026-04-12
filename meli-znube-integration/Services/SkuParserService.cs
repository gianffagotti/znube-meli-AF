namespace meli_znube_integration.Services;

/// <summary>Extensible SKU parser. Default implementation uses format CODIGO#COLOR#TALLE (split by '#').</summary>
public interface ISkuParser
{
    SkuParts ParseSku(string sku);
}

public record SkuParts(string Code, string Color, string Size);

public class SkuParserService : ISkuParser
{
    public SkuParts ParseSku(string sku)
    {
        if (string.IsNullOrWhiteSpace(sku))
        {
            return new SkuParts(string.Empty, string.Empty, string.Empty);
        }

        var parts = sku.Split('#');
        var code = parts.Length > 0 ? parts[0] : string.Empty;
        var color = parts.Length > 1 ? parts[1] : string.Empty;
        var size = parts.Length > 2 ? parts[2] : string.Empty;

        return new SkuParts(code, color, size);
    }
}
