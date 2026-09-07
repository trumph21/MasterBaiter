using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;

namespace MasterBaiter;

/// <summary>
/// Schneidet mit, was beim Bedienen der Gehilfen-Fenster tatsaechlich passiert.
///
/// Der Grund steht im Wiki und in dieser Sitzung zweimal im Protokoll: Beim
/// Blast-Off-Knopf der Cosmic Exploration habe ich Ereignistyp und Parameter
/// geraten, lag zweimal daneben, und der eine Treffer war ein Zufall, der bei
/// einem anderen Planeten sofort aufflog.
///
/// Zehn Minuten Mitschnitt schlagen einen Nachmittag Raten. Deshalb wird hier
/// nichts angeklickt, bevor nicht aufgeschrieben ist, wie ein echter Klick
/// aussieht: welches Fenster, welcher Ereignistyp, welche Parameter.
///
/// Laeuft nur, solange es eingeschaltet ist — die Fenster erzeugen beim
/// Bewegen der Maus genug Ereignisse, um ein Protokoll in Sekunden unlesbar zu
/// machen.
/// </summary>
internal static class RetainerRecorder
{
    /// <summary>Die Fenster der Kette von der Glocke bis zum Gehilfeninventar.</summary>
    private static readonly string[] Watched =
    [
        "RetainerList",          // die Auswahl an der Glocke
        "SelectString",          // "Entrust or withdraw items" und Nachbarn
        "SelectYesno",
        "Talk",                  // der Begruessungskasten
        "InventoryRetainer",     // das Gehilfeninventar selbst
        "InventoryRetainerLarge",
    ];

    private static bool _listening;

    public static bool Listening => _listening;

    public static void Toggle()
    {
        if (_listening)
            Stop();
        else
            Start();
    }

    private static void Start()
    {
        if (_listening)
            return;

        foreach (var name in Watched)
            Services.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, name, OnEvent);

        _listening = true;
        Plugin.Log.Information(
            "[MasterBaiter] Recording retainer windows. Open a bell, pick a retainer, " +
            "choose the item menu — then switch this off and send the log.");
    }

    public static void Stop()
    {
        if (!_listening)
            return;

        foreach (var name in Watched)
            Services.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, name, OnEvent);

        _listening = false;
        Plugin.Log.Information("[MasterBaiter] Stopped recording retainer windows.");
    }

    private static void OnEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs e)
            return;

        // Alles ausser dem Rauschen.
        //
        // Der erste Anlauf liess nur MouseClick und ButtonClick durch und fing
        // damit genau ein Ereignis: den Sprechkasten. Die Auswahl eines
        // Gehilfen und der Menuepunkt gingen leer aus — Listenfenster melden
        // sich offenbar anders. Also wird jetzt alles aufgeschrieben, was
        // nicht blosse Mausbewegung ist, statt vorab zu entscheiden, was
        // wichtig sein duerfte.
        var kind = (FFXIVClientStructs.FFXIV.Component.GUI.AtkEventType)e.AtkEventType;
        var name = kind.ToString();

        if (name.Contains("MouseMove", StringComparison.Ordinal)
            || name.Contains("MouseOver", StringComparison.Ordinal)
            || name.Contains("MouseOut", StringComparison.Ordinal)
            || name.Contains("RollOver", StringComparison.Ordinal)
            || name.Contains("RollOut", StringComparison.Ordinal)
            || name.Contains("Focus", StringComparison.Ordinal)
            || name.Contains("Timer", StringComparison.Ordinal))
            return;

        Plugin.Log.Information(
            $"[MasterBaiter] Recorded: {args.AddonName} {kind} param {e.EventParam}");
    }
}
