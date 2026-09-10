using Lumina.Excel.Sheets;
using FFXIVClientStructs.FFXIV.Client.Game.UI;

namespace MasterBaiter;

/// <summary>
/// Welche grossen Fische noch im Angeltagebuch fehlen.
///
/// Dieselbe Auswahl, die GatherBuddys Fischtabelle unter "Big Fish" und
/// "Uncaught" zeigt — nur ohne die Tabelle. Die Regeln stammen aus dessen
/// Quelltext und nicht aus einer Vermutung:
///
///   * <b>Gross</b> ist <c>Item.Rarity &gt; 1</c> (GatherBuddy: <c>Fish.cs</c>,
///     <c>FishType = ItemData.Rarity > 1 ? Big : Normal</c>).
///   * <b>Gefangen</b> steht in <c>PlayerState.CaughtFishBitArray</c>, ein Bit
///     je Zeile des <c>FishParameter</c>-Blatts.
///   * <b>Ozeanfische zaehlen nicht dazu.</b> GatherBuddys Filter ist eine
///     Kette: Ozean vor Speerfischen vor Gross. Wer das uebersieht, bekommt
///     eine laengere Liste als die Tabelle zeigt und wundert sich.
///   * <b>Ohne Fangplatz faellt er raus</b>, wie in
///     <c>DrawAddAllFilteredToAutoGather</c>: Ein Fisch, den man nirgends
///     angeln kann, gehoert in keine Sammelliste.
///
/// Speerfische stehen in <c>SpearfishingItem</c> und nicht in
/// <c>FishParameter</c> — sie fallen also von selbst weg, so wie in der Kette.
/// </summary>
internal static class BigFish
{
    /// <summary>Ein fehlender grosser Fisch.</summary>
    internal readonly record struct Catch(uint FishId, uint ItemId, string Name);

    /// <summary>
    /// Die Gegenstandskennungen aller Fangplaetze, und getrennt davon die der
    /// Ozeanplaetze.
    ///
    /// Einmal gebaut und behalten: Die Blaetter aendern sich innerhalb einer
    /// Sitzung nicht, das Tagebuch schon.
    /// </summary>
    private static HashSet<uint>? _fishable;
    private static HashSet<uint>? _oceanOnly;

    private static void BuildSpots()
    {
        if (_fishable != null)
            return;

        var fishable = new HashSet<uint>();
        var ocean = new HashSet<uint>();

        // Welche Fangplaetze zur Ozeanfahrt gehoeren, sagt IKDSpot.
        var oceanSpots = new HashSet<uint>();
        foreach (var row in Plugin.DataManager.GetExcelSheet<IKDSpot>())
        {
            oceanSpots.Add(row.SpotMain.RowId);
            oceanSpots.Add(row.SpotSub.RowId);
        }

        foreach (var spot in Plugin.DataManager.GetExcelSheet<FishingSpot>())
        {
            var isOcean = oceanSpots.Contains(spot.RowId);
            foreach (var item in spot.Item)
            {
                if (item.RowId == 0)
                    continue;

                fishable.Add(item.RowId);
                if (isOcean)
                    ocean.Add(item.RowId);
            }
        }

        // Ein Fisch, der auch ausserhalb des Ozeans beisst, ist kein
        // Ozeanfisch — nur wer ausschliesslich dort vorkommt.
        ocean.IntersectWith(fishable);
        _fishable = fishable;
        _oceanOnly = ocean;

        Plugin.Log.Information(
            $"[MasterBaiter] Fishing spots: {fishable.Count} catchable item(s), " +
            $"{ocean.Count} of them at ocean spots, {oceanSpots.Count} ocean spot(s).");
    }

    /// <summary>
    /// Steht dieser Fisch schon im Tagebuch?
    ///
    /// Ein Bit je <c>FishParameter</c>-Zeile. Liegt der Zeiger nicht vor — vor
    /// dem Einloggen —, ist die Antwort unbekannt, und unbekannt heisst hier
    /// nicht "nein": Eine Liste aus allen grossen Fischen des Spiels waere
    /// keine Hilfe.
    /// </summary>
    private static unsafe bool? Caught(uint fishId, int bits)
    {
        var state = PlayerState.Instance();
        if (state == null || !Plugin.ClientState.IsLoggedIn)
            return null;

        // Die Zeilennummer ist der Bitindex, und das Feld ist nur so lang wie
        // das Blatt Zeilen hat — nicht so lang wie seine hoechste Nummer.
        //
        // Das ist keine Vorsichtsmassnahme ins Blaue: FishParameter hat 2509
        // Zeilen, aber Nummern bis 15982. Ein Zugriff auf Bit 15982 laege rund
        // siebzehnhundert Byte hinter dem Feld, mitten in fremden Daten des
        // PlayerState. Er liefe still und laese Unsinn.
        //
        // Ausgeloest wird das heute nicht, weil keine der 983 hohen Zeilen im
        // Tagebuch steht und die Abfrage vorher aussteigt. "Heute nicht" ist
        // aber keine Schranke, sondern ein Zufall des Spielstands.
        if (fishId >= (uint)bits)
            return null;

        return state->CaughtFishBitArray[(int)fishId];
    }

    /// <summary>
    /// Die grossen Fische, die noch fehlen — oder null, wenn das Tagebuch
    /// gerade nicht lesbar ist.
    /// </summary>
    public static List<Catch>? Missing()
    {
        BuildSpots();
        if (_fishable == null || _oceanOnly == null)
            return null;

        var sheet = Plugin.DataManager.GetExcelSheet<FishParameter>();
        if (sheet == null)
            return null;

        var items = Plugin.DataManager.GetExcelSheet<Item>();
        var outOfLog = 0;
        var found = new List<Catch>();
        var seen = new HashSet<uint>();

        foreach (var row in sheet)
        {
            if (!row.IsInLog)
                continue;

            if (items.GetRowOrDefault(row.Item.RowId) is not { RowId: > 0 } fish)
                continue;

            if (fish.Rarity <= 1)
                continue;

            if (!_fishable.Contains(fish.RowId) || _oceanOnly.Contains(fish.RowId))
                continue;

            var caught = Caught(row.RowId, sheet.Count);
            if (caught == null)
            {
                // Ausserhalb des Feldes heisst unbekannt, nicht ungefangen.
                // Ein Fisch, ueber den das Tagebuch nichts sagen kann, gehoert
                // in keine Liste, die "noch zu fangen" heisst.
                if (row.RowId < (uint)sheet.Count)
                    return null;

                outOfLog++;
                continue;
            }

            if (caught.Value || !seen.Add(fish.RowId))
                continue;

            found.Add(new Catch(row.RowId, fish.RowId, fish.Name.ExtractText()));
        }

        found.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.CurrentCulture));

        Plugin.Log.Information(
            $"[MasterBaiter] {found.Count} big fish still missing from the log " +
            $"(of {sheet.Count} fish rows" +
            (outOfLog > 0 ? $", {outOfLog} skipped as beyond the log bit array" : string.Empty) + ").");

        return found;
    }
}
