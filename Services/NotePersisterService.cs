using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Services;

public class NotePersisterService : INotePersisterService
{
    private readonly IMeliApiClient _meli;
    private readonly ILogger<NotePersisterService> _logger;

    public NotePersisterService(IMeliApiClient meli, ILogger<NotePersisterService> logger)
    {
        _meli = meli;
        _logger = logger;
    }

    public async Task<bool> CreateOrderNoteAsync(string orderId, string noteText, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId) || string.IsNullOrWhiteSpace(noteText))
            return false;

        var dryRun = EnvVars.GetBool(EnvVars.Keys.DryRun, false);
        if (dryRun)
        {
            _logger.LogInformation("DRY_RUN: would create order note for OrderId={OrderId}, Length={Length}", orderId, noteText.Length);
            return true;
        }

        return await _meli.CreateOrderNoteAsync(orderId, noteText, cancellationToken);
    }
}
