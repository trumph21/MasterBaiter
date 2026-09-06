namespace MasterBaiter;

/// <summary>
/// Zerlegt die Preiszeichenkette aus dem Haendlerverzeichnis wieder in Zahlen.
///
/// Dort steht der Preis als fertiger Text — "77 gil", "5 Purple Crafters' Scrip",
/// oder "5 Purple Crafters' Scrip / 3", wenn ein Kauf drei Stueck liefert. Fuer
/// eine Summe braucht die Vorschau die Bestandteile zurueck.
///
/// Absichtlich eine eigene, spielfreie Klasse: Sie ist die einzige Stelle, an
/// der aus Text wieder eine Zahl wird, und ein Fehler darin faellt sonst erst
/// als falsche Summe auf.
/// </summary>
internal readonly record struct PriceTag(int Amount, string Currency, int Per)
{
    /// <summary>Was <paramref name="count"/> Stueck kosten.</summary>
    public long TotalFor(int count)
    {
        if (count <= 0 || Per <= 0)
            return 0;

        // Aufgerundet: Ein halber Kauf ist keiner.
        var purchases = (count + Per - 1) / Per;
        return (long)purchases * Amount;
    }

    /// <summary>
    /// Nimmt dieser Laden diese Waehrung ueberhaupt an?
    ///
    /// Ein Koeder hat oft mehrere Preise — Dragonfly kostet Cosmocredits bei
    /// der Cosmic Exploration und liegt zugleich am Scrip-Tausch. Welcher der
    /// beiden gilt, haengt am Halt. Den falschen anzuzeigen ist schlimmer als
    /// keinen: Eine Summe, die man nicht bezahlt, sieht aus wie eine, die man
    /// bezahlt.
    /// </summary>
    public bool FitsVendor(VendorIndex.VendorKind kind) => kind switch
    {
        VendorIndex.VendorKind.GilShop =>
            Currency.Equals("gil", StringComparison.OrdinalIgnoreCase),

        VendorIndex.VendorKind.ScripExchange =>
            Currency.Contains("scrip", StringComparison.OrdinalIgnoreCase),

        VendorIndex.VendorKind.Cosmic =>
            Currency.Contains("cosmocredit", StringComparison.OrdinalIgnoreCase)
            || Currency.Contains("lunar credit", StringComparison.OrdinalIgnoreCase),

        // Sonderlaeden nehmen alles Moegliche. Ohne bekannte Regel keine
        // Behauptung aufstellen.
        _ => true,
    };

    public static bool TryParse(string? text, out PriceTag tag)
    {
        tag = default;
        if (string.IsNullOrWhiteSpace(text))
            return false;

        var rest = text.Trim();

        var digits = 0;
        while (digits < rest.Length && char.IsAsciiDigit(rest[digits]))
            digits++;

        if (digits == 0 || !int.TryParse(rest[..digits], out var amount))
            return false;

        rest = rest[digits..].Trim();
        if (rest.Length == 0)
            return false;

        var per = 1;
        var slash = rest.LastIndexOf('/');
        if (slash >= 0)
        {
            var tail = rest[(slash + 1)..].Trim();
            if (!int.TryParse(tail, out per) || per <= 0)
                return false;

            rest = rest[..slash].Trim();
            if (rest.Length == 0)
                return false;
        }

        tag = new PriceTag(amount, rest, per);
        return true;
    }
}
