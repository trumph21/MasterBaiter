namespace MasterBaiter;

/// <summary>
/// Zeitabstaende zwischen zwei Handlungen.
///
/// Zwei Gruende fuer Streuung statt fester Werte, und nur der zweite haelt
/// einer Pruefung stand:
///
/// Erstens ist ein fester Wert eine Wette darauf, dass das Spiel immer gleich
/// schnell antwortet. Es tut das nicht — der Kauf am Cosmocredit-Tausch schlug
/// fehl, weil fuenf Millisekunden nach dem Oeffnen noch keine Posten im Fenster
/// standen. Ein etwas laengerer, schwankender Abstand faengt solche Faelle ab,
/// bevor sie zu Fehlern werden.
///
/// Zweitens wirkt gleichfoermiges Klicken im Millisekundentakt maschinell.
/// Dass Streuung daran etwas aendert, sollte man aber nicht glauben: Die
/// Erkennung haengt nicht an Reaktionszeiten. Wer ein Plugin benutzt, traegt
/// das Risiko unabhaengig davon, wie gleichmaessig es klickt.
/// </summary>
internal static class Pacing
{
    private static readonly Random Rng = new();

    /// <summary>
    /// Streckt oder staucht alle Abstaende. 100 ist der Grundwert; wer es eilig
    /// hat, stellt kleiner, wer es ruhiger mag, groesser.
    /// </summary>
    public static int Percent { get; set; } = 100;

    /// <summary>Wie stark ein Abstand schwankt, in Prozent des Grundwerts.</summary>
    private const int SpreadPercent = 45;

    /// <summary>Kuerzester Abstand, egal wie klein der Grundwert ist.</summary>
    private const int FloorMs = 80;

    /// <summary>
    /// Ein Abstand um <paramref name="baseMs"/> herum. Die Streuung ist
    /// einseitig nach oben gewichtet: Zu frueh nachzusehen kostet einen
    /// Fehlschlag, zu spaet nur Zeit.
    /// </summary>
    public static int Delay(int baseMs)
    {
        var scaled = Math.Max(1, baseMs * Math.Clamp(Percent, 10, 400) / 100);
        var spread = Math.Max(1, scaled * SpreadPercent / 100);
        var value = scaled + Rng.Next(-spread / 3, spread + 1);
        return Math.Max(FloorMs, value);
    }

    /// <summary>Der Zeitpunkt, zu dem die naechste Handlung fruehestens ansteht.</summary>
    public static long Next(int baseMs) => Environment.TickCount64 + Delay(baseMs);

    /// <summary>
    /// Eine gelegentliche laengere Pause, wie sie beim Lesen einer Liste
    /// entsteht. Trifft etwa jeden zwoelften Aufruf.
    /// </summary>
    public static long NextWithPause(int baseMs)
        => Rng.Next(12) == 0
            ? Environment.TickCount64 + Delay(baseMs) + Delay(Rng.Next(600, 1600))
            : Next(baseMs);
}
