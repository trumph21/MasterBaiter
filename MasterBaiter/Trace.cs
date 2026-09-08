namespace MasterBaiter;

/// <summary>
/// Was das Plugin gerade tut, im Protokoll.
///
/// Jede Stoerung in diesem Plugin sieht von aussen gleich aus: Der Charakter
/// steht still. Ob er auf ein Fenster wartet, auf einen Weg, auf eine Antwort
/// des Marktbretts oder auf gar nichts mehr, steht dem Bildschirm nicht an —
/// und jede einzelne Fehlersuche dieser Sitzung begann damit, aus einer
/// Handvoll Zeilen zu erraten, in welchem Schritt es haengt.
///
/// Deshalb hier: <see cref="Change"/> bekommt jeden Frame den aktuellen Stand
/// und schreibt nur, wenn er sich geaendert hat. Das ergibt genau eine Zeile je
/// Uebergang statt sechzig je Sekunde — eine luekenlose Spur, die trotzdem
/// lesbar bleibt.
///
/// Ausgeschrieben wird auf Information und nicht auf Debug: Dalamud filtert
/// Debug bei der Voreinstellung weg, und ein Protokoll, das man erst
/// freischalten muss, ist keines, wenn der Fehler schon passiert ist.
/// </summary>
internal static class Trace
{
    /// <summary>
    /// Ob die ausfuehrliche Spur mitgeschrieben wird. Aus dem Spielstand
    /// gesetzt, damit man sie abschalten kann, ohne das Plugin zu tauschen.
    /// </summary>
    public static bool Detailed { get; set; } = true;

    private static readonly Dictionary<string, string> Last = new(StringComparer.Ordinal);

    /// <summary>
    /// Meldet den Stand einer Sache und schreibt nur bei Aenderung.
    ///
    /// <paramref name="what"/> ist der Name der Sache — "Travel", "Shop
    /// window" —, <paramref name="state"/> ihr jetziger Zustand. Wer das jeden
    /// Frame aufruft, bekommt die Uebergaenge und sonst nichts.
    /// </summary>
    public static void Change(string what, string state)
    {
        if (!Detailed)
            return;

        if (Last.TryGetValue(what, out var before))
        {
            if (before == state)
                return;

            Last[what] = state;
            Plugin.Log.Information($"[MasterBaiter] {what}: {before} -> {state}");
            return;
        }

        // Der erste Stand ist kein Uebergang. Ihn als "-> Idle" zu melden,
        // fuellt das Protokoll beim Laden mit Zeilen ueber nichts.
        Last[what] = state;
    }

    /// <summary>
    /// Eine Zeile, die nur bei ausfuehrlicher Spur erscheint.
    ///
    /// Fuer alles, was eine Entscheidung begruendet, aber keinen Zustand
    /// beschreibt — welcher Halt gewonnen hat, warum ein Koeder ausfaellt.
    /// Fehlschlaege gehoeren hier nicht her: Die schreiben immer.
    /// </summary>
    public static void Say(string line)
    {
        if (Detailed)
            Plugin.Log.Information($"[MasterBaiter] {line}");
    }

    /// <summary>
    /// Was der Spieler angestossen hat.
    ///
    /// Ohne diese Zeilen laesst sich im Protokoll nicht unterscheiden, ob eine
    /// Handlung ausblieb oder nie angefordert wurde — bei "Sort Bait Storage"
    /// hat genau das eine Fehlersuche gekostet.
    /// </summary>
    public static void Pressed(string button) =>
        Plugin.Log.Information($"[MasterBaiter] Button: {button}.");
}
