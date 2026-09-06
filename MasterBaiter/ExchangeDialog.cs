using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MasterBaiter;

/// <summary>
/// Der Mengendialog des Scrip-Tauschs.
///
/// Anders als der Gil-Kraemer fragt der Tausch nicht mit "SelectYesno" nach,
/// sondern oeffnet ein eigenes Fenster mit Mengenfeld und den Schaltflaechen
/// "Exchange" und "Cancel". Wer nur auf SelectYesno wartet, sieht den Kauf
/// scheinbar wirkungslos verpuffen — ausgeloest wurde er, quittiert nie.
///
/// Die Menge steht bereits im Feld, sie kommt aus dem Kaufaufruf. Zu tun
/// bleibt das Bestaetigen.
/// </summary>
internal static unsafe class ExchangeDialog
{
    private const string Name = "ShopExchangeItemDialog";
    private const int QuantityIndex = 18;

    public static AtkUnitBase* Get()
    {
        var ptr = Plugin.GameGui.GetAddonByName(Name);
        if (ptr.IsNull || !ptr.IsVisible)
            return null;

        var addon = (AtkUnitBase*)ptr.Address;
        return addon->IsReady ? addon : null;
    }

    public static bool IsOpen => Get() != null;

    /// <summary>Die vorbelegte Menge, 0 wenn sie sich nicht lesen laesst.</summary>
    public static int Quantity
    {
        get
        {
            var addon = Get();
            if (addon == null)
                return 0;

            var values = addon->AtkValuesSpan;
            return QuantityIndex < values.Length ? values[QuantityIndex].Int : 0;
        }
    }

    /// <summary>
    /// Bestaetigt den Tausch. Die genaue Rueckrufform ist nicht dokumentiert,
    /// deshalb in Stufen: erst mit Menge, dann ohne. Bleibt das Fenster danach
    /// offen, wird es abgebrochen, sonst blockiert es den ganzen Durchlauf.
    /// </summary>
    public static bool Confirm(int attempt)
    {
        var addon = Get();
        if (addon == null)
            return false;

        switch (attempt)
        {
            case 0:
                AddonCallback.Fire(addon, true, 0, Math.Max(Quantity, 1));
                return true;
            case 1:
                AddonCallback.Fire(addon, true, 0);
                return true;
            case 2:
                DumpToLog();
                AddonCallback.Fire(addon, true, 0, Math.Max(Quantity, 1), 0);
                return true;
            default:
                Plugin.Log.Warning("[MasterBaiter] The exchange dialog did not respond; cancelling it.");
                AddonCallback.Fire(addon, true, 1);
                return false;
        }
    }

    public static void DumpToLog()
    {
        var addon = Get();
        if (addon == null)
        {
            Plugin.Log.Information("[MasterBaiter] Exchange dialog: not open.");
            return;
        }

        var values = addon->AtkValuesSpan;
        Plugin.Log.Information($"[MasterBaiter] Exchange dialog, {values.Length} values:");
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            var text = v.Type switch
            {
                AtkValueType.Int => v.Int.ToString(),
                AtkValueType.UInt => v.UInt.ToString(),
                AtkValueType.Bool => v.Byte != 0 ? "true" : "false",
                AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString =>
                    "\"" + v.String.ToString() + "\"",
                _ => null,
            };
            if (text != null)
                Plugin.Log.Information($"[MasterBaiter]   [{i}] {v.Type} = {text}");
        }
    }
}
