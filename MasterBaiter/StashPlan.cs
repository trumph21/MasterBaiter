namespace MasterBaiter;

/// <summary>
/// Welche Stapel bewegt werden — und welche nicht.
///
/// Herausgeloest aus <see cref="StashTransfer"/>, weil hier jeder Fehler
/// Gegenstände verschiebt. Alles andere in diesem Plugin kann hoechstens
/// nichts tun; ein Fehlgriff hier hat zwei Verbindungsabbrueche gekostet, und
/// alle drei Ursachen sassen in genau dieser Rechnung: die Uebergroesse-Regel,
/// die Phantomfaecher der Premium-Satteltasche, die fehlende Platzgrenze.
///
/// Deshalb ohne jeden Spielzugriff: Wer die Faecher aufzaehlt, steht daneben.
/// Was hier steht, laesst sich pruefen, ohne dass ein Charakter irgendwo
/// herumsteht.
/// </summary>
internal static class StashPlan
{
    /// <summary>Ein Stapel, der zur Bewegung ansteht.</summary>
    internal readonly record struct Offer(uint ItemId, int Amount);

    /// <summary>
    /// Welche Stapel geholt werden.
    ///
    /// Geholt wird jeder Stapel eines Koeders, dessen Beutel zu kurz ist —
    /// auch einer, der die Zielmenge ueberschreitet. Das war einmal anders:
    /// Solange "wegraeumen" alles ueber der Zielmenge nahm, waere ein zu
    /// grosser Stapel sofort zurueckgewandert. Seit weggeraeumt wird, was kein
    /// Fisch mehr braucht, gibt es dieses Hin und Her nicht.
    ///
    /// Sobald die Luecke gedeckt ist, wird aufgehoert — auch wenn noch weitere
    /// Stapel desselben Koeders daliegen. Der Stapel, der sie deckt, darf sie
    /// ueberschreiten; ein zweiter danach waere nur noch Ballast im Beutel.
    ///
    /// <paramref name="shortBy"/> enthaelt nur Koeder, die ueberhaupt in Frage
    /// kommen — was uebersprungen wird, steht gar nicht erst darin.
    /// </summary>
    public static List<int> Fetch(IReadOnlyList<Offer> offers, IReadOnlyDictionary<uint, int> shortBy,
        int freeSlots)
    {
        var take = new List<int>();
        var left = new Dictionary<uint, int>();

        for (var i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];

            if (!left.TryGetValue(offer.ItemId, out var missing))
            {
                if (!shortBy.TryGetValue(offer.ItemId, out missing))
                    continue;

                left[offer.ItemId] = missing;
            }

            if (missing <= 0)
                continue;

            take.Add(i);
            left[offer.ItemId] = missing - offer.Amount;
        }

        return Cap(take, freeSlots);
    }

    /// <summary>
    /// Welche Stapel weggeraeumt werden.
    ///
    /// Zwei Bedingungen, und beide muessen halten:
    ///
    ///   * Kein Fisch der Liste braucht den Koeder noch. Ein Ueberschuss ueber
    ///     die Zielmenge ist kein Grund — der gehoert dem Spieler.
    ///   * Nach dem Zug liegt immer noch die Zielmenge im Beutel. Bei einem
    ///     einzelnen Stapel von 504 und Ziel 300 bleibt er also liegen: Ihn
    ///     wegzuraeumen hiesse, mit null dazustehen.
    ///
    /// <paramref name="inBag"/> wird mitgefuehrt, weil sich mehrere Stapel
    /// desselben Koeders denselben Bestand teilen: Wer das nicht abzieht, raeumt
    /// den zweiten weg, obwohl der erste die Untergrenze schon erreicht hat.
    /// </summary>
    public static List<int> Stow(IReadOnlyList<Offer> offers, IReadOnlySet<uint> needed,
        IReadOnlyDictionary<uint, int> keep, IReadOnlyDictionary<uint, int> inBag, int freeSlots,
        out int keptWhole)
    {
        var take = new List<int>();
        var left = new Dictionary<uint, int>();
        keptWhole = 0;

        for (var i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            if (needed.Contains(offer.ItemId))
                continue;

            if (!left.TryGetValue(offer.ItemId, out var have))
                left[offer.ItemId] = have = inBag.GetValueOrDefault(offer.ItemId);

            var floor = keep.GetValueOrDefault(offer.ItemId);
            if (have - offer.Amount < floor)
            {
                keptWhole++;
                continue;
            }

            take.Add(i);
            left[offer.ItemId] = have - offer.Amount;
        }

        return Cap(take, freeSlots);
    }

    /// <summary>
    /// Kuerzt auf den Platz, der drueben tatsaechlich frei ist.
    ///
    /// Siebzig Zuege in ein Lager mit einem freien Fach sind neunundsechzig
    /// Ablehnungen — und die haben schon einmal die Verbindung gekostet.
    /// </summary>
    private static List<int> Cap(List<int> take, int freeSlots)
    {
        if (freeSlots < 0)
            freeSlots = 0;

        if (take.Count > freeSlots)
            take.RemoveRange(freeSlots, take.Count - freeSlots);

        return take;
    }
}
