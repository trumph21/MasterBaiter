namespace MasterBaiter;

/// <summary>
/// Waehlt aus den Angeboten des Marktbretts das passende aus.
///
/// Eigene Klasse ohne Spielzeiger, weil hier ein Fehler Gil kostet und die
/// Regeln nicht offensichtlich sind. Drei davon sind hart und gelten immer:
///
///   1. Nichts ueber dem Preis je Stueck.
///   2. Kein Stapel, der groesser ist als der offene Bedarf. Ein Stapel ist
///      unteilbar; neunundneunzig Stueck zu nehmen, wenn zehn fehlen, ist kein
///      Schnaeppchen, sondern das Zehnfache fuer Ware, die niemand wollte.
///   3. Nichts, dessen Gesamtpreis das Restbudget sprengt.
///
/// Unter dem, was uebrig bleibt, gewinnt der guenstigste Stueckpreis; bei
/// Gleichstand der groessere Stapel, weil er den Bestand in einem Kauf
/// weiterbringt.
/// </summary>
internal static class ListingChoice
{
    /// <summary>Ein Angebot, auf das Noetige verkuerzt.</summary>
    internal readonly record struct Offer(int Index, uint ItemId, uint UnitPrice, uint Quantity);

    /// <summary>
    /// Das beste Angebot, oder null. <paramref name="needed"/> ist der offene
    /// Bedarf, <paramref name="budget"/> das, was noch ausgegeben werden darf.
    /// </summary>
    public static Offer? Best(IEnumerable<Offer> offers, int needed, int maxUnitPrice, long budget)
    {
        Offer? best = null;

        foreach (var offer in offers)
        {
            if (offer.ItemId == 0 || offer.Quantity == 0)
                continue;
            if (offer.UnitPrice > (uint)maxUnitPrice)
                continue;
            if ((long)offer.UnitPrice * offer.Quantity > budget)
                continue;
            if (offer.Quantity > needed)
                continue;

            if (best is not { } current
                || offer.UnitPrice < current.UnitPrice
                || (offer.UnitPrice == current.UnitPrice && offer.Quantity > current.Quantity))
                best = offer;
        }

        return best;
    }

    /// <summary>Der guenstigste Stueckpreis fuer ein Item, ohne jede Ruecksicht auf Grenzen.</summary>
    public static uint Cheapest(IEnumerable<Offer> offers, uint itemId)
    {
        uint cheapest = 0;
        foreach (var offer in offers)
        {
            if (offer.ItemId != itemId || offer.Quantity == 0)
                continue;
            if (cheapest == 0 || offer.UnitPrice < cheapest)
                cheapest = offer.UnitPrice;
        }

        return cheapest;
    }

    /// <summary>Der kleinste Stapel innerhalb der Preisgrenze, 0 wenn keiner passt.</summary>
    public static uint SmallestStack(IEnumerable<Offer> offers, uint itemId, uint maxUnitPrice)
    {
        uint smallest = 0;
        foreach (var offer in offers)
        {
            if (offer.ItemId != itemId || offer.Quantity == 0)
                continue;
            if (offer.UnitPrice > maxUnitPrice)
                continue;
            if (smallest == 0 || offer.Quantity < smallest)
                smallest = offer.Quantity;
        }

        return smallest;
    }
}
