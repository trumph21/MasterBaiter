using MasterBaiter;
using Xunit;

namespace MasterBaiter.Tests;

public class PurseTests
{
    private static Purse.Wanted Item(uint id, int amount, string price)
    {
        Assert.True(PriceTag.TryParse(price, out var tag));
        return new Purse.Wanted(id, amount, tag);
    }

    [Fact]
    public void EverythingFitsWhenThereIsEnough()
    {
        var list = new[] { Item(1, 10, "5 Scrip"), Item(2, 10, "5 Scrip") };
        Assert.Equal([1u, 2u], Purse.Payable(list, 1000));
    }

    [Fact]
    public void PurchasesShareTheSameBalance()
    {
        // Drei Posten zu je 100 Scrip, aber nur 200 Scrip da: zwei, nicht drei.
        var list = new[] { Item(1, 1, "100 Scrip"), Item(2, 1, "100 Scrip"), Item(3, 1, "100 Scrip") };
        Assert.Equal([1u, 2u], Purse.Payable(list, 200));
    }

    [Fact]
    public void APartlyAffordableItemStillCounts()
    {
        // 300 Stueck zu 3 Scrip waeren 900; mit 30 Scrip sind zehn drin.
        // Hinzufahren lohnt sich dafuer, also zaehlt der Posten.
        var list = new[] { Item(1, 300, "3 Scrip") };
        Assert.Equal([1u], Purse.Payable(list, 30));
    }

    [Fact]
    public void NothingIsPayableWithoutMoney()
    {
        var list = new[] { Item(1, 10, "5 Scrip") };
        Assert.Empty(Purse.Payable(list, 0));
        Assert.Empty(Purse.Payable(list, 4));
    }

    [Fact]
    public void BundlesCostLessThanTheirCount()
    {
        // "5 Scrip / 3" heisst drei Stueck je Kauf. Neun Stueck sind drei
        // Kaeufe zu fuenf, also fuenfzehn — und die sind mit 15 Scrip drin.
        var list = new[] { Item(1, 9, "5 Scrip / 3"), Item(2, 1, "5 Scrip") };
        Assert.Equal([1u], Purse.Payable(list, 15));
    }

    [Fact]
    public void AFreeOrBrokenPriceIsSkippedRatherThanTreatedAsFree()
    {
        var broken = new Purse.Wanted(7, 10, default);
        Assert.Empty(Purse.Payable([broken], 500));
    }
}
