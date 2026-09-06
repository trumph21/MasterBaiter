using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MasterBaiter;

/// <summary>
/// Der Bestaetigungsdialog ("Purchase N x fuer M gil?"), der nach jedem
/// Kaufaufruf erscheint. Ja ist Callback-Wert 0, Nein waere 1.
/// </summary>
internal static unsafe class YesNoDialog
{
    public static AtkUnitBase* Get()
    {
        var ptr = Plugin.GameGui.GetAddonByName("SelectYesno");
        if (ptr.IsNull || !ptr.IsVisible)
            return null;

        var addon = (AtkUnitBase*)ptr.Address;
        return addon->IsReady ? addon : null;
    }

    public static bool IsOpen => Get() != null;

    public static bool Confirm()
    {
        var addon = Get();
        if (addon == null)
            return false;

        AddonCallback.Fire(addon, true, 0);
        return true;
    }
}
