using System.Runtime.InteropServices;
using FFXIVClientStructs.FFXIV.Component.GUI;

namespace MasterBaiter;

/// <summary>
/// Schickt eine Rueckmeldung an ein Addon, wie es ein Klick tun wuerde.
///
/// Der zweite Parameter heisst in den Spielstrukturen <c>close</c> und tut
/// genau das: Er schliesst das Fenster nach dem Rueckruf. Bei einem Dialog, der
/// ohnehin verschwinden soll, ist das richtig. Bei einem Fenster, das offen
/// bleiben muss — einer Auswahl etwa —, zerstoert es dieses, bevor die Auswahl
/// wirken kann. Ich hatte den Parameter fuer ein harmloses "Zustand
/// aktualisieren" gehalten; das Planetenfenster schloss sich deshalb sofort.
/// </summary>
internal static unsafe class AddonCallback
{
    public static void Fire(AtkUnitBase* addon, bool close, params int[] values)
    {
        if (addon == null || values.Length == 0)
            return;

        var block = Marshal.AllocHGlobal(sizeof(AtkValue) * values.Length);
        try
        {
            var atkValues = (AtkValue*)block;
            for (var i = 0; i < values.Length; i++)
            {
                atkValues[i].Type = AtkValueType.Int;
                atkValues[i].Int = values[i];
            }

            addon->FireCallback((uint)values.Length, atkValues, close);
        }
        finally
        {
            Marshal.FreeHGlobal(block);
        }
    }
}
