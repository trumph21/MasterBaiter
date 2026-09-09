namespace MasterBaiter;

/// <summary>
/// Die drei Koeder, ohne die eine Ozeanfahrt nicht anfaengt.
///
/// Ozeanfischen laeuft nicht ueber die Sammelliste: Was dort beisst, haengt an
/// Route, Tageszeit und Wetter und steht in keinem Auftrag. Fuer das Plugin
/// sieht dieser Vorrat deshalb aus wie Ballast — kein Fisch der Liste braucht
/// ihn, also raeumt "Sort Bait Storage" ihn folgerichtig weg. Zwei Minuten vor
/// der Abfahrt ist das die falsche Entscheidung, und die naechste Faehre geht
/// erst in zwei Stunden.
///
/// Die drei sind der ganze Bedarf: <b>Ragworm</b> fuer die Oberflaeche,
/// <b>Krill</b> fuer die mittlere Tiefe, <b>Plump Worm</b> fuer den Grund. Mit
/// ihnen laesst sich jede Spielart jeder Route abfischen.
///
/// Die Kennungen stammen aus der mitgelieferten Haendlertabelle, nicht aus dem
/// Gedaechtnis: <c>data/vendor-locations.json</c> fuehrt sie unter genau diesen
/// Nummern.
/// </summary>
internal static class OceanFishing
{
    public const uint Ragworm = 29714;
    public const uint Krill = 29715;
    public const uint PlumpWorm = 29716;

    /// <summary>Die drei, in der Reihenfolge ihrer Tiefe.</summary>
    public static readonly uint[] Baits = [Ragworm, Krill, PlumpWorm];

    private static readonly HashSet<uint> Set = [.. Baits];

    public static bool Contains(uint itemId) => Set.Contains(itemId);

    /// <summary>Ihre Namen, wie sie im Spiel heissen.</summary>
    public static string Names => string.Join(", ", Baits.Select(Restock.ItemName));
}
