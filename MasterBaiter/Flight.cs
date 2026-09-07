using FFXIVClientStructs.FFXIV.Client.Game.UI;
using Lumina.Excel.Sheets;

namespace MasterBaiter;

/// <summary>
/// Darf hier geflogen werden?
///
/// Die Antwort haengt nicht am Gebiet allein, sondern am Charakter: Fliegen ist
/// je Gebiet freigeschaltet, wenn dessen Aetherstroeme gesammelt sind. Ein
/// Flugweg in einem Gebiet ohne Freischaltung endet damit, dass der Charakter
/// unter einem Ziel steht, das er nicht erreicht.
///
/// Gebiete ohne Aetherstrom-Gruppe — Staedte, Instanzen — sind damit von selbst
/// ausgeschlossen: Sie tragen keine, und ohne Gruppe gibt es nichts zu
/// vervollstaendigen.
/// </summary>
internal static unsafe class Flight
{
    private static uint _territory;
    private static bool _allowed;

    public static bool AllowedHere
    {
        get
        {
            var here = Plugin.ClientState.TerritoryType;
            if (here == _territory)
                return _allowed;

            _territory = here;
            _allowed = Check(here);
            return _allowed;
        }
    }

    private static bool Check(uint territory)
    {
        var row = Plugin.DataManager.GetExcelSheet<TerritoryType>()?.GetRowOrDefault(territory);
        var set = row?.AetherCurrentCompFlgSet.RowId ?? 0;
        if (set == 0)
            return false;

        var state = PlayerState.Instance();
        return state != null && state->IsAetherCurrentZoneComplete(set);
    }
}
