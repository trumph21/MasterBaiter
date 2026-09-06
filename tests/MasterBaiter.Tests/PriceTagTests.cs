using MasterBaiter;
using Xunit;

namespace MasterBaiter.Tests;

public class PriceTagTests
{
    [Fact]
    public void ReadsGil()
    {
        Assert.True(PriceTag.TryParse("77 gil", out var tag));
        Assert.Equal(77, tag.Amount);
        Assert.Equal("gil", tag.Currency);
        Assert.Equal(1, tag.Per);
    }

    [Fact]
    public void ReadsCurrencyWithSpacesAndApostrophe()
    {
        Assert.True(PriceTag.TryParse("5 Purple Crafters' Scrip", out var tag));
        Assert.Equal(5, tag.Amount);
        Assert.Equal("Purple Crafters' Scrip", tag.Currency);
    }

    [Fact]
    public void ReadsBundleSize()
    {
        Assert.True(PriceTag.TryParse("5 Purple Crafters' Scrip / 3", out var tag));
        Assert.Equal(3, tag.Per);
        Assert.Equal("Purple Crafters' Scrip", tag.Currency);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("gil")]
    [InlineData("77")]
    [InlineData("5 Scrip / 0")]
    [InlineData("5 Scrip / x")]
    public void RefusesWhatItCannotRead(string? text)
        => Assert.False(PriceTag.TryParse(text, out _));

    [Fact]
    public void RoundsUpToWholePurchases()
    {
        Assert.True(PriceTag.TryParse("5 Scrip / 3", out var tag));

        // Sieben Stueck sind drei Kaeufe zu je fuenf, nicht zwei.
        Assert.Equal(15, tag.TotalFor(7));
        Assert.Equal(5, tag.TotalFor(1));
        Assert.Equal(5, tag.TotalFor(3));
        Assert.Equal(10, tag.TotalFor(4));
    }

    [Fact]
    public void CostsNothingForNothing()
    {
        Assert.True(PriceTag.TryParse("77 gil", out var tag));
        Assert.Equal(0, tag.TotalFor(0));
        Assert.Equal(0, tag.TotalFor(-5));
    }
}

public class VendorCurrencyTests
{
    private static PriceTag Parse(string text)
    {
        Assert.True(PriceTag.TryParse(text, out var tag));
        return tag;
    }

    [Fact]
    public void GilShopTakesOnlyGil()
    {
        Assert.True(Parse("77 gil").FitsVendor(VendorIndex.VendorKind.GilShop));
        Assert.False(Parse("3 Purple Gatherers' Scrip").FitsVendor(VendorIndex.VendorKind.GilShop));
        Assert.False(Parse("10 Cosmocredit").FitsVendor(VendorIndex.VendorKind.GilShop));
    }

    [Fact]
    public void ScripExchangeTakesOnlyScrips()
    {
        Assert.True(Parse("3 Purple Gatherers' Scrip").FitsVendor(VendorIndex.VendorKind.ScripExchange));
        Assert.False(Parse("77 gil").FitsVendor(VendorIndex.VendorKind.ScripExchange));

        // Der Fall aus der Vorschau: Dragonfly kostet Cosmocredits, liegt aber
        // auch am Scrip-Tausch. Dort ist der Cosmocredit-Preis keine Auskunft.
        Assert.False(Parse("10 Cosmocredit").FitsVendor(VendorIndex.VendorKind.ScripExchange));
    }

    [Fact]
    public void CosmicTakesItsOwnCredits()
    {
        Assert.True(Parse("10 Cosmocredit").FitsVendor(VendorIndex.VendorKind.Cosmic));
        Assert.True(Parse("5 Lunar Credit").FitsVendor(VendorIndex.VendorKind.Cosmic));
        Assert.False(Parse("3 Purple Gatherers' Scrip").FitsVendor(VendorIndex.VendorKind.Cosmic));
    }

    [Fact]
    public void UnknownShopKindMakesNoClaim()
    {
        Assert.True(Parse("77 gil").FitsVendor(VendorIndex.VendorKind.Other));
        Assert.True(Parse("10 Cosmocredit").FitsVendor(VendorIndex.VendorKind.Other));
    }
}
