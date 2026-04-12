using FluentAssertions;
using meli_znube_integration.Core.Application.Orders;
using meli_znube_integration.Core.Domain.Orders;

namespace meli_znube_integration.Tests.Core.Application;

public sealed class NoteFormatterTests
{
    private readonly NoteFormatter _sut = new();

    [Fact]
    public void Empty_allocations_returns_null()
    {
        _sut.Format(Array.Empty<AllocationResult>(), null, false, false, false).Should().BeNull();
    }

    [Fact]
    public void Abbreviates_sin_asignacion_and_sin_stock()
    {
        var allocations = new List<AllocationResult>
        {
            New(AllocationDomainService.SinAsignacion, "S1", 1, "P1"),
            New(AllocationDomainService.SinStock, "S2", 2, "P2"),
        };
        var note = _sut.Format(allocations, null, false, false, false);
        note.Should().NotBeNull();
        note!.Should().StartWith("[A] ");
        note.Should().Contain("SA:");
        note.Should().Contain("SS:");
    }

    [Fact]
    public void Total_length_does_not_exceed_300()
    {
        var allocations = new List<AllocationResult>();
        for (var i = 0; i < 40; i++)
        {
            allocations.Add(New($"Warehouse{i % 3}", "SKU", 1, new string('X', 20) + i));
        }
        var note = _sut.Format(allocations, "VeryLongZoneNameThatMightBeRemovedByPipeline", true, true, true);
        note.Should().NotBeNull();
        (note!.Length <= 300).Should().BeTrue();
    }

    [Fact]
    public void Long_product_labels_are_truncated_to_keep_note_within_300_chars()
    {
        var chunk = new string('x', 50) + " ";
        var longLabel = string.Concat(Enumerable.Repeat(chunk, 8)).TrimEnd();
        var allocations = new List<AllocationResult>
        {
            New("Dep", "sku", 1, longLabel),
        };
        var note = _sut.Format(allocations, null, false, false, false);
        note.Should().NotBeNull();
        (note!.Length <= 300).Should().BeTrue();
        note.Should().StartWith("[A] ");
    }

    [Fact]
    public void Restante_mode_when_more_than_nine_products_in_one_assignment()
    {
        var allocations = new List<AllocationResult>();
        for (var i = 0; i < 10; i++)
        {
            allocations.Add(New("Depósito A", "sku", 1, $"Prod{i}"));
        }
        var note = _sut.Format(allocations, null, false, false, false);
        note.Should().NotBeNull();
        note!.Should().Contain("Restante");
    }

    [Fact]
    public void Appends_pack_combo_toc_and_zone_suffixes()
    {
        var allocations = new List<AllocationResult>
        {
            New("Loc", "s", 1, "A"),
        };
        var note = _sut.Format(allocations, "Zona Norte", true, true, true);
        note.Should().NotBeNull();
        note!.Should().Contain("(Zona Norte)");
        note.Should().Contain("(TOC)");
        note.Should().Contain("(P)");
        note.Should().Contain("(C)");
    }

    private static AllocationResult New(string resource, string sku, int qty, string label) =>
        new()
        {
            ResourceName = resource,
            Sku = sku,
            AllocatedQuantity = qty,
            Label = label,
        };
}
