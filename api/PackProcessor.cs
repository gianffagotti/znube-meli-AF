using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Api;

public class PackProcessor
{
    private readonly MeliAuth _auth;
    private readonly MeliClient _meli;
    private readonly NoteService _noteService;
    private readonly PackLockStoreBlob _lockStore;
    private readonly ILogger<PackProcessor> _logger;

    public PackProcessor(MeliAuth auth, MeliClient meli, NoteService noteService, PackLockStoreBlob lockStore, ILogger<PackProcessor> logger)
    {
        _auth = auth;
        _meli = meli;
        _noteService = noteService;
        _lockStore = lockStore;
        _logger = logger;
    }

    public async Task<(string? OrderIdWritten, string? NoteText)> ProcessAsync(string orderIdFromWebhook)
    {
        var accessToken = await _auth.GetValidAccessTokenAsync();
        var order = await _meli.GetOrderAsync(orderIdFromWebhook, accessToken);
        if (order == null || order.DateCreatedUtc < DateTime.UtcNow.AddHours(-24))
        {
            return (null, null);
        }

        // Flujo individual si no tiene pack
        if (string.IsNullOrWhiteSpace(order.PackId))
        {
            // idempotencia: si ya hay [AUTO], cortar
            if (await _meli.HasAutoNoteAsync(orderIdFromWebhook, accessToken))
            {
                return (orderIdFromWebhook, null);
            }

            var body = await _noteService.BuildSingleOrderBodyAsync(order, accessToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return (orderIdFromWebhook, null);
            }
            var final = _noteService.BuildFinalNote(body);
            await _meli.UpsertOrderNoteAsync(orderIdFromWebhook, final, accessToken);
            return (orderIdFromWebhook, final);
        }

        // Flujo pack
        var packId = order.PackId!;
        var (acquired, blob) = await _lockStore.TryAcquireAsync(packId);
        if (!acquired)
        {
            _logger.LogDebug("Lock de pack {PackId} no adquirido, otro proceso lo maneja", packId);
            return (null, null);
        }

        try
        {
            var orders = await _meli.GetOrdersByPackAsync(packId, accessToken);
            if (orders.Count == 0)
            {
                return (null, null);
            }

            var last = orders
                .OrderBy(o => o.DateCreatedUtc ?? DateTimeOffset.MinValue)
                .ThenBy(o => TryParseLong(o.Id))
                .Last();

            var body = await _noteService.BuildConsolidatedBodyAsync(orders, accessToken);
            if (string.IsNullOrWhiteSpace(body))
            {
                return (null, null);
            }
            var final = _noteService.BuildFinalNote(body);

            if (!string.IsNullOrWhiteSpace(last.Id))
            {
                await _meli.UpsertOrderNoteAsync(last.Id!, final, accessToken);
                return (last.Id, final);
            }
            return (null, null);
        }
        finally
        {
            await _lockStore.MarkDoneAsync(blob);
        }
    }

    private static long TryParseLong(string? s)
    {
        if (long.TryParse(s, out var v)) return v;
        return 0L;
    }
}


