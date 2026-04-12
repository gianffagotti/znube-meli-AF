using FluentAssertions;
using meli_znube_integration.Clients;
using meli_znube_integration.Common;
using meli_znube_integration.Infrastructure.Adapters.Znube;
using meli_znube_integration.Models;
using Moq;

namespace meli_znube_integration.Tests.Infrastructure.Adapters;

public class ZnubeInventoryAdapterTests
{
    private readonly Mock<IZnubeApiClient> _znube = new();

    [Fact]
    public async Task GetAvailableStockAsync_sums_quantities_for_matching_sku_case_insensitive()
    {
        var sku = "Prod#A#B";
        var response = new OmnichannelResponse
        {
            Data = new OmnichannelData
            {
                Stock =
                [
                    new OmnichannelStockItem
                    {
                        Sku = "prod#a#b",
                        Stock =
                        [
                            new OmnichannelStockDetail
                            {
                                ResourceId = "r1",
                                Quantity = 2,
                                LastUpdateDate = DateTimeOffset.UtcNow
                            },
                            new OmnichannelStockDetail
                            {
                                ResourceId = "r2",
                                Quantity = 3,
                                LastUpdateDate = DateTimeOffset.UtcNow
                            }
                        ]
                    }
                ]
            }
        };

        var normalized = ZnubeLogicExtensions.NormalizeSellerSku(sku);
        _znube.Setup(c => c.GetStockBySkuAsync(normalized, It.IsAny<CancellationToken>()))
            .ReturnsAsync(response);

        var qty = await CreateSut().GetAvailableStockAsync(sku);

        qty.Should().Be(5);
    }

    [Fact]
    public async Task GetAvailableStockAsync_returns_zero_when_client_returns_null()
    {
        _znube.Setup(c => c.GetStockBySkuAsync("missing", It.IsAny<CancellationToken>()))
            .ReturnsAsync((OmnichannelResponse?)null);

        var qty = await CreateSut().GetAvailableStockAsync("missing");

        qty.Should().Be(0);
    }

    private ZnubeInventoryAdapter CreateSut() => new(_znube.Object);
}
