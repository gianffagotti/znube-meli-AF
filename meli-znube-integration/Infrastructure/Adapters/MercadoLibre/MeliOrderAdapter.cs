using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Core.Domain.Orders;
using meli_znube_integration.Core.Ports;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Infrastructure.Adapters.MercadoLibre;

/// <summary>
/// Mercado Libre ACL for <see cref="IOrderPort"/> and <see cref="INotePort"/> using <see cref="IMeliApiClient"/> only.
/// </summary>
public sealed class MeliOrderAdapter : IOrderPort, INotePort
{
    private readonly IMeliApiClient _meli;
    private readonly ILogger<MeliOrderAdapter> _logger;

    public MeliOrderAdapter(IMeliApiClient meli, ILogger<MeliOrderAdapter> logger)
    {
        _meli = meli ?? throw new ArgumentNullException(nameof(meli));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task<Order?> GetOrderAsync(string orderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            return null;

        var dto = await _meli.GetOrderAsync(orderId.Trim(), cancellationToken).ConfigureAwait(false);
        if (dto == null)
            return null;

        var shipmentCache = new Dictionary<string, MeliShipmentDto?>(StringComparer.Ordinal);
        return await MapToOrderAsync(dto, shipmentCache, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<Order>> GetPackOrdersAsync(string packId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(packId))
            return Array.Empty<Order>();

        var dtos = await _meli.GetPackOrdersAsync(packId.Trim(), cancellationToken).ConfigureAwait(false);
        if (dtos.Count == 0)
            return Array.Empty<Order>();

        var shipmentCache = new Dictionary<string, MeliShipmentDto?>(StringComparer.Ordinal);
        var list = new List<Order>(dtos.Count);
        foreach (var dto in dtos)
        {
            list.Add(await MapToOrderAsync(dto, shipmentCache, cancellationToken).ConfigureAwait(false));
        }

        return list;
    }

    public async Task<bool> HasNotesAsync(string orderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            return false;

        var notes = await _meli.GetOrderNotesAsync(orderId.Trim(), cancellationToken).ConfigureAwait(false);
        return notes.Any(static n => !string.IsNullOrWhiteSpace(n));
    }

    public async Task<bool> HasOtherOrdersFromBuyerIn24hAsync(Order referenceOrder, CancellationToken cancellationToken = default)
    {
        var sellerId = EnvVars.GetString(EnvVars.Keys.MeliSellerId);
        if (string.IsNullOrWhiteSpace(sellerId)
            || !referenceOrder.DateCreatedUtc.HasValue
            || string.IsNullOrWhiteSpace(referenceOrder.BuyerNickname))
            return false;

        var to = referenceOrder.DateCreatedUtc.Value;
        var from = to.AddHours(-24);
        var search = await _meli.SearchOrdersAsync(
                long.Parse(sellerId, System.Globalization.CultureInfo.InvariantCulture),
                referenceOrder.BuyerNickname.Trim(),
                from,
                to,
                cancellationToken)
            .ConfigureAwait(false);

        if (search?.Results == null)
            return false;

        var targetNick = referenceOrder.BuyerNickname.Trim();
        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var r in search.Results)
        {
            var resultNick = r.Buyer?.Nickname?.Trim();
            if (string.IsNullOrWhiteSpace(resultNick) || string.IsNullOrWhiteSpace(targetNick)
                || !string.Equals(resultNick, targetNick, StringComparison.OrdinalIgnoreCase))
                continue;

            var key = r.PackId ?? r.Id;
            if (!string.IsNullOrWhiteSpace(key))
            {
                keys.Add(key!);
                if (keys.Count >= 2)
                    return true;
            }
        }

        return false;
    }

    public async Task<bool> PublishNoteAsync(string orderId, string noteContent, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(noteContent))
            return false;

        var dryRun = EnvVars.GetBool(EnvVars.Keys.DryRun, false);
        if (dryRun)
        {
            _logger.LogInformation(
                "DRY_RUN: would create order note for OrderId={OrderId}, Length={Length}",
                orderId,
                noteContent.Length);
            return true;
        }

        return await _meli.CreateOrderNoteAsync(orderId, noteContent, cancellationToken).ConfigureAwait(false);
    }

    private async Task<Order> MapToOrderAsync(
        MeliOrderDto dto,
        Dictionary<string, MeliShipmentDto?> shipmentCache,
        CancellationToken cancellationToken)
    {
        DateTime? dateCreatedUtc = null;
        if (!string.IsNullOrWhiteSpace(dto.DateCreated) && DateTimeOffset.TryParse(dto.DateCreated, out var parsed))
            dateCreatedUtc = parsed.UtcDateTime;

        var lines = new List<OrderLine>();
        foreach (var oi in dto.OrderItems ?? new List<MeliOrderItemDto>())
        {
            var title = oi.Item?.Title?.Trim();
            if (string.IsNullOrEmpty(title))
                title = "(sin título)";
            var qty = oi.Quantity <= 0 ? 1 : oi.Quantity;
            lines.Add(new OrderLine
            {
                MarketplaceItemId = oi.Item?.Id?.Trim() ?? string.Empty,
                Quantity = qty,
                Title = title,
                SellerSku = oi.Item?.SellerSku ?? oi.SellerSku
            });
        }

        MeliShipmentDto? shipment = null;
        var shippingId = dto.Shipping?.Id;
        if (!string.IsNullOrWhiteSpace(shippingId))
        {
            if (!shipmentCache.TryGetValue(shippingId, out shipment))
            {
                shipment = await _meli.GetShipmentAsync(shippingId, cancellationToken).ConfigureAwait(false);
                shipmentCache[shippingId] = shipment;
            }
        }

        var shipmentType = ShipmentType.Standard;
        string? destinationZone = null;
        if (shipment != null)
        {
            if (shipment.IsFlex())
            {
                shipmentType = ShipmentType.Flex;
                destinationZone = shipment.GetZone();
            }
            else if (shipment.IsFull())
            {
                shipmentType = ShipmentType.Full;
            }
        }

        return new Order
        {
            Id = dto.Id?.Trim() ?? string.Empty,
            DateCreatedUtc = dateCreatedUtc,
            PackId = dto.PackId,
            ShipmentType = shipmentType,
            DestinationZone = destinationZone,
            BuyerNickname = dto.Buyer?.Nickname,
            Lines = lines
        };
    }
}
