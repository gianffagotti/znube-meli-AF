using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Infrastructure;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

public class PackProcessor
{
    private readonly MeliAuth _auth;
    private readonly IMeliApiClient _meli;
    private readonly NoteService _noteService;
    private readonly PackLockStoreBlob _lockStore;
    private readonly ILogger<PackProcessor> _logger;

    private static readonly bool SendBuyerMessageEnabled = EnvVars.GetBool(EnvVars.Keys.SendBuyerMessage, true);
    private static readonly bool UpsertOrderNoteEnabled = EnvVars.GetBool(EnvVars.Keys.UpsertOrderNote, true);

    public PackProcessor(MeliAuth auth, IMeliApiClient meli, NoteService noteService, PackLockStoreBlob lockStore, ILogger<PackProcessor> logger)
    {
        _auth = auth;
        _meli = meli;
        _noteService = noteService;
        _lockStore = lockStore;
        _logger = logger;
    }

    public async Task<(string? OrderIdWritten, string? NoteText)> ProcessAsync(string orderIdFromWebhook)
    {
        var orderDto = await _meli.GetOrderAsync(orderIdFromWebhook);
        var order = orderDto?.ToOrder();
        if (order == null || order.DateCreatedUtc < DateTime.UtcNow.AddHours(-24))
            return (null, null);

        if (string.IsNullOrWhiteSpace(order.PackId))
        {
            var notes = await _meli.GetOrderNotesAsync(orderIdFromWebhook);
            if (NoteUtils.ContainsAutoNote(notes))
                return (orderIdFromWebhook, null);

            var body = await _noteService.BuildSingleOrderBodyAsync(order);
            if (string.IsNullOrWhiteSpace(body))
                return (orderIdFromWebhook, null);
            var final = _noteService.BuildFinalNote(body);
            var upserted = false;
            if (UpsertOrderNoteEnabled)
                upserted = await _meli.CreateOrderNoteAsync(orderIdFromWebhook, final);
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
                _logger.LogWarning(ex, "Fallo al enviar mensaje action_guide para order {PackId}", orderIdFromWebhook);
            }
            return (orderIdFromWebhook, final);
        }

        var packId = order.PackId!;
        var (acquired, blob) = await _lockStore.TryAcquireAsync(packId);
        if (!acquired)
        {
            _logger.LogDebug("Lock de pack {PackId} no adquirido, otro proceso lo maneja", packId);
            return (null, null);
        }

        try
        {
            var orderDtos = await _meli.GetPackOrdersAsync(packId);
            if (orderDtos.Count == 0)
                return (null, null);
            var orders = orderDtos.Select(d => d.ToOrder()).ToList();
            var last = orders.Last();

            var body = await _noteService.BuildConsolidatedBodyAsync(orders);
            if (string.IsNullOrWhiteSpace(body))
                return (null, null);
            var final = _noteService.BuildFinalNote(body);

            if (string.IsNullOrWhiteSpace(last.Id))
                return (null, null);
            var upserted = false;
            if (UpsertOrderNoteEnabled)
                upserted = await _meli.CreateOrderNoteAsync(last.Id!, final);
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
        finally
        {
            await _lockStore.MarkDoneAsync(blob);
        }
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
