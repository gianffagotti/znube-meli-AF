namespace meli_znube_integration.Common;

/// <summary>
/// Constantes de la API de Mercado Libre (atributos, prefijos, opciones). Centraliza literales para reutilización.
/// </summary>
public static class MeliConstants
{
    /// <summary>ID del atributo SKU del vendedor en variaciones/ítems.</summary>
    public const string SellerSkuAttributeId = "SELLER_SKU";

    /// <summary>Prefijo de ID de ítem de Mercado Libre (ej. MLA123456789). Usado para distinguir itemId de user_product_id.</summary>
    public const string ItemIdPrefixMla = "MLA";

    /// <summary>Opción por defecto al enviar mensaje al comprador (option_id).</summary>
    public const string MessageOptionIdOther = "OTHER";
}
