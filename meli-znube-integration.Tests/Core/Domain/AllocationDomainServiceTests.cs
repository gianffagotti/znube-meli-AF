using FluentAssertions;
using meli_znube_integration.Core.Domain.Orders;

namespace meli_znube_integration.Tests.Core.Domain;

public sealed class AllocationDomainServiceTests
{
    private readonly AllocationDomainService _sut = new();

    [Fact]
    public void Deposito_is_consumed_before_other_warehouses()
    {
        var items = new List<ResolvedOrderLine>
        {
            new()
            {
                Sku = "SKU1",
                Quantity = 5,
                Label = "L",
                RuleType = OrderLineRuleType.Full,
            },
        };
        var stock = new Dictionary<string, List<ResourceStock>>(StringComparer.OrdinalIgnoreCase)
        {
            ["SKU1"] =
            [
                new ResourceStock { ResourceName = "Depósito Central", AvailableQuantity = 2 },
                new ResourceStock { ResourceName = "Local A", AvailableQuantity = 10 },
            ],
        };

        var result = _sut.Allocate(items, stock);

        result.Should().HaveCount(2);
        result[0].Should().Match<AllocationResult>(r =>
            r.ResourceName == "Depósito Central" && r.AllocatedQuantity == 2);
        result[1].Should().Match<AllocationResult>(r =>
            r.ResourceName == "Local A" && r.AllocatedQuantity == 3);
    }

    [Fact]
    public void Single_non_deposito_warehouse_drains_until_shortfall_is_sin_stock()
    {
        var items = new List<ResolvedOrderLine>
        {
            new()
            {
                Sku = "S",
                Quantity = 6,
                Label = "L",
                RuleType = OrderLineRuleType.Full,
            },
        };
        var stock = new Dictionary<string, List<ResourceStock>>(StringComparer.OrdinalIgnoreCase)
        {
            ["S"] = [new ResourceStock { ResourceName = "Sucursal Norte", AvailableQuantity = 4 }],
        };

        var result = _sut.Allocate(items, stock);

        result.Should().HaveCount(2);
        result[0].Should().Match<AllocationResult>(r =>
            r.ResourceName == "Sucursal Norte" && r.AllocatedQuantity == 4);
        result[1].Should().Match<AllocationResult>(r =>
            r.ResourceName == AllocationDomainService.SinStock && r.AllocatedQuantity == 2);
    }

    [Fact]
    public void Round_robin_alternates_between_two_warehouses()
    {
        var items = new List<ResolvedOrderLine>
        {
            new()
            {
                Sku = "R",
                Quantity = 4,
                Label = "L",
                RuleType = OrderLineRuleType.Full,
            },
        };
        var stock = new Dictionary<string, List<ResourceStock>>(StringComparer.OrdinalIgnoreCase)
        {
            ["R"] =
            [
                new ResourceStock { ResourceName = "WA", AvailableQuantity = 100 },
                new ResourceStock { ResourceName = "WB", AvailableQuantity = 100 },
            ],
        };

        var result = _sut.Allocate(items, stock);

        var physical = result.Where(r => r.ResourceName is not (AllocationDomainService.SinStock or AllocationDomainService.SinAsignacion)).ToList();
        physical.Should().HaveCount(4);
        physical[0].ResourceName.Should().Be("WA");
        physical[1].ResourceName.Should().Be("WB");
        physical[2].ResourceName.Should().Be("WA");
        physical[3].ResourceName.Should().Be("WB");
        physical.Should().OnlyContain(r => r.AllocatedQuantity == 1);
    }

    [Fact]
    public void Missing_sku_key_yields_sin_asignacion()
    {
        var items = new List<ResolvedOrderLine>
        {
            new()
            {
                Sku = "ABC",
                Quantity = 3,
                Label = "x",
                RuleType = OrderLineRuleType.Full,
            },
        };
        var stock = new Dictionary<string, List<ResourceStock>>(StringComparer.OrdinalIgnoreCase);

        var result = _sut.Allocate(items, stock);

        result.Should().ContainSingle()
            .Which.Should().Match<AllocationResult>(r =>
                r.ResourceName == AllocationDomainService.SinAsignacion
                && r.AllocatedQuantity == 3
                && r.Sku == "ABC");
    }

    [Fact]
    public void Sequential_lines_for_same_sku_share_mutable_stock()
    {
        var items = new List<ResolvedOrderLine>
        {
            new()
            {
                Sku = "X",
                Quantity = 2,
                Label = "a",
                RuleType = OrderLineRuleType.Full,
            },
            new()
            {
                Sku = "X",
                Quantity = 3,
                Label = "b",
                RuleType = OrderLineRuleType.Full,
            },
        };
        var stock = new Dictionary<string, List<ResourceStock>>(StringComparer.OrdinalIgnoreCase)
        {
            ["X"] = [new ResourceStock { ResourceName = "W1", AvailableQuantity = 4 }],
        };

        var result = _sut.Allocate(items, stock);

        var w1Rows = result.Where(r => r.ResourceName == "W1").ToList();
        w1Rows.Sum(r => r.AllocatedQuantity).Should().Be(4);
        result.Should().Contain(r =>
            r.ResourceName == AllocationDomainService.SinStock && r.AllocatedQuantity == 1);
    }

    [Theory]
    [InlineData("Depósito Central", true)]
    [InlineData("Mi Deposito Sur", true)]
    [InlineData("Local", false)]
    public void Deposito_eligible_reflects_substring_after_normalization(string name, bool expected)
    {
        AllocationDomainService.IsDepositoEligible(name).Should().Be(expected);
    }
}
