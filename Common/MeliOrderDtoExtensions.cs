using meli_znube_integration.Clients;
using meli_znube_integration.Models.Dtos;

namespace meli_znube_integration.Common;

public static class MeliOrderDtoExtensions
{
    /// <summary>
    /// Maps API DTO to domain MeliOrder for use with ZnubeAllocationService and note builders.
    /// </summary>
    public static MeliOrder ToOrder(this MeliOrderDto? dto)
    {
        if (dto == null)
            return new MeliOrder { Items = new List<MeliOrderItem>() };

        var items = new List<MeliOrderItem>();
        foreach (var oi in dto.OrderItems ?? new List<MeliOrderItemDto>())
        {
            var title = oi.Item?.Title?.Trim();
            if (string.IsNullOrEmpty(title)) title = "(sin título)";
            var sku = oi.Item?.SellerSku ?? oi.SellerSku;
            var qty = oi.Quantity <= 0 ? 1 : oi.Quantity;
            items.Add(new MeliOrderItem { Title = title!, SellerSku = sku, Quantity = qty });
        }

        DateTimeOffset? dateCreatedUtc = null;
        if (!string.IsNullOrWhiteSpace(dto.DateCreated) && DateTimeOffset.TryParse(dto.DateCreated, out var parsed))
            dateCreatedUtc = parsed.ToUniversalTime();

        return new MeliOrder
        {
            Id = dto.Id,
            PackId = dto.PackId,
            ShippingId = dto.Shipping?.Id,
            DateCreatedUtc = dateCreatedUtc,
            BuyerNickname = dto.Buyer?.Nickname,
            BuyerFirstName = dto.Buyer?.FirstName,
            Items = items
        };
    }
}
