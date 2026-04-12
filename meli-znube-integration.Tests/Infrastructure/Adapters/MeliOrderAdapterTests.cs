using FluentAssertions;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Core.Domain.Orders;
using meli_znube_integration.Infrastructure.Adapters.MercadoLibre;
using meli_znube_integration.Models.Dtos;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;

namespace meli_znube_integration.Tests.Infrastructure.Adapters;

public class MeliOrderAdapterTests
{
    private readonly Mock<IMeliApiClient> _meli = new();

    private MeliOrderAdapter CreateSut() => new(_meli.Object, NullLogger<MeliOrderAdapter>.Instance);

    [Fact]
    public async Task GetOrderAsync_maps_flex_zone_and_lines()
    {
        var orderId = "12345";
        var dto = new MeliOrderDto
        {
            Id = orderId,
            PackId = "pack-1",
            DateCreated = "2024-01-15T10:00:00.000Z",
            Shipping = new MeliOrderShippingDto { Id = "ship-1" },
            Buyer = new MeliOrderBuyerDto { Nickname = "buyer1" },
            OrderItems =
            [
                new MeliOrderItemDto
                {
                    Quantity = 2,
                    Item = new MeliItemShortDto
                    {
                        Id = "MLA111",
                        Title = "Product A",
                        SellerSku = "SKU-A"
                    }
                }
            ]
        };

        var shipment = new MeliShipmentDto
        {
            Logistic = new MeliLogisticDto { Type = "self_service" },
            Destination = new MeliDestinationDto
            {
                ShippingAddress = new MeliReceiverAddressDto
                {
                    City = new MeliCityDto { Name = "Zona Norte" }
                }
            }
        };

        _meli.Setup(c => c.GetOrderAsync(orderId, default)).ReturnsAsync(dto);
        _meli.Setup(c => c.GetShipmentAsync("ship-1", default)).ReturnsAsync(shipment);

        var order = await CreateSut().GetOrderAsync(orderId);

        order.Should().NotBeNull();
        order!.Id.Should().Be(orderId);
        order.PackId.Should().Be("pack-1");
        order.BuyerNickname.Should().Be("buyer1");
        order.ShipmentType.Should().Be(ShipmentType.Flex);
        order.DestinationZone.Should().Be("Zona Norte");
        order.Lines.Should().HaveCount(1);
        order.Lines[0].MarketplaceItemId.Should().Be("MLA111");
        order.Lines[0].Quantity.Should().Be(2);
        order.Lines[0].Title.Should().Be("Product A");
        order.Lines[0].SellerSku.Should().Be("SKU-A");
    }

    [Fact]
    public async Task GetOrderAsync_maps_full_shipment()
    {
        var dto = new MeliOrderDto
        {
            Id = "99",
            DateCreated = "2024-06-01T12:00:00.000Z",
            Shipping = new MeliOrderShippingDto { Id = "s-full" },
            OrderItems = []
        };
        var shipment = new MeliShipmentDto
        {
            Logistic = new MeliLogisticDto { Type = "fulfillment" }
        };

        _meli.Setup(c => c.GetOrderAsync("99", default)).ReturnsAsync(dto);
        _meli.Setup(c => c.GetShipmentAsync("s-full", default)).ReturnsAsync(shipment);

        var order = await CreateSut().GetOrderAsync("99");

        order!.ShipmentType.Should().Be(ShipmentType.Full);
        order.DestinationZone.Should().BeNull();
    }

    [Fact]
    public async Task GetOrderAsync_maps_standard_when_logistic_is_not_flex_or_full()
    {
        var dto = new MeliOrderDto
        {
            Id = "1",
            Shipping = new MeliOrderShippingDto { Id = "s-std" },
            OrderItems = []
        };
        var shipment = new MeliShipmentDto
        {
            Logistic = new MeliLogisticDto { Type = "xd_drop_off" }
        };

        _meli.Setup(c => c.GetOrderAsync("1", default)).ReturnsAsync(dto);
        _meli.Setup(c => c.GetShipmentAsync("s-std", default)).ReturnsAsync(shipment);

        var order = await CreateSut().GetOrderAsync("1");

        order!.ShipmentType.Should().Be(ShipmentType.Standard);
    }

    [Fact]
    public async Task GetPackOrdersAsync_reuses_shipment_cache_for_shared_shipping_id()
    {
        var o1 = new MeliOrderDto
        {
            Id = "10",
            Shipping = new MeliOrderShippingDto { Id = "shared-ship" },
            OrderItems = []
        };
        var o2 = new MeliOrderDto
        {
            Id = "11",
            Shipping = new MeliOrderShippingDto { Id = "shared-ship" },
            OrderItems = []
        };
        var shipment = new MeliShipmentDto { Logistic = new MeliLogisticDto { Type = "fulfillment" } };

        _meli.Setup(c => c.GetPackOrdersAsync("p1", default)).ReturnsAsync(new List<MeliOrderDto> { o1, o2 });
        _meli.Setup(c => c.GetShipmentAsync("shared-ship", default)).ReturnsAsync(shipment);

        var orders = await CreateSut().GetPackOrdersAsync("p1");

        orders.Should().HaveCount(2);
        _meli.Verify(c => c.GetShipmentAsync("shared-ship", default), Times.Once);
    }

    [Fact]
    public async Task HasNotesAsync_true_when_note_text_present()
    {
        _meli.Setup(c => c.GetOrderNotesAsync("1", default)).ReturnsAsync(new List<string> { "hello" });

        var has = await CreateSut().HasNotesAsync("1");

        has.Should().BeTrue();
    }

    [Fact]
    public async Task HasNotesAsync_false_when_empty_list()
    {
        _meli.Setup(c => c.GetOrderNotesAsync("1", default)).ReturnsAsync(new List<string>());

        var has = await CreateSut().HasNotesAsync("1");

        has.Should().BeFalse();
    }

    [Fact]
    public async Task PublishNoteAsync_dry_run_skips_create()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVars.Keys.DryRun);
        try
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.DryRun, "true");

            var ok = await CreateSut().PublishNoteAsync("1", "note body");

            ok.Should().BeTrue();
            _meli.Verify(c => c.CreateOrderNoteAsync(It.IsAny<string>(), It.IsAny<string>(), default), Times.Never);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.DryRun, prev);
        }
    }

    [Fact]
    public async Task PublishNoteAsync_calls_create_when_not_dry_run()
    {
        var prev = Environment.GetEnvironmentVariable(EnvVars.Keys.DryRun);
        try
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.DryRun, "false");
            _meli.Setup(c => c.CreateOrderNoteAsync("1", "x", default)).ReturnsAsync(true);

            var ok = await CreateSut().PublishNoteAsync("1", "x");

            ok.Should().BeTrue();
            _meli.Verify(c => c.CreateOrderNoteAsync("1", "x", default), Times.Once);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.DryRun, prev);
        }
    }

    [Fact]
    public async Task HasOtherOrdersFromBuyerIn24hAsync_true_when_two_distinct_keys()
    {
        var prevSeller = Environment.GetEnvironmentVariable(EnvVars.Keys.MeliSellerId);
        try
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.MeliSellerId, "100");

            var search = new MeliSearchResponseDto
            {
                Results =
                [
                    new MeliSearchResultDto
                    {
                        Id = "o1",
                        PackId = "p1",
                        Buyer = new MeliOrderBuyerDto { Nickname = "nick" }
                    },
                    new MeliSearchResultDto
                    {
                        Id = "o2",
                        PackId = "p2",
                        Buyer = new MeliOrderBuyerDto { Nickname = "nick" }
                    }
                ]
            };

            _meli.Setup(c => c.SearchOrdersAsync(100, "nick", It.IsAny<DateTime>(), It.IsAny<DateTime>(), default))
                .ReturnsAsync(search);

            var reference = new Order
            {
                Id = "x",
                DateCreatedUtc = new DateTime(2024, 3, 1, 15, 0, 0, DateTimeKind.Utc),
                ShipmentType = ShipmentType.Standard,
                BuyerNickname = "nick",
                Lines = []
            };

            var result = await CreateSut().HasOtherOrdersFromBuyerIn24hAsync(reference);

            result.Should().BeTrue();
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.MeliSellerId, prevSeller);
        }
    }

    [Fact]
    public async Task HasOtherOrdersFromBuyerIn24hAsync_false_when_seller_missing()
    {
        var prevSeller = Environment.GetEnvironmentVariable(EnvVars.Keys.MeliSellerId);
        try
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.MeliSellerId, null);

            var reference = new Order
            {
                Id = "x",
                DateCreatedUtc = DateTime.UtcNow,
                ShipmentType = ShipmentType.Standard,
                BuyerNickname = "nick",
                Lines = []
            };

            var result = await CreateSut().HasOtherOrdersFromBuyerIn24hAsync(reference);

            result.Should().BeFalse();
            _meli.Verify(c => c.SearchOrdersAsync(It.IsAny<long>(), It.IsAny<string>(), It.IsAny<DateTime>(), It.IsAny<DateTime>(), default), Times.Never);
        }
        finally
        {
            Environment.SetEnvironmentVariable(EnvVars.Keys.MeliSellerId, prevSeller);
        }
    }
}
