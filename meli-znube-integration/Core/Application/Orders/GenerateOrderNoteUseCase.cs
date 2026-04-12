using meli_znube_integration.Common;
using meli_znube_integration.Core.Domain.Orders;
using meli_znube_integration.Core.Ports;

namespace meli_znube_integration.Core.Application.Orders;

/// <summary>
/// Orchestrates load → idempotency (skipped when DRY_RUN) → expansion → Znube stock → allocation → TOC → note text → publish.
/// </summary>
public sealed class GenerateOrderNoteUseCase
{
    private readonly IOrderPort _orderPort;
    private readonly IInventoryPort _inventoryPort;
    private readonly INotePort _notePort;
    private readonly IOrderExpansionService _orderExpansion;
    private readonly IAllocationDomainService _allocationService;
    private readonly INoteFormatter _noteFormatter;

    public GenerateOrderNoteUseCase(
        IOrderPort orderPort,
        IInventoryPort inventoryPort,
        INotePort notePort,
        IOrderExpansionService orderExpansion,
        IAllocationDomainService allocationService,
        INoteFormatter noteFormatter)
    {
        _orderPort = orderPort ?? throw new ArgumentNullException(nameof(orderPort));
        _inventoryPort = inventoryPort ?? throw new ArgumentNullException(nameof(inventoryPort));
        _notePort = notePort ?? throw new ArgumentNullException(nameof(notePort));
        _orderExpansion = orderExpansion ?? throw new ArgumentNullException(nameof(orderExpansion));
        _allocationService = allocationService ?? throw new ArgumentNullException(nameof(allocationService));
        _noteFormatter = noteFormatter ?? throw new ArgumentNullException(nameof(noteFormatter));
    }

    public async Task<GenerateOrderNoteResult> ExecuteAsync(string orderId, CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(orderId))
            return new GenerateOrderNoteResult(false, null);

        var primary = await _orderPort.GetOrderAsync(orderId, cancellationToken).ConfigureAwait(false);
        if (primary == null)
            return new GenerateOrderNoteResult(false, null);

        if (primary.ShipmentType == ShipmentType.Full)
            return new GenerateOrderNoteResult(false, null);

        var dryRun = EnvVars.GetBool(EnvVars.Keys.DryRun, false);
        if (!dryRun && await _orderPort.HasNotesAsync(orderId, cancellationToken).ConfigureAwait(false))
            return new GenerateOrderNoteResult(false, null);

        var orderForExpansion = await BuildOrderForExpansionAsync(primary, cancellationToken).ConfigureAwait(false);

        var resolved = (await _orderExpansion.ExpandAsync(orderForExpansion, cancellationToken).ConfigureAwait(false)).ToList();
        if (resolved.Count == 0)
            return new GenerateOrderNoteResult(false, null);

        var stockPerSku = new Dictionary<string, List<ResourceStock>>(StringComparer.OrdinalIgnoreCase);
        foreach (var sku in resolved.Select(r => r.Sku).Where(s => !string.IsNullOrWhiteSpace(s)).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var stocks = await _inventoryPort.GetResourceStocksBySkuAsync(sku, cancellationToken).ConfigureAwait(false);
            stockPerSku[sku] = stocks.ToList();
        }

        var allocations = _allocationService.Allocate(resolved, stockPerSku);
        var addToc = await _orderPort.HasOtherOrdersFromBuyerIn24hAsync(orderForExpansion, cancellationToken).ConfigureAwait(false);
        var hasPack = resolved.Any(r => r.RuleType == OrderLineRuleType.Pack);
        var hasCombo = resolved.Any(r => r.RuleType == OrderLineRuleType.Combo);

        var note = _noteFormatter.Format(allocations, orderForExpansion.DestinationZone, addToc, hasPack, hasCombo);
        if (string.IsNullOrWhiteSpace(note))
            return new GenerateOrderNoteResult(false, null);

        var published = await _notePort.PublishNoteAsync(orderId, note, cancellationToken).ConfigureAwait(false);
        return new GenerateOrderNoteResult(published, note);
    }

    private async Task<Order> BuildOrderForExpansionAsync(Order primary, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(primary.PackId))
            return primary;

        var members = await _orderPort.GetPackOrdersAsync(primary.PackId.Trim(), cancellationToken).ConfigureAwait(false);
        if (members.Count == 0)
            return primary;

        var mergedLines = new List<OrderLine>();
        foreach (var o in members)
        {
            foreach (var line in o.Lines)
                mergedLines.Add(line);
        }

        return new Order
        {
            Id = primary.Id,
            DateCreatedUtc = primary.DateCreatedUtc,
            PackId = primary.PackId,
            ShipmentType = primary.ShipmentType,
            DestinationZone = primary.DestinationZone,
            BuyerNickname = primary.BuyerNickname,
            Lines = mergedLines,
        };
    }
}
