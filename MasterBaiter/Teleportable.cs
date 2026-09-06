using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace MasterBaiter;

/// <summary>
/// Prueft, ob ein Aetheryt tatsaechlich anfliegbar ist.
///
/// Die Spieldaten allein reichen nicht: Manche Aetheryten stehen zwar im
/// Aetheryte-Sheet und haengen an einem Gebiet, sind aber kein Teleportziel —
/// Cosmic Exploration etwa betritt man ueber den Missionsablauf. Wer sich auf
/// die Sheets verlaesst, schickt den Charakter ins falsche Gebiet.
///
/// Massgeblich ist die Teleportliste des Charakters. Sie enthaelt nur, was
/// freigeschaltet und anfliegbar ist.
///
/// WICHTIG: UpdateAetheryteList ist eine Spielfunktion, die die Liste neu
/// aufbaut — keine harmlose Abfrage. Sie darf nur auf dem Framework-Faden und
/// nur bei eingeloggtem Charakter laufen. Ein frueherer Stand rief sie aus der
/// Fensterzeichnung heraus fuer jeden Haendler jedes Koeders auf, also hunderte
/// Male je Bild; beim Zonenwechsel griff sie ins Leere und riss das Spiel mit.
///
/// Deshalb: Die Liste wird im Takt gelesen und zwischengespeichert, und
/// <see cref="Check"/> sieht ausschliesslich in diesem Speicher nach.
/// </summary>
internal static unsafe class Teleportable
{
    /// <summary>So oft wird die Teleportliste hoechstens neu gelesen.</summary>
    private const int RefreshIntervalMs = 5000;

    private static readonly HashSet<uint> Unlocked = [];
    private static long _nextRefresh;
    private static bool _read;

    /// <summary>Anzahl freigeschalteter Ziele, fuer die Selbstpruefung.</summary>
    public static int Count => Unlocked.Count;

    /// <summary>
    /// Liest die Teleportliste, hoechstens alle paar Sekunden. Gehoert in den
    /// Framework-Takt, nicht in die Zeichenroutine.
    /// </summary>
    public static void Tick()
    {
        var now = Environment.TickCount64;
        if (now < _nextRefresh)
            return;
        _nextRefresh = now + RefreshIntervalMs;

        // Ohne eingeloggten Charakter gibt es keine Liste, und der Versuch,
        // sie aufzubauen, ist genau der Absturz.
        if (!Plugin.ClientState.IsLoggedIn || Plugin.ObjectTable.LocalPlayer == null)
            return;

        var telepo = Telepo.Instance();
        if (telepo == null)
            return;

        var list = telepo->UpdateAetheryteList();
        if (list == null)
            return;

        var entries = list->AsSpan();
        if (entries.Length == 0)
            return;

        Unlocked.Clear();
        foreach (var entry in entries)
            Unlocked.Add(entry.AetheryteId);

        _read = true;
    }

    public static bool Check(uint aetheryteId, out string reason)
    {
        reason = string.Empty;
        if (aetheryteId == 0)
        {
            reason = "no aetheryte known";
            return false;
        }

        // Solange die Liste noch nie gelesen wurde, nicht aussperren. Lifestream
        // lehnt einen unmoeglichen Teleport ohnehin ab; ein falsches "nein"
        // hier wuerde dagegen Haendler verschwinden lassen.
        if (!_read)
        {
            reason = "teleport list not read yet";
            return true;
        }

        if (Unlocked.Contains(aetheryteId))
            return true;

        reason = "not attuned or not a teleport destination";
        return false;
    }
}
