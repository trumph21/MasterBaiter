using Dalamud.Game.Addon.Lifecycle;
using Dalamud.Game.Addon.Lifecycle.AddonArgTypes;
using Dalamud.Memory;
using FFXIVClientStructs.FFXIV.Component.GUI;
using Lumina.Excel.Sheets;

namespace MasterBaiter;

/// <summary>
/// Die Planetenauswahl der Cosmic Exploration ("WKSPlanetSelect").
///
/// Aufbau aus einem Fenster-Dump:
///   [0]      Anzahl der Planeten
///   [1..4]   Symbole
///   [11..14] Beschreibungstexte
///   [16..19] Namen — Sinus Ardorum, Phaenna, Oizys, Auxesia
///
/// Der Rueckruf zum Auswaehlen ist die eine Unbekannte: FFXIVClientStructs
/// kennt weder ein AddonWKSPlanetSelect noch einen zugehoerigen Agenten, und
/// aus den Fensterwerten laesst er sich nicht ablesen.
///
/// Deshalb zwei Wege nebeneinander:
///
///   1. Ein Versuch in Stufen. Trifft eine Stufe, faellt das sofort auf, weil
///      sich das Fenster schliesst. Das Risiko ist gering — ein Fehlversuch
///      bewirkt nichts, und schlimmstenfalls landet man auf dem falschen
///      Planeten, was ein Rueckweg behebt.
///   2. Ein Mitschnitt: Klickt der Spieler selbst, wird das Ereignis samt
///      Kennwert protokolliert. Ein einziger Klick verraet damit, was die
///      Stufen nicht getroffen haben.
/// </summary>
internal static unsafe class CosmicPlanet
{
    private const string AddonName = "WKSPlanetSelect";
    private const int CountIndex = 0;
    private const int NameBase = 16;

    private static bool _listening;

    public static AtkUnitBase* Get()
    {
        var ptr = Plugin.GameGui.GetAddonByName(AddonName);
        return ptr.IsNull || !ptr.IsVisible ? null : (AtkUnitBase*)ptr.Address;
    }

    public static bool IsOpen => Get() != null;

    /// <summary>Die Planetennamen in der Reihenfolge des Fensters.</summary>
    public static List<string> Names()
    {
        var result = new List<string>();
        var addon = Get();
        if (addon == null)
            return result;

        var values = addon->AtkValuesSpan;
        if (values.Length <= CountIndex)
            return result;

        var count = (int)values[CountIndex].UInt;
        for (var i = 0; i < count && NameBase + i < values.Length; i++)
        {
            var value = values[NameBase + i];
            var text = value.String.Value == null
                ? string.Empty
                : MemoryHelper.ReadSeStringNullTerminated((nint)value.String.Value).TextValue;
            result.Add(text);
        }

        return result;
    }

    /// <summary>
    /// Die Planeten aus den Spieldaten, in derselben Reihenfolge wie im
    /// Fenster. Damit laesst sich der Zielplanet auch waehlen, wenn gerade
    /// kein Fenster offen ist.
    /// </summary>
    public static List<string> FromSheet()
    {
        var result = new List<string>();
        var sheet = Plugin.DataManager.GetExcelSheet<WKSPlanetSelect>();
        var places = Plugin.DataManager.GetExcelSheet<PlaceName>();
        if (sheet == null || places == null)
            return result;

        foreach (var row in sheet)
        {
            var name = places.GetRowOrDefault(row.Name.RowId)?.Name.ExtractText();
            if (!string.IsNullOrWhiteSpace(name))
                result.Add(name);
        }

        return result;
    }

    /// <summary>Die Zeile eines Planeten, oder -1.</summary>
    public static int IndexOf(string planet)
    {
        var names = Names();
        for (var i = 0; i < names.Count; i++)
            if (string.Equals(names[i], planet, StringComparison.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    /// <summary>
    /// Versatz zwischen Planetenzeile und dem Kennwert des Klicks.
    ///
    /// Gemessen, nicht geraten: Ein Klick auf Phaenna (Zeile 1) meldete
    /// MouseClick param 3, einer auf Auxesia (Zeile 3) param 5.
    /// </summary>
    private const int RowParamOffset = 2;

    /// <summary>Kennwert des "Blast Off"-Knopfes, ebenfalls gemessen.</summary>
    private const int BlastOffParam = 7;

    /// <summary>
    /// Bildet einen Klick im Fenster nach.
    ///
    /// Die Planetenauswahl laeuft NICHT ueber FireCallback — dort bewirkt sie
    /// nichts oder bricht ab. Sie kommt als Ereignis beim Fenster an, und nur
    /// so laesst sie sich nachbilden.
    ///
    /// Das bedeutet, ein Ereignis selbst zusammenzusetzen und dem Spiel zu
    /// uebergeben. Node, Ziel und Zuhoerer werden dafuer gesetzt, die
    /// Ereignisdaten genullt; liest der Empfaenger trotzdem etwas, das hier
    /// fehlt, kann das abstuerzen. Deshalb ist der Weg abschaltbar und nicht
    /// die Voreinstellung.
    /// </summary>
    private static bool Send(AtkEventType type, int param)
    {
        var addon = Get();
        if (addon == null || !addon->IsReady || addon->RootNode == null)
            return false;

        var stage = AtkStage.Instance();
        if (stage == null)
            return false;

        var data = new AtkEventData();
        var evt = new AtkEvent
        {
            Node = addon->RootNode,
            Target = (AtkEventTarget*)&stage->AtkEventTarget,
            Listener = (AtkEventListener*)addon,
            Param = (uint)param,
        };

        addon->ReceiveEvent(type, param, &evt, &data);
        return true;
    }

    /// <summary>Waehlt einen Planeten an.</summary>
    public static bool SelectRow(int index)
    {
        if (index < 0)
            return false;
        return Send(AtkEventType.MouseClick, index + RowParamOffset);
    }

    /// <summary>Loest "Blast Off" aus. Danach kommt der gewoehnliche Ja/Nein-Dialog.</summary>
    public static bool BlastOff() => Send(AtkEventType.ButtonClick, BlastOffParam);

    /// <summary>Die Zahlenwerte des Fensters, kompakt.</summary>
    private static string Numbers()
    {
        var addon = Get();
        if (addon == null)
            return "window gone";

        var values = addon->AtkValuesSpan;
        var parts = new List<string>();
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            if (v.Type is AtkValueType.Int)
                parts.Add($"[{i}]={v.Int}");
            else if (v.Type is AtkValueType.Bool)
                parts.Add($"[{i}]={(v.Byte != 0 ? "true" : "false")}");
        }

        return parts.Count == 0 ? "no numbers" : string.Join(" ", parts);
    }

    /// <summary>Schreibt alle Fensterwerte ins Log, um die Wahl zu pruefen.</summary>
    public static void DumpToLog()
    {
        var addon = Get();
        if (addon == null)
            return;

        var values = addon->AtkValuesSpan;
        Plugin.Log.Information($"[MasterBaiter] Planet window, {values.Length} values:");
        for (var i = 0; i < values.Length; i++)
        {
            var v = values[i];
            var text = v.Type switch
            {
                AtkValueType.Int => v.Int.ToString(),
                AtkValueType.UInt => v.UInt.ToString(),
                AtkValueType.Bool => v.Byte != 0 ? "true" : "false",
                AtkValueType.String or AtkValueType.ManagedString or AtkValueType.ConstString
                    => "\"" + v.String.ToString() + "\"",
                _ => null,
            };
            if (text != null)
                Plugin.Log.Information($"[MasterBaiter]   [{i}] {v.Type} = {text}");
        }
    }

    /// <summary>
    /// Schneidet mit, was das Fenster bei einem echten Klick empfaengt. Kostet
    /// nichts, solange niemand klickt, und ist die verlaesslichste Quelle fuer
    /// den Rueckruf, den keine Struktur dokumentiert.
    /// </summary>
    public static void StartListening()
    {
        if (_listening)
            return;
        _listening = true;
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, AddonName, OnEvent);

        // Zusaetzlich auf allen Fenstern, aber nur solange die Planetenauswahl
        // offen ist. Der Blast-Off-Knopf gehoert nicht zu ihr — er erzeugt dort
        // kein Ereignis —, also muss er woanders liegen. Wo, sagt nur ein
        // Mitschnitt; raten hat hier schon zweimal danebengelegen.
        Services.AddonLifecycle.RegisterListener(AddonEvent.PreReceiveEvent, OnAnyEvent);
    }

    public static void StopListening()
    {
        if (!_listening)
            return;
        _listening = false;
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, AddonName, OnEvent);
        Services.AddonLifecycle.UnregisterListener(AddonEvent.PreReceiveEvent, OnAnyEvent);
    }

    private static void OnAnyEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs e)
            return;

        // Nur waehrend der Planetenauswahl, und nur Klicks. Sonst waere das
        // Protokoll in Sekunden unbrauchbar.
        if (!IsOpen || args.AddonName == AddonName)
            return;
        // Ueber den Namen gefiltert statt ueber die Enum: Sie liegt je nach
        // Dalamud-Fassung woanders, und der Zweck ist ein Mitschnitt, keine
        // dauerhafte Auswertung.
        var kind = e.AtkEventType.ToString();
        if (!kind.Contains("Click", StringComparison.OrdinalIgnoreCase)
            && !kind.Contains("Input", StringComparison.OrdinalIgnoreCase))
            return;

        Plugin.Log.Information(
            $"[MasterBaiter] While the planet window is open: {args.AddonName} " +
            $"{e.AtkEventType} param {e.EventParam}.");
    }

    private static void OnEvent(AddonEvent type, AddonArgs args)
    {
        if (args is not AddonReceiveEventArgs e)
            return;

        // Der Kennwert eines echten Klicks ist die einzige belastbare Quelle
        // dafuer, wie das Fenster angesprochen wird.
        var names = Names();
        var guess = e.EventParam - RowParamOffset;
        var planet = guess >= 0 && guess < names.Count ? names[guess] : "?";

        // Zusaetzlich die Zahlenwerte des Fensters: Wenn die Auswahl irgendwo
        // vermerkt wird, aendert sich einer davon zwischen zwei Klicks. Das ist
        // der einzige Weg herauszufinden, ob ein Planetenklick ueberhaupt beim
        // Fenster ankommt oder nur im Bauteil bleibt.
        Plugin.Log.Information(
            $"[MasterBaiter] Planet window event: {e.AtkEventType} param {e.EventParam} " +
            $"(row {guess} would be {planet}), territory {Plugin.ClientState.TerritoryType}. " +
            $"Values: {Numbers()}");
    }
}
