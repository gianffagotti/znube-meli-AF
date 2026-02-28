using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
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
    public async Task<(string? OrderIdWritten, string? NoteText)> ProcessAsync(string orderIdFromWebhook, MeliOrderDto? orderDto = null)
    {
        if (orderDto == null)
            return (null, null);

        var order = orderDto.ToOrder();
        if (order == null || order.DateCreatedUtc < DateTime.UtcNow.AddHours(-24))
            return (null, null);

        var isPack = !string.IsNullOrWhiteSpace(order.PackId);
        var packId = order.PackId;
        var orders = new List<MeliOrder>();

        if (isPack)
        {
            var orderDtos = await _meli.GetPackOrdersAsync(packId!);
            if (orderDtos.Count == 0)
                return (null, null);
            orders = orderDtos.Select(d => d.ToOrder()).ToList();
        }
        else
        {
            orders.Add(order);
        }

        var last = orders.Last();
        if (string.IsNullOrWhiteSpace(last.Id))
            return (null, null);

        var dryRun = EnvVars.GetBool(EnvVars.Keys.DryRun, false);
        var notes = await _meli.GetOrderNotesAsync(last.Id!);
        if (!dryRun && notes.Count != 0)
        {
            return (null, null);
        }

        var input = await BuildNoteBodyInputAsync(orders, last, isPack);
        return await WriteNoteAsync(orderIdFromWebhook, orders, last, input, isPack, packId);
    }

    private async Task<NoteBodyInput?> BuildNoteBodyInputAsync(List<MeliOrder> orders, MeliOrder last, bool isPack)
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

        var allocationResult = await _znubeAllocationService.GetAllocationsForOrdersAsync(orders);
        if (allocationResult == null)
            return null;

        var addToc = await HasTwoOrMoreOrdersByBuyerIn24hAsync(last);
        return new NoteBodyInput
        {
            Allocations = allocationResult.Allocations,
            Zone = zone,
            AddToc = addToc,
            HasPack = allocationResult.HasPack,
            HasCombo = allocationResult.HasCombo
        };
    }

    private async Task<(string? OrderIdWritten, string? NoteText)> WriteNoteAsync(
        string orderIdFromWebhook,
        List<MeliOrder> orders,
        MeliOrder last,
        NoteBodyInput? input,
        bool isPack,
        string? packId)
    {
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
            upserted = await _notePersisterService.CreateOrderNoteAsync(last.Id!, final);
        try
        {
            if (upserted && SendBuyerMessageEnabled)
            {
                var buyerNameUpper = BuildBuyerNameUpper(orders);
                var messageTargetId = isPack ? packId : orderIdFromWebhook;
                if (!string.IsNullOrWhiteSpace(messageTargetId))
                {
                    await _meli.SendMessageAsync(messageTargetId!, BuildActionGuideMessage(buyerNameUpper));
                }
            }
        }
        catch (Exception ex)
        {
            if (isPack)
                _logger.LogWarning(ex, "Fallo al enviar mensaje action_guide para pack {PackId}", packId);
            else
                _logger.LogWarning(ex, "Fallo al enviar mensaje action_guide para order {OrderId}", orderIdFromWebhook);
        }
        return (last.Id, final);
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
