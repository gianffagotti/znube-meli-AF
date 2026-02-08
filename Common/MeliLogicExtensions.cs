using meli_znube_integration.Models.Dtos;

namespace meli_znube_integration.Common;

public static class MeliShipmentDtoExtensions
{
    /// <summary>
    /// Returns true if shipment logistic type is "self_service" or "flex" (case-insensitive).
    /// No EnvVars dependency; uses API constants.
    /// </summary>
    public static bool IsFlex(this MeliShipmentDto? shipment)
    {
        if (shipment == null || string.IsNullOrWhiteSpace(shipment.LogisticType))
            return false;
        var t = shipment.LogisticType.Trim();
        return string.Equals(t, "self_service", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "flex", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Returns true if shipment logistic type is "fulfillment" or "full" (case-insensitive).
    /// </summary>
    public static bool IsFull(this MeliShipmentDto? shipment)
    {
        if (shipment == null || string.IsNullOrWhiteSpace(shipment.LogisticType))
            return false;
        var t = shipment.LogisticType.Trim();
        return string.Equals(t, "fulfillment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "full", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Gets zone from destination.shipping_address.city.name, fallback to receiver_address.city.name.
    /// </summary>
    public static string? GetZone(this MeliShipmentDto? shipment)
    {
        if (shipment == null)
            return null;
        var zone = shipment.Destination?.ShippingAddress?.City?.Name;
        if (!string.IsNullOrWhiteSpace(zone))
            return zone;
        return shipment.ReceiverAddress?.City?.Name;
    }
}
