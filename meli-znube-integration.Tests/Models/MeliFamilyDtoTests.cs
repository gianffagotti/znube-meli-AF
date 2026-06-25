using System.Text.Json;
using FluentAssertions;
using meli_znube_integration.Models.Dtos;

namespace meli_znube_integration.Tests.Models;

public class MeliFamilyDtoTests
{
    private static readonly JsonSerializerOptions Options = new()
    {
        PropertyNameCaseInsensitive = true,
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    [Fact]
    public void Family_with_ids_deserializes_in_order()
    {
        var json = "{\"id\":\"FAM1\",\"user_products_ids\":[\"MLAU111\",\"MLAU222\"]}";

        var dto = JsonSerializer.Deserialize<MeliUserProductsFamilyDto>(json, Options);

        dto.Should().NotBeNull();
        dto!.Id.Should().Be("FAM1");
        dto.UserProductsIds.Should().ContainInOrder("MLAU111", "MLAU222");
    }

    [Fact]
    public void Family_without_user_products_ids_yields_empty_non_null_list()
    {
        var json = "{\"id\":\"FAM1\"}";

        var dto = JsonSerializer.Deserialize<MeliUserProductsFamilyDto>(json, Options);

        dto.Should().NotBeNull();
        dto!.UserProductsIds.Should().NotBeNull();
        dto.UserProductsIds.Should().BeEmpty();
    }

    [Fact]
    public void Family_with_empty_ids_array_yields_empty_list()
    {
        var json = "{\"id\":\"FAM1\",\"user_products_ids\":[]}";

        var dto = JsonSerializer.Deserialize<MeliUserProductsFamilyDto>(json, Options);

        dto!.UserProductsIds.Should().BeEmpty();
    }
}
