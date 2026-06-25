using FluentAssertions;
using meli_znube_integration.Common;
using meli_znube_integration.Models;

namespace meli_znube_integration.Tests.Common;

public class MeliItemLogisticsExtensionsTests
{
    private static MeliItem ItemWith(string? logisticType, string? status = "active") =>
        new()
        {
            Id = "MLA1",
            Status = status,
            Shipping = logisticType == null ? null : new MeliShipping { LogisticType = logisticType }
        };

    // --- IsFullOrFlexLogistics ---

    [Theory]
    [InlineData("fulfillment")]
    [InlineData("full")]
    [InlineData("self_service")]
    [InlineData("flex")]
    [InlineData("FULFILLMENT")]
    public void IsFullOrFlex_true_for_full_and_flex(string logisticType)
    {
        ItemWith(logisticType).IsFullOrFlexLogistics().Should().BeTrue();
    }

    [Theory]
    [InlineData("drop_off")]
    [InlineData("xd_drop_off")]
    [InlineData("")]
    public void IsFullOrFlex_false_for_other_or_blank(string logisticType)
    {
        ItemWith(logisticType).IsFullOrFlexLogistics().Should().BeFalse();
    }

    [Fact]
    public void IsFullOrFlex_false_when_shipping_or_item_null()
    {
        ItemWith(null).IsFullOrFlexLogistics().Should().BeFalse();
        ((MeliItem?)null).IsFullOrFlexLogistics().Should().BeFalse();
    }

    // --- IsActive ---

    [Fact]
    public void IsActive_true_for_active_status()
    {
        ItemWith("fulfillment", "active").IsActive().Should().BeTrue();
    }

    [Fact]
    public void IsActive_true_when_status_is_null_unknown()
    {
        ItemWith("fulfillment", null).IsActive().Should().BeTrue();
        new MeliItem { Id = "MLA1", Status = "" }.IsActive().Should().BeTrue();
        ((MeliItem?)null).IsActive().Should().BeTrue();
    }

    [Theory]
    [InlineData("paused")]
    [InlineData("closed")]
    [InlineData("under_review")]
    public void IsActive_false_for_non_active_status(string status)
    {
        ItemWith("fulfillment", status).IsActive().Should().BeFalse();
    }

    // --- IsEligibleForUi ---

    [Fact]
    public void IsEligibleForUi_true_when_full_and_active()
    {
        ItemWith("fulfillment", "active").IsEligibleForUi().Should().BeTrue();
    }

    [Fact]
    public void IsEligibleForUi_false_when_inactive_even_if_full()
    {
        ItemWith("fulfillment", "paused").IsEligibleForUi().Should().BeFalse();
    }

    [Fact]
    public void IsEligibleForUi_false_when_non_full_flex_even_if_active()
    {
        ItemWith("drop_off", "active").IsEligibleForUi().Should().BeFalse();
    }

    [Fact]
    public void IsEligibleForUi_false_when_both_inactive_and_wrong_logistics()
    {
        ItemWith("drop_off", "paused").IsEligibleForUi().Should().BeFalse();
    }

    [Fact]
    public void IsEligibleForUi_true_when_full_and_null_status()
    {
        ItemWith("fulfillment", null).IsEligibleForUi().Should().BeTrue();
    }
}
