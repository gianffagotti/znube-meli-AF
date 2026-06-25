using meli_znube_integration.Models;

namespace meli_znube_integration.Common;

/// <summary>
/// Logistics eligibility helpers for <see cref="MeliItem"/> (which carries <see cref="MeliShipping"/>,
/// not the order-side <c>MeliShipmentDto</c>). Used to enforce the unbreakable FULL+FLEX filter for the UI proxy.
/// </summary>
public static class MeliItemLogisticsExtensions
{
    /// <summary>True if the item's logistic type is FULL (fulfillment/full) or FLEX (self_service/flex). False for any other or blank type.</summary>
    public static bool IsFullOrFlexLogistics(this MeliItem? item)
    {
        var t = item?.Shipping?.LogisticType;
        if (string.IsNullOrWhiteSpace(t)) return false;
        t = t.Trim();
        return string.Equals(t, "fulfillment", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "full", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "self_service", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "flex", StringComparison.OrdinalIgnoreCase)
            || string.Equals(t, "cross_docking", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// True when the item's lifecycle status is "active" or unknown (null/empty).
    /// A null status means the field was not projected — treat as active rather than silently dropping the item.
    /// </summary>
    public static bool IsActive(this MeliItem? item)
    {
        var s = item?.Status;
        if (string.IsNullOrWhiteSpace(s)) return true;
        return string.Equals(s.Trim(), "active", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Combined eligibility for the UI proxy: must be FULL+FLEX logistics AND active (or unknown) status.</summary>
    public static bool IsEligibleForUi(this MeliItem? item) =>
        item.IsFullOrFlexLogistics() && item.IsActive();
}
