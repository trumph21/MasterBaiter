using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MasterBaiter;

/// <summary>
/// Das Fenster, das eine Rufglocke oeffnet: die Liste der eigenen Gehilfen.
///
/// Eigene Klasse, weil <see cref="Travel"/> sonst nicht weiss, dass es
/// angekommen ist. Ohne sie wartet die Reise auf ein Ladenfenster, das an einer
/// Glocke nie erscheint — und spricht sie dabei immer wieder an, bis die
/// Ausweichpunkte durch sind. Genau das stand im Protokoll: dreimal ansprechen,
/// Standpunkt wechseln, wieder dreimal, obwohl die Liste offen war.
///
/// Wie eine Zeile angewaehlt wird, ist nicht geraten, sondern mitgeschnitten:
///
///     RetainerList           ListItemClick        param 1
///     Talk                   MouseClick           param 0
///     InventoryRetainerLarge ChildAddonAttached   …
///
/// Bemerkenswert daran ist, was fehlt: kein <c>SelectString</c>. Nach dem
/// Sprechkasten geht das Inventar unmittelbar auf, der Menuepunkt "Entrust or
/// withdraw items" entfaellt.
/// </summary>
internal static unsafe class RetainerList
{
    private const string Name = "RetainerList";


    public static AtkUnitBase* Get()
    {
        var ptr = Plugin.GameGui.GetAddonByName(Name);
        if (ptr.IsNull || !ptr.IsVisible)
            return null;

        var addon = (AtkUnitBase*)ptr.Address;
        return addon->IsReady ? addon : null;
    }

    public static bool IsOpen => Get() != null;

    /// <summary>Der Befehl "oeffne den gewaehlten Gehilfen".</summary>
    private const int OpenCommand = 2;

    /// <summary>
    /// Waehlt die Zeile <paramref name="index"/> an, von null gezaehlt.
    ///
    /// Die Zahl im Rueckruf ist ein Befehl, kein Zeilenindex. Das stand am Ende
    /// einer Reihe von Fehlschluessen, und jeder einzelne sah unterwegs
    /// vernuenftig aus:
    ///
    ///   * Mitschnitt: <c>ListItemClick param 1</c> beim ersten Gehilfen.
    ///     Daraus geschlossen: param = Zeile + 1.
    ///   * <c>param 0</c> oeffnete "Sort Retainers" — passte zur Regel.
    ///   * <c>param 2</c> oeffnete den zweiten Gehilfen aber nicht.
    ///   * Zweiter Mitschnitt, zweiter Gehilfe: wieder <c>param 1</c>. Der
    ///     Parameter war also nie die Zeile.
    ///   * Ueber den Rueckruf durchprobiert: 0 nichts, 1 sortieren, 2 der erste
    ///     Gehilfe, 3 und 4 nichts. Waere 2 die erste Zeile, muesste 3 die
    ///     zweite sein.
    ///
    /// Also: 1 sortiert, 2 oeffnet den <b>ausgewaehlten</b> Gehilfen. Die Zeile
    /// muss daneben stehen, deshalb zwei Werte statt einem.
    ///
    /// Fuenf Messpunkte, drei widerlegte Annahmen. Geraten haette ich hier
    /// jedes Mal falsch.
    /// </summary>
    public static bool Select(int index)
    {
        var addon = Get();
        if (addon == null || index < 0)
            return false;

        var values = stackalloc AtkValue[2];
        values[0].SetInt(OpenCommand);
        values[1].SetInt(index);

        // Der dritte Parameter heisst close, nicht updateState — hier soll das
        // Fenster offen bleiben.
        addon->FireCallback(2, values, false);

        Plugin.Log.Information($"[MasterBaiter] Retainer list: open row {index}.");
        return true;
    }
}
