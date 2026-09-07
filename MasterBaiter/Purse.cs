namespace MasterBaiter;

/// <summary>
/// Was von einer Einkaufsliste mit dem bezahlbar ist, was da ist.
///
/// Absichtlich ohne Spielzugriff: Die Regel entscheidet, wohin die Route
/// faehrt, und ein Fehler darin faellt sonst erst auf, wenn der Charakter vor
/// einem Laden steht, den er sich nicht leisten kann. So laesst sie sich
/// pruefen.
///
/// Der Kern ist, dass sich Kaeufe denselben Vorrat teilen: Drei Koeder zu je
/// hundert Scrips sind bei zweihundert Scrips nicht drei bezahlbare Posten,
/// sondern zwei.
/// </summary>
internal static class Purse
{
    /// <summary>Ein Bestand, wie er in der Leiste steht.</summary>
    internal readonly record struct Holding(string Currency, int Amount, bool Live, DateTime? Seen);

    /// <summary>
    /// Die Bestaende zu den Waehrungen, die in diesen Preisen vorkommen.
    ///
    /// <c>Live</c> sagt, ob die Zahl gerade abgelesen wurde oder aus dem
    /// Gedaechtnis stammt — bei einer ortsgebundenen Waehrung ist das der
    /// Unterschied zwischen Auskunft und Erinnerung.
    /// </summary>
    public static List<Holding> Held(IEnumerable<string> priceTexts)
    {
        var currencies = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var text in priceTexts)
            if (PriceTag.TryParse(text, out var tag))
                currencies.Add(tag.Currency);

        var held = new List<Holding>();
        foreach (var currency in currencies)
        {
            if (Wallet.IdOf(currency) is not { } id)
                continue;

            var live = Restock.CountInInventory(id);
            var amount = live > 0 ? live : Wallet.Remembered(id) ?? 0;
            if (amount <= 0)
                continue;

            held.Add(new Holding(currency, amount, live > 0, Wallet.SeenAt(id)));
        }

        // Gil zuletzt: die haeufigste Waehrung ist selten die knappe.
        return held
            .OrderBy(h => h.Currency.Equals("gil", StringComparison.OrdinalIgnoreCase))
            .ThenBy(h => h.Currency, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Ein Posten: so viele Stueck zu diesem Preis.</summary>
    internal readonly record struct Wanted(uint BaitId, int Amount, PriceTag Price);

    /// <summary>
    /// Welche Posten sich mit <paramref name="balance"/> noch bezahlen lassen.
    ///
    /// Der Reihe nach abgearbeitet — wer zuerst kommt, kauft zuerst. Ein Posten
    /// zaehlt auch dann, wenn nur ein Teil davon bezahlbar ist: Zwanzig von
    /// dreihundert Koedern sind ein Grund hinzufahren, null nicht.
    /// </summary>
    public static List<uint> Payable(IEnumerable<Wanted> items, int balance)
    {
        var affordable = new List<uint>();
        var left = balance;

        foreach (var item in items)
        {
            if (left <= 0)
                break;

            if (item.Price.Amount <= 0 || item.Amount <= 0)
                continue;

            // Wie viele Kaeufe der Vorrat noch traegt.
            var purchases = left / item.Price.Amount;
            if (purchases <= 0)
                continue;

            var needed = (item.Amount + item.Price.Per - 1) / item.Price.Per;
            var covered = Math.Min(purchases, needed);

            left -= covered * item.Price.Amount;
            affordable.Add(item.BaitId);
        }

        return affordable;
    }
}
