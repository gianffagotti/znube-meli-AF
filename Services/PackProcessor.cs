using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

public class PackProcessor
{
    private readonly MeliAuth _auth;
    private readonly IMeliApiClient _meli;
    private readonly IZnubeAllocationService _znubeAllocationService;
    private readonly INoteContentBuilder _noteContentBuilder;
    private readonly INotePersisterService _notePersisterService;
    private readonly ILogger<PackProcessor> _logger;

    private static readonly bool SendBuyerMessageEnabled = EnvVars.GetBool(EnvVars.Keys.SendBuyerMessage, true);
    private static readonly bool UpsertOrderNoteEnabled = EnvVars.GetBool(EnvVars.Keys.UpsertOrderNote, true);

    public PackProcessor(
        MeliAuth auth,
        IMeliApiClient meli,
        IZnubeAllocationService znubeAllocationService,
        INoteContentBuilder noteContentBuilder,
        INotePersisterService notePersisterService,
        ILogger<PackProcessor> logger)
    {
        _auth = auth;
        _meli = meli;
        _znubeAllocationService = znubeAllocationService;
        _noteContentBuilder = noteContentBuilder;
        _notePersisterService = notePersisterService;
        _logger = logger;
    }

    /// <summary>
    /// Processes a single order or pack (note + optional message). Locking is handled by the caller (OrderExecutionStore in Webhook).
    /// Flow: TryStartExecution -> Process -> MarkDone. Spec 02.
    /// </summary>
    public async Task<(string? OrderIdWritten, string? NoteText)> ProcessAsync(string orderIdFromWebhook)
    {
        var orderDto = await _meli.GetOrderAsync(orderIdFromWebhook);
        var order = orderDto?.ToOrder();
        if (order == null || order.DateCreatedUtc < DateTime.UtcNow.AddHours(-24))
            return (null, null);

        if (string.IsNullOrWhiteSpace(order.PackId))
            return await ProcessSingleOrderAsync(orderIdFromWebhook, order);

        var packId = order.PackId!;
        var orderDtos = await _meli.GetPackOrdersAsync(packId);
        if (orderDtos.Count == 0)
            return (null, null);
        var orders = orderDtos.Select(d => d.ToOrder()).ToList();
        var last = orders.OrderBy(o => o.DateCreatedUtc ?? DateTimeOffset.MinValue).ThenBy(o => NoteUtils.TryParseLong(o.Id)).Last();
        if (string.IsNullOrWhiteSpace(last.Id))
            return (null, null);

        var input = await BuildNoteBodyInputForPackAsync(orders, last);
        if (input == null || (input.Allocations.Count == 0 && string.IsNullOrWhiteSpace(input.Zone) && !input.AddToc))
            return (null, null);

        var body = _noteContentBuilder.BuildBody(input);
        if (string.IsNullOrWhiteSpace(body))
            return (null, null);
        var final = _noteContentBuilder.BuildFinalNote(body);

        var upserted = false;
        if (UpsertOrderNoteEnabled)
            upserted = await _notePersisterService.CreateOrderNoteAsync(last.Id!, final);
        try
        {
            if (upserted && SendBuyerMessageEnabled)
            {
                var buyerNameUpper = BuildBuyerNameUpper(orders);
                await _meli.SendMessageAsync(packId, BuildActionGuideMessage(buyerNameUpper));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al enviar mensaje action_guide para pack {PackId}", packId);
        }
        return (last.Id, final);
    }

    private async Task<(string? OrderIdWritten, string? NoteText)> ProcessSingleOrderAsync(string orderIdFromWebhook, MeliOrder order)
    {
        var notes = await _meli.GetOrderNotesAsync(orderIdFromWebhook);
        if (NoteUtils.ContainsAutoNote(notes))
            return (orderIdFromWebhook, null);

        var input = await BuildNoteBodyInputForSingleOrderAsync(order);
        if (input == null)
            return (orderIdFromWebhook, null);
        if (input.Allocations.Count == 0 && string.IsNullOrWhiteSpace(input.Zone) && !input.AddToc)
            return (orderIdFromWebhook, null);

        var body = _noteContentBuilder.BuildBody(input);
        if (string.IsNullOrWhiteSpace(body))
            return (orderIdFromWebhook, null);
        var final = _noteContentBuilder.BuildFinalNote(body);

        bool upserted = false;
        if (UpsertOrderNoteEnabled)
            upserted = await _notePersisterService.CreateOrderNoteAsync(orderIdFromWebhook, final);
        try
        {
            if (upserted && SendBuyerMessageEnabled)
            {
                var buyerNameUpper = BuildBuyerNameUpper(new[] { order });
                await _meli.SendMessageAsync(orderIdFromWebhook, BuildActionGuideMessage(buyerNameUpper));
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Fallo al enviar mensaje action_guide para order {OrderId}", orderIdFromWebhook);
        }
        return (orderIdFromWebhook, final);
    }

    private async Task<NoteBodyInput?> BuildNoteBodyInputForSingleOrderAsync(MeliOrder order)
    {
        string? zone = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(order.ShippingId))
            {
                var shipment = await _meli.GetShipmentAsync(order.ShippingId);
                if (shipment != null)
                {
                    if (shipment.IsFull())
                        return null;
                    if (shipment.IsFlex())
                        zone = shipment.GetZone();
                }
            }
        }
        catch { }

        var allocations = await _znubeAllocationService.GetAllocationsForOrderAsync(order);
        var addToc = await HasTwoOrMoreOrdersByBuyerIn24hAsync(order);
        return new NoteBodyInput { Allocations = allocations ?? new List<ZnubeAllocationEntry>(), Zone = zone, AddToc = addToc };
    }

    private async Task<NoteBodyInput?> BuildNoteBodyInputForPackAsync(List<MeliOrder> orders, MeliOrder last)
    {
        string? zone = null;
        try
        {
            if (!string.IsNullOrWhiteSpace(last.ShippingId))
            {
                var shipment = await _meli.GetShipmentAsync(last.ShippingId);
                if (shipment != null)
                {
                    if (shipment.IsFull()) return null;
                    if (shipment.IsFlex()) zone = shipment.GetZone();
                }
            }
        }
        catch { }

        var allocations = await _znubeAllocationService.GetAllocationsForOrdersAsync(orders);
        var addToc = await HasTwoOrMoreOrdersByBuyerIn24hAsync(last);
        return new NoteBodyInput { Allocations = allocations ?? new List<ZnubeAllocationEntry>(), Zone = zone, AddToc = addToc };
    }

    private async Task<bool> HasTwoOrMoreOrdersByBuyerIn24hAsync(MeliOrder order)
    {
        var sellerId = EnvVars.GetString(EnvVars.Keys.MeliSellerId);
        if (string.IsNullOrWhiteSpace(sellerId) || !order.DateCreatedUtc.HasValue || string.IsNullOrWhiteSpace(order.BuyerNickname))
            return false;
        var to = order.DateCreatedUtc.Value;
        var from = to.AddHours(-24);
        var search = await _meli.SearchOrdersAsync(long.Parse(sellerId!), order.BuyerNickname!, from.UtcDateTime, to.UtcDateTime);
        if (search?.Results == null) return false;
        var targetNick = order.BuyerNickname.Trim();
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
                if (keys.Count >= 2) return true;
            }
        }
        return false;
    }

    private static string BuildBuyerNameUpper(IEnumerable<MeliOrder> orders)
    {
        var name = orders.Select(o => o.BuyerFirstName).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                   ?? orders.Select(o => o.BuyerNickname).FirstOrDefault(n => !string.IsNullOrWhiteSpace(n))
                   ?? string.Empty;
        return name.Trim().ToUpperInvariant();
    }

    private static string BuildActionGuideMessage(string buyerNameUpper)
    {
        return "\uD83D\uDC9C ¡Hola, " + buyerNameUpper + "! Gracias por tu compra \uD83D\uDECD\uFE0F\n" +
               "¿Querés aprovechar el envío y sumar otro producto?\n" +
               "Tenemos opciones para mujer, maternal, hombre y niños,\n" +
               "¡y 3 cuotas sin interés!\n" +
               "✨ Encontranos como Victoria Garrido lencerías.\uD83D\uDC9C";
    }
}
