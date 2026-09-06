using Xunit;

namespace MasterBaiter.Tests;

/// <summary>
/// Die Auswahl am Marktbrett. Hier kostet ein Fehler Gil, und die Faelle
/// stammen aus echten Laeufen: Die Zahlen sind die aus dem Protokoll.
/// </summary>
public class ListingChoiceTests
{
    private const uint Jig = 12707;

    private static ListingChoice.Offer Offer(int index, uint price, uint quantity, uint item = Jig)
        => new(index, item, price, quantity);

    [Fact]
    public void Nimmt_den_guenstigsten_Stueckpreis()
    {
        var offers = new[] { Offer(0, 5000, 5), Offer(1, 3000, 5), Offer(2, 4000, 5) };

        var best = ListingChoice.Best(offers, needed: 10, maxUnitPrice: 20000, budget: 100000);

        Assert.Equal(1, best!.Value.Index);
        Assert.Equal(3000u, best.Value.UnitPrice);
    }

    [Fact]
    public void Bei_gleichem_Preis_gewinnt_der_groessere_Stapel()
    {
        // Beide gleich teuer je Stueck; der groessere bringt den Bestand in
        // einem Kauf weiter, statt drei Kaeufe zu brauchen.
        var offers = new[] { Offer(0, 3000, 2), Offer(1, 3000, 8), Offer(2, 3000, 5) };

        var best = ListingChoice.Best(offers, needed: 10, maxUnitPrice: 20000, budget: 100000);

        Assert.Equal(8u, best!.Value.Quantity);
    }

    [Fact]
    public void Nimmt_nie_mehr_als_fehlt()
    {
        // Der Fall aus dem Log: Spinner, guenstigstes Angebot 4500, kleinster
        // Stapel 35, gebraucht werden 10. Ein Stapel ist unteilbar, also faellt
        // er weg — auch als einziges Angebot.
        var offers = new[] { Offer(0, 4500, 35) };

        Assert.Null(ListingChoice.Best(offers, needed: 10, maxUnitPrice: 20000, budget: 1000000));
    }

    [Fact]
    public void Ein_zu_grosser_Stapel_gewinnt_auch_nicht_gegen_nichts()
    {
        // Wichtig, weil eine fruehere Fassung den zu grossen Stapel als letzte
        // Moeglichkeit doch genommen haette: 99 Stueck statt 10 kosteten das
        // Zehnfache.
        var offers = new[] { Offer(0, 100, 99), Offer(1, 4000, 3) };

        var best = ListingChoice.Best(offers, needed: 3, maxUnitPrice: 20000, budget: 1000000);

        Assert.Equal(3u, best!.Value.Quantity);
        Assert.Equal(4000u, best.Value.UnitPrice);
    }

    [Fact]
    public void Achtet_auf_den_Preis_je_Stueck()
    {
        // Bladed Steel Jig lag bei 12998; mit einer Grenze von 5000 faellt er raus.
        var offers = new[] { Offer(0, 12998, 1) };

        Assert.Null(ListingChoice.Best(offers, needed: 10, maxUnitPrice: 5000, budget: 100000));
        Assert.NotNull(ListingChoice.Best(offers, needed: 10, maxUnitPrice: 20000, budget: 100000));
    }

    [Fact]
    public void Rechnet_das_Budget_ueber_den_ganzen_Stapel()
    {
        // Der Fehler, der eine Route abbrach: 4500 je Stueck liegt unter der
        // Grenze, aber 20 Stueck sind 90000 und sprengen das Restbudget.
        var offers = new[] { Offer(0, 4500, 20) };

        Assert.Null(ListingChoice.Best(offers, needed: 20, maxUnitPrice: 5000, budget: 50000));
        Assert.NotNull(ListingChoice.Best(offers, needed: 20, maxUnitPrice: 5000, budget: 90000));
    }

    [Fact]
    public void Ueberspringt_leere_und_fremde_Eintraege()
    {
        var offers = new[]
        {
            new ListingChoice.Offer(0, 0, 100, 5),          // kein Item
            new ListingChoice.Offer(1, Jig, 100, 0),        // leerer Stapel
            new ListingChoice.Offer(2, Jig, 3000, 5),
        };

        Assert.Equal(2, ListingChoice.Best(offers, 10, 20000, 100000)!.Value.Index);
    }

    [Fact]
    public void Cheapest_ignoriert_alle_Grenzen()
    {
        // Absicht: Die Meldung soll sagen, was am Markt los ist, auch wenn das
        // Plugin es nicht anfassen durfte.
        var offers = new[] { Offer(0, 12998, 99), Offer(1, 2900, 40) };

        Assert.Equal(2900u, ListingChoice.Cheapest(offers, Jig));
        Assert.Equal(0u, ListingChoice.Cheapest(offers, itemId: 999));
    }

    [Fact]
    public void SmallestStack_beachtet_die_Preisgrenze()
    {
        var offers = new[] { Offer(0, 12000, 1), Offer(1, 3000, 22) };

        Assert.Equal(1u, ListingChoice.SmallestStack(offers, Jig, maxUnitPrice: 20000));
        Assert.Equal(22u, ListingChoice.SmallestStack(offers, Jig, maxUnitPrice: 5000));
        Assert.Equal(0u, ListingChoice.SmallestStack(offers, Jig, maxUnitPrice: 100));
    }
}
