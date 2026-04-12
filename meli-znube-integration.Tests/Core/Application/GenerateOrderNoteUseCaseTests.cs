using FluentAssertions;
using meli_znube_integration.Common;
using meli_znube_integration.Core.Application.Orders;
using meli_znube_integration.Core.Domain.Orders;
using meli_znube_integration.Core.Ports;
using Moq;

namespace meli_znube_integration.Tests.Core.Application;

// Serial: tests in this class share process-global DRY_RUN (see EnvVars); avoids races with When_dry_run_*.
[CollectionDefinition(nameof(GenerateOrderNoteUseCaseTestsCollection), DisableParallelization = true)]
public sealed class GenerateOrderNoteUseCaseTestsCollection;

[Collection(nameof(GenerateOrderNoteUseCaseTestsCollection))]
public sealed class GenerateOrderNoteUseCaseTests
{
    [Fact]
    public async Task Happy_path_calls_dependencies_in_order_and_publishes()
    {
        var order = new Order
        {
            Id = "O1",
            DestinationZone = null,
            Lines =
            [
                new OrderLine { MarketplaceItemId = "M1", Quantity = 1, Title = "T", SellerSku = "SKU1" },
            ],
        };
        var resolved = new List<ResolvedOrderLine>
        {
            new() { Sku = "SKU1", Quantity = 1, Label = "T", RuleType = OrderLineRuleType.Standard },
        };
        var allocations = new List<AllocationResult>
        {
            new()
            {
                ResourceName = "Dep",
                Sku = "SKU1",
                AllocatedQuantity = 1,
                Label = "T",
            },
        };

        var step = 0;
        var orderPort = new Mock<IOrderPort>(MockBehavior.Strict);
        orderPort.Setup(o => o.GetOrderAsync("O1", It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                step.Should().Be(0);
                step++;
            })
            .ReturnsAsync(order);
        orderPort.Setup(o => o.HasNotesAsync("O1", It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                step.Should().Be(1);
                step++;
            })
            .ReturnsAsync(false);
        orderPort.Setup(o => o.HasOtherOrdersFromBuyerIn24hAsync(order, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                step.Should().Be(5);
                step++;
            })
            .ReturnsAsync(false);

        var expansion = new Mock<IOrderExpansionService>(MockBehavior.Strict);
        expansion.Setup(e => e.ExpandAsync(order, It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                step.Should().Be(2);
                step++;
            })
            .ReturnsAsync(resolved);

        var inventory = new Mock<IInventoryPort>(MockBehavior.Strict);
        inventory.Setup(i => i.GetResourceStocksBySkuAsync("SKU1", It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                step.Should().Be(3);
                step++;
            })
            .ReturnsAsync(new List<ResourceStock> { new() { ResourceName = "Dep", AvailableQuantity = 5 } });

        var allocator = new Mock<IAllocationDomainService>(MockBehavior.Strict);
        allocator.Setup(a => a.Allocate(It.IsAny<List<ResolvedOrderLine>>(), It.IsAny<Dictionary<string, List<ResourceStock>>>()))
            .Callback(() =>
            {
                step.Should().Be(4);
                step++;
            })
            .Returns(allocations);

        var formatter = new Mock<INoteFormatter>(MockBehavior.Strict);
        formatter.Setup(f => f.Format(allocations, order.DestinationZone, false, false, false))
            .Callback(() =>
            {
                step.Should().Be(6);
                step++;
            })
            .Returns("[A] test");

        var notePort = new Mock<INotePort>(MockBehavior.Strict);
        notePort.Setup(n => n.PublishNoteAsync("O1", "[A] test", It.IsAny<CancellationToken>()))
            .Callback(() =>
            {
                step.Should().Be(7);
                step++;
            })
            .ReturnsAsync(true);

        var sut = new GenerateOrderNoteUseCase(
            orderPort.Object,
            inventory.Object,
            notePort.Object,
            expansion.Object,
            allocator.Object,
            formatter.Object);

        var prevDryRun = Environment.GetEnvironmentVariable(EnvVars.Keys.DryRun);
        try
        {
            // Process-global env: other test classes (or parallel workers) may set DRY_RUN=true.
            Environment.SetEnvironmentVariable(EnvVars.Keys.DryRun, "false");

            var result = await sut.ExecuteAsync("O1");

            result.NotePublished.Should().BeTrue();
            result.NoteText.Should().Be("[A] test");

            orderPort.Verify(o => o.GetOrderAsync("O1", It.IsAny<CancellationToken>()), Times.Once);
            expansion.Verify(e => e.ExpandAsync(order, It.IsAny<CancellationToken>()), Times.Once);
            notePort.Verify(n => n.PublishNoteAsync("O1", "[A] test", It.IsAny<CancellationToken>()), Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.DryRun, prevDryRun);
        }
    }

    [Fact]
    public async Task Pack_merges_lines_from_GetPackOrdersAsync_before_expand()
    {
        var lineA = new OrderLine { MarketplaceItemId = "M1", Quantity = 1, Title = "A", SellerSku = "S1" };
        var lineB = new OrderLine { MarketplaceItemId = "M2", Quantity = 2, Title = "B", SellerSku = "S2" };

        var primary = new Order
        {
            Id = "O-last",
            PackId = "PACK1",
            ShipmentType = ShipmentType.Standard,
            Lines = [lineA],
        };
        var other = new Order
        {
            Id = "O-other",
            PackId = "PACK1",
            ShipmentType = ShipmentType.Standard,
            Lines = [lineB],
        };

        var resolved = new List<ResolvedOrderLine>
        {
            new() { Sku = "S1", Quantity = 1, Label = "A", RuleType = OrderLineRuleType.Standard },
            new() { Sku = "S2", Quantity = 2, Label = "B", RuleType = OrderLineRuleType.Standard },
        };

        var orderPort = new Mock<IOrderPort>(MockBehavior.Strict);
        orderPort.Setup(o => o.GetOrderAsync("O-last", It.IsAny<CancellationToken>())).ReturnsAsync(primary);
        orderPort.Setup(o => o.HasNotesAsync("O-last", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        orderPort.Setup(o => o.GetPackOrdersAsync("PACK1", It.IsAny<CancellationToken>())).ReturnsAsync(new List<Order> { primary, other });
        orderPort.Setup(o => o.HasOtherOrdersFromBuyerIn24hAsync(
                It.Is<Order>(o => o.Id == "O-last" && o.Lines.Count == 2),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(false);

        var expansion = new Mock<IOrderExpansionService>(MockBehavior.Strict);
        expansion.Setup(e => e.ExpandAsync(
                It.Is<Order>(o => o.Id == "O-last" && o.Lines.Count == 2 && ReferenceEquals(o.Lines[0], lineA) && ReferenceEquals(o.Lines[1], lineB)),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(resolved);

        var inventory = new Mock<IInventoryPort>(MockBehavior.Strict);
        inventory.Setup(i => i.GetResourceStocksBySkuAsync("S1", It.IsAny<CancellationToken>())).ReturnsAsync(new List<ResourceStock>());
        inventory.Setup(i => i.GetResourceStocksBySkuAsync("S2", It.IsAny<CancellationToken>())).ReturnsAsync(new List<ResourceStock>());

        var allocator = new Mock<IAllocationDomainService>(MockBehavior.Strict);
        allocator.Setup(a => a.Allocate(It.IsAny<List<ResolvedOrderLine>>(), It.IsAny<Dictionary<string, List<ResourceStock>>>()))
            .Returns(new List<AllocationResult>());

        var formatter = new Mock<INoteFormatter>(MockBehavior.Strict);
        formatter.Setup(f => f.Format(It.IsAny<IReadOnlyList<AllocationResult>>(), It.IsAny<string?>(), false, false, false))
            .Returns("[A] pack");

        var notePort = new Mock<INotePort>(MockBehavior.Strict);
        notePort.Setup(n => n.PublishNoteAsync("O-last", "[A] pack", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var sut = new GenerateOrderNoteUseCase(
            orderPort.Object,
            inventory.Object,
            notePort.Object,
            expansion.Object,
            allocator.Object,
            formatter.Object);

        var result = await sut.ExecuteAsync("O-last");

        result.NotePublished.Should().BeTrue();
        orderPort.Verify(o => o.GetPackOrdersAsync("PACK1", It.IsAny<CancellationToken>()), Times.Once);
        expansion.Verify(e => e.ExpandAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task When_notes_already_exist_skips_expansion_and_publish()
    {
        var order = new Order { Id = "O1", Lines = [] };
        var orderPort = new Mock<IOrderPort>();
        orderPort.Setup(o => o.GetOrderAsync("O1", It.IsAny<CancellationToken>())).ReturnsAsync(order);
        orderPort.Setup(o => o.HasNotesAsync("O1", It.IsAny<CancellationToken>())).ReturnsAsync(true);

        var expansion = new Mock<IOrderExpansionService>();
        var notePort = new Mock<INotePort>();

        var sut = new GenerateOrderNoteUseCase(
            orderPort.Object,
            Mock.Of<IInventoryPort>(),
            notePort.Object,
            expansion.Object,
            Mock.Of<IAllocationDomainService>(),
            Mock.Of<INoteFormatter>());

        var result = await sut.ExecuteAsync("O1");

        result.NotePublished.Should().BeFalse();
        expansion.Verify(e => e.ExpandAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Never);
        notePort.Verify(n => n.PublishNoteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task When_dry_run_true_does_not_query_HasNotesAsync()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVars.Keys.DryRun);
        try
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.DryRun, "true");

            var order = new Order
            {
                Id = "O1",
                Lines = [new OrderLine { MarketplaceItemId = "M1", Quantity = 1, Title = "T", SellerSku = "S" }],
            };

            var orderPort = new Mock<IOrderPort>(MockBehavior.Strict);
            orderPort.Setup(o => o.GetOrderAsync("O1", It.IsAny<CancellationToken>())).ReturnsAsync(order);

            var expansion = new Mock<IOrderExpansionService>(MockBehavior.Strict);
            expansion.Setup(e => e.ExpandAsync(order, It.IsAny<CancellationToken>())).ReturnsAsync(new List<ResolvedOrderLine>());

            var sut = new GenerateOrderNoteUseCase(
                orderPort.Object,
                Mock.Of<IInventoryPort>(),
                Mock.Of<INotePort>(),
                expansion.Object,
                Mock.Of<IAllocationDomainService>(),
                Mock.Of<INoteFormatter>());

            var result = await sut.ExecuteAsync("O1");

            result.NotePublished.Should().BeFalse();
            orderPort.Verify(o => o.HasNotesAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.DryRun, prev);
        }
    }

    [Fact]
    public async Task When_formatted_note_empty_does_not_publish()
    {
        var order = new Order
        {
            Id = "O1",
            Lines = [new OrderLine { MarketplaceItemId = "M1", Quantity = 1, Title = "T", SellerSku = "S" }],
        };
        var resolved = new List<ResolvedOrderLine>
        {
            new() { Sku = "S", Quantity = 1, Label = "T", RuleType = OrderLineRuleType.Standard },
        };
        var orderPort = new Mock<IOrderPort>();
        orderPort.Setup(o => o.GetOrderAsync("O1", It.IsAny<CancellationToken>())).ReturnsAsync(order);
        orderPort.Setup(o => o.HasNotesAsync("O1", It.IsAny<CancellationToken>())).ReturnsAsync(false);
        orderPort.Setup(o => o.HasOtherOrdersFromBuyerIn24hAsync(order, It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var expansion = new Mock<IOrderExpansionService>();
        expansion.Setup(e => e.ExpandAsync(order, It.IsAny<CancellationToken>())).ReturnsAsync(resolved);

        var inventory = new Mock<IInventoryPort>();
        inventory.Setup(i => i.GetResourceStocksBySkuAsync("S", It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<ResourceStock>());

        var allocator = new Mock<IAllocationDomainService>();
        allocator.Setup(a => a.Allocate(It.IsAny<List<ResolvedOrderLine>>(), It.IsAny<Dictionary<string, List<ResourceStock>>>()))
            .Returns(new List<AllocationResult>());

        var formatter = new Mock<INoteFormatter>();
        formatter.Setup(f => f.Format(It.IsAny<IReadOnlyList<AllocationResult>>(), It.IsAny<string?>(), It.IsAny<bool>(), It.IsAny<bool>(), It.IsAny<bool>()))
            .Returns((string?)null);

        var notePort = new Mock<INotePort>();

        var sut = new GenerateOrderNoteUseCase(
            orderPort.Object,
            inventory.Object,
            notePort.Object,
            expansion.Object,
            allocator.Object,
            formatter.Object);

        var result = await sut.ExecuteAsync("O1");

        result.NotePublished.Should().BeFalse();
        notePort.Verify(n => n.PublishNoteAsync(It.IsAny<string>(), It.IsAny<string>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task When_full_shipment_skips_expansion_and_publish()
    {
        var order = new Order
        {
            Id = "O1",
            ShipmentType = ShipmentType.Full,
            Lines = [new OrderLine { MarketplaceItemId = "M1", Quantity = 1, Title = "T", SellerSku = "S" }],
        };
        var orderPort = new Mock<IOrderPort>();
        orderPort.Setup(o => o.GetOrderAsync("O1", It.IsAny<CancellationToken>())).ReturnsAsync(order);
        orderPort.Setup(o => o.HasNotesAsync("O1", It.IsAny<CancellationToken>())).ReturnsAsync(false);

        var expansion = new Mock<IOrderExpansionService>(MockBehavior.Strict);

        var sut = new GenerateOrderNoteUseCase(
            orderPort.Object,
            Mock.Of<IInventoryPort>(),
            Mock.Of<INotePort>(),
            expansion.Object,
            Mock.Of<IAllocationDomainService>(),
            Mock.Of<INoteFormatter>());

        var result = await sut.ExecuteAsync("O1");

        result.NotePublished.Should().BeFalse();
        expansion.Verify(e => e.ExpandAsync(It.IsAny<Order>(), It.IsAny<CancellationToken>()), Times.Never);
    }
}
