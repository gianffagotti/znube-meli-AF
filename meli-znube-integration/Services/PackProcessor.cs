using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Core.Application.Orders;
using meli_znube_integration.Models;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

public class PackProcessor
{
    private readonly IMeliApiClient _meli;
    private readonly GenerateOrderNoteUseCase _generateOrderNote;
    private readonly ILogger<PackProcessor> _logger;

    private static readonly bool SendBuyerMessageEnabled = EnvVars.GetBool(EnvVars.Keys.SendBuyerMessage, true);

    public PackProcessor(
        IMeliApiClient meli,
        GenerateOrderNoteUseCase generateOrderNote,
        ILogger<PackProcessor> logger)
    {
        _meli = meli;
        _generateOrderNote = generateOrderNote;
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

        var dryRun = EnvVars.GetBool(EnvVars.Keys.DryRun, false);

        var order = orderDto.ToOrder();
        if (order == null || (!dryRun && order.DateCreatedUtc < DateTime.UtcNow.AddHours(-24)))
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

        var notes = await _meli.GetOrderNotesAsync(last.Id!);
        if (!dryRun && notes.Count != 0)
        {
            return (null, null);
        }

        var noteResult = await _generateOrderNote.ExecuteAsync(last.Id!).ConfigureAwait(false);

        if (!noteResult.NotePublished || string.IsNullOrWhiteSpace(noteResult.NoteText))
            return (orderIdFromWebhook, null);

        try
        {
            if (SendBuyerMessageEnabled)
            {
                var buyerNameUpper = BuildBuyerNameUpper(orders);
                var messageTargetId = isPack ? packId : orderIdFromWebhook;
                if (!string.IsNullOrWhiteSpace(messageTargetId))
                {
                    if (dryRun)
                    {
                        _logger.LogInformation("DRY_RUN: would send buyer message for {TargetId}", messageTargetId);
                    }
                    else
                    {
                        await _meli.SendMessageAsync(messageTargetId!, BuildActionGuideMessage(buyerNameUpper));
                    }
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

        return (last.Id, noteResult.NoteText);
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
