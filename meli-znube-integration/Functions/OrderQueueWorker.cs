using System.Text.Json;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Infrastructure;
using meli_znube_integration.Models.Dtos;
using meli_znube_integration.Services;
using Microsoft.Azure.Functions.Worker;
using Microsoft.Extensions.Logging;

namespace meli_znube_integration.Functions;

public class OrderQueueWorker
{
    private readonly PackProcessor _processor;
    private readonly IMeliApiClient _meliClient;
    private readonly IOrderExecutionStore _orderExecutionStore;
    private readonly ILogger<OrderQueueWorker> _logger;

    public OrderQueueWorker(
        PackProcessor processor,
        IMeliApiClient meliClient,
        IOrderExecutionStore orderExecutionStore,
        ILogger<OrderQueueWorker> logger)
    {
        _processor = processor;
        _meliClient = meliClient;
        _orderExecutionStore = orderExecutionStore;
        _logger = logger;
    }

    [Function("OrderQueueWorker")]
    public async Task Run(
        [QueueTrigger("%ORDERS_WEBHOOK_QUEUE_NAME%", Connection = "AZURE_STORAGE_CONNECTION_STRING")] string message)
    {
        try
        {
            OrderQueueMessage? payload;
            try
            {
                payload = JsonSerializer.Deserialize<OrderQueueMessage>(message);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Invalid orders queue message payload.");
                return;
            }

            if (payload == null || string.IsNullOrWhiteSpace(payload.Resource))
            {
                _logger.LogWarning("Orders queue message missing resource.");
                return;
            }

            var (isValid, orderId) = WebhookOrderResourceHelper.TryParseOrderIdFromResource(payload.Resource);
            if (!isValid || string.IsNullOrWhiteSpace(orderId))
            {
                _logger.LogDebug("webhook ignorado: resource sin orderId válido: {Resource}", payload.Resource);
                return;
            }

            // Resolve execution key: PackId (group) or OrderId (single). Spec 02.
            // If GetOrderAsync returns null (order not found), executionKey = orderId; ProcessAsync returns (null,null); we still MarkDoneAsync to avoid stuck executions.
            var orderDto = await _meliClient.GetOrderAsync(orderId);
            var executionKey = !string.IsNullOrWhiteSpace(orderDto?.PackId) ? orderDto.PackId : orderId;

            var dryRun = EnvVars.GetBool(EnvVars.Keys.DryRun, false);
            if (!dryRun && !await _orderExecutionStore.TryStartExecutionAsync(executionKey))
            {
                _logger.LogDebug("Order/Pack {Key} already locked or done. Returning.", executionKey);
                return;
            }

            try
            {
                var (orderIdWritten, noteText) = await _processor.ProcessAsync(orderId, orderDto);
                _logger.LogInformation("Nota: {NoteText}", noteText);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error procesando orden {OrderId}", orderId);
                throw;
            }
            finally
            {
                await _orderExecutionStore.MarkDoneAsync(executionKey);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Error crítico procesando mensaje de orders_v2.");
            throw;
        }
    }
}
