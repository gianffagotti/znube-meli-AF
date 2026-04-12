namespace meli_znube_integration.Common;

/// <summary>
/// Helper for parsing and validating orders_v2 webhook resource. Spec 02.
/// Extracted for testability (ORD-07).
/// </summary>
public static class WebhookOrderResourceHelper
{
    /// <summary>
    /// Tries to parse a valid order ID from the resource path.
    /// Returns (false, null) if resource is invalid: null/empty, missing /orders/, or orderId non-numeric.
    /// </summary>
    public static (bool IsValid, string? OrderId) TryParseOrderIdFromResource(string? resource)
    {
        if (string.IsNullOrWhiteSpace(resource))
            return (false, null);

        if (resource.IndexOf("/orders/", StringComparison.OrdinalIgnoreCase) < 0)
            return (false, null);

        var orderId = ExtractLastSegment(resource);
        if (string.IsNullOrWhiteSpace(orderId) || !orderId.Trim().All(char.IsDigit))
            return (false, null);

        return (true, orderId.Trim());
    }

    internal static string ExtractLastSegment(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
            return string.Empty;
        var parts = path.Split('/', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length > 0 ? parts[^1] : string.Empty;
    }
}
