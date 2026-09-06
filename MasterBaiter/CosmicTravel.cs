using System.Numerics;
using Dalamud.Game.ClientState.Objects.Enums;

namespace MasterBaiter;

/// <summary>
/// Bringt den Charakter auf einen Planeten der Cosmic Exploration.
///
/// Dorthin fuehrt kein Aetheryt. Der Weg geht ueber einen NPC:
///
///   1. den NPC ansprechen
///   2. im Auswahlfenster "Travel to cosmic exploration area." waehlen
///   3. im Planetenfenster den Zielplaneten waehlen
///   4. warten, bis das Gebiet wechselt
///
/// Wo dieser NPC steht, weiss das Plugin nicht von sich aus — er haengt an
/// keinem Laden und steht in keiner der Tabellen, die den Haendlerindex
/// speisen. Statt Koordinaten zu erfinden, merkt sich das Plugin ihn beim
/// ersten Mal: Sobald sein Auswahlfenster auftaucht, wird der naechststehende
/// NPC samt Gebiet und Position gespeichert. Ab dann faehrt es allein hin.
/// </summary>
internal sealed class CosmicTravel(Configuration config, Travel travel, VendorIndex vendors)
{
    private enum Step { Idle, ToGate, Interact, Dialog, Planet, Warping }

    private const int InteractTimeoutMs = 15000;
    private const int WarpTimeoutMs = 30000;
    private const int StepDelayMs = 750;

    /// <summary>Woran die Zeile im Auswahlfenster zu erkennen ist.</summary>
    private const string GateKeyword = "cosmic exploration";

    /// <summary>
    /// Drivingway, der Moon Rover in Mare Lamentorum. Von dort geht es auf die
    /// Planeten der Cosmic Exploration.
    ///
    /// Die Werte stammen aus dem Spiel selbst, nicht aus einer Schaetzung: Sie
    /// wurden beim Ansprechen aus der Objektliste gelesen. Mare Lamentorum ist
    /// ein gewoehnliches Gebiet mit Aetheryt, die Anreise also der uebliche
    /// Teleport.
    ///
    /// Der Teleportpunkt ist fest auf Bestways Burrow gesetzt. Mare Lamentorum
    /// hat zwei Aetheryten, und keiner von beiden hat in den Spieldaten eine
    /// Position — die Suche nach dem naechstgelegenen hat also nichts zu
    /// vergleichen und nimmt den erstbesten, Sinus Lacrimarum. Von dort ist der
    /// Weg deutlich weiter.
    ///
    /// Wer einen anderen Weg benutzt, ueberschreibt das schlicht: Ein einmal
    /// gelernter NPC hat Vorrang vor diesem Eintrag.
    /// </summary>
    private static readonly Configuration.Spot DefaultGate = new()
    {
        Name = "Drivingway",
        Territory = 959,       // Mare Lamentorum
        AetheryteId = 175,     // Bestways Burrow, nicht Sinus Lacrimarum
        DataId = 1052581,
        X = 25.7f,
        Y = -137.4f,
        Z = -411.3f,
    };

    private Step _step = Step.Idle;
    private long _nextAt;
    private long _deadline;
    private uint _fromTerritory;
    private int _planetAttempt;
    private VendorIndex.Vendor? _thenWalkTo;
    private bool _sawPlanetWindow;

    public bool Running => _step != Step.Idle;
    public string Status { get; private set; } = string.Empty;

    /// <summary>
    /// Der NPC, ueber den es losgeht: gelernt, sonst der eingebaute.
    ///
    /// Beschreibt ein gelernter Eintrag denselben NPC wie der eingebaute,
    /// gewinnt trotzdem der eingebaute — er kennt den richtigen Teleportpunkt,
    /// den ein Mitschnitt aus der Objektliste nicht liefern kann.
    /// </summary>
    private Configuration.Spot Gate
    {
        get
        {
            var learned = config.CosmicGate;
            if (learned == null)
                return DefaultGate;
            if (learned.DataId == DefaultGate.DataId && learned.Territory == DefaultGate.Territory)
                return DefaultGate;
            return learned;
        }
    }

    /// <summary>Einen Weg gibt es immer, notfalls den eingebauten.</summary>
    public bool GateKnown => true;

    /// <summary>Wer benutzt wird, fuer die Anzeige.</summary>
    public string GateName => Gate.Name is { Length: > 0 } n ? n : "unknown";

    /// <summary>Wurde der NPC selbst gelernt oder ist es der mitgelieferte?</summary>
    public bool GateIsLearned => !ReferenceEquals(Gate, DefaultGate);

    /// <summary>
    /// Vergisst den gelernten NPC und faellt auf den eingebauten zurueck.
    /// Noetig, weil beim Lernen der falsche erwischt werden kann.
    /// </summary>
    public void ForgetGate()
    {
        config.CosmicGate = null;
        config.Save();
        Plugin.Log.Information($"[MasterBaiter] Forgot the learned NPC, using {DefaultGate.Name} again.");
    }

    /// <summary>
    /// Faehrt zu dem Planeten, auf dem dieser Haendler steht, und laeuft dort
    /// zu ihm. Das Gebiet des Haendlers bestimmt den Planeten, sein Zonenname
    /// steht ohnehin in den Spieldaten.
    /// </summary>
    public void StartTo(VendorIndex.Vendor vendor)
    {
        var planet = PlanetOf(vendor.Zone);
        if (planet == null)
        {
            Status = $"{vendor.Zone} is not a cosmic exploration area.";
            return;
        }

        if (Plugin.ClientState.TerritoryType == vendor.Territory)
        {
            // Schon dort, dann genuegt Laufen.
            travel.Start(vendor);
            return;
        }

        config.CosmicPlanet = planet;
        config.Save();
        _thenWalkTo = vendor;
        Start();
    }

    /// <summary>Der Planet zu einem Zonennamen, oder null.</summary>
    public static string? PlanetOf(string zone)
        => CosmicPlanet.FromSheet()
            .FirstOrDefault(p => string.Equals(p, zone, StringComparison.OrdinalIgnoreCase));

    public void Start()
    {
        var gate = Gate;

        // Das Ausgangsgebiet wird NICHT hier festgehalten. Zwischen Start und
        // Planetenwahl liegt ein Teleport, das Gebiet wechselt also ohnehin —
        // eine Ankunftspruefung darauf meldet Erfolg, bevor irgendetwas
        // geschehen ist. Massgeblich ist das Gebiet, in dem das Fenster steht.
        _fromTerritory = 0;
        _planetAttempt = 0;
        _sawPlanetWindow = false;

        // Der Fahrzeug-NPC oeffnet kein Ladenfenster, sondern die
        // Planetenauswahl — oder erst ein Auswahlmenue davor.
        _step = Step.ToGate;
        travel.Start(Spot(gate), () => CosmicPlanet.IsOpen || TopicSelect.IsOpen || TalkDialog.IsOpen);

        Status = $"Travelling to {config.CosmicPlanet}.";
        _deadline = Environment.TickCount64 + WarpTimeoutMs;
        var spot = Spot(gate);
        Plugin.Log.Information(
            $"[MasterBaiter] Cosmic travel started, target {config.CosmicPlanet}, " +
            $"via {gate.Name} in {spot.Zone} (aetheryte {spot.AetheryteName}).");
    }

    public void Stop(string reason)
    {
        if (_step == Step.Idle)
            return;
        if (travel.Running)
            travel.Stop(reason);
        _step = Step.Idle;
        Status = reason;
        Plugin.Log.Information($"[MasterBaiter] Cosmic travel stopped: {reason}");
    }

    private VendorIndex.Vendor Spot(Configuration.Spot gate)
        => vendors.MakeSpot(gate.Name is { Length: > 0 } n ? n : "Cosmic travel NPC", gate.Territory,
            new Vector3(gate.X, gate.Y, gate.Z), gate.DataId, gate.AetheryteId);

    /// <summary>
    /// Merkt sich den NPC, sobald sein Auswahlfenster sichtbar ist. Laeuft
    /// unabhaengig von einer Reise, damit ein einziges Gespraech genuegt.
    /// </summary>
    public void Learn()
    {
        // Der eingebaute Eintrag genuegt normalerweise. Gelernt wird nur, was
        // er nicht abdeckt: ein Fahrzeug in einem anderen Gebiet.
        if (config.CosmicGate != null)
            return;
        if (Plugin.ClientState.TerritoryType == DefaultGate.Territory)
            return;

        // Zwei Einstiege, und der zweite hat mich beim ersten Anlauf gekostet:
        // Nicht jeder Weg in die Cosmic Exploration laeuft ueber ein
        // Auswahlmenue. Der Moon Rover ist ein Fahrzeug und oeffnet die
        // Planetenauswahl unmittelbar. Wer nur auf das Menue wartet, lernt nie.
        var viaMenu = TopicSelect.IsOpen
                      && TopicSelect.Entries().Any(e => e.Contains(GateKeyword, StringComparison.OrdinalIgnoreCase));
        var viaPlanets = CosmicPlanet.IsOpen;
        if (!viaMenu && !viaPlanets)
            return;

        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null)
            return;

        // Zuerst das anvisierte Ziel. Der naechststehende NPC ist der falsche
        // Massstab: Beim ersten Versuch stand zufaellig Searchingway naeher als
        // der NPC, mit dem tatsaechlich gesprochen wurde.
        var nearest = Plugin.TargetManager.Target;
        var best = nearest == null ? float.MaxValue : Vector3.Distance(me.Position, nearest.Position);

        if (nearest == null)
        {
            foreach (var obj in Plugin.ObjectTable)
            {
                if (obj.ObjectKind is not (ObjectKind.EventNpc or ObjectKind.EventObj or ObjectKind.Mount))
                    continue;
                var d = Vector3.Distance(me.Position, obj.Position);
                if (d >= best)
                    continue;
                best = d;
                nearest = obj;
            }
        }

        if (nearest == null || best > 12f)
            return;

        config.CosmicGate = new Configuration.Spot
        {
            Name = nearest.Name.TextValue,
            Territory = Plugin.ClientState.TerritoryType,
            DataId = nearest.BaseId,
            X = nearest.Position.X,
            Y = nearest.Position.Y,
            Z = nearest.Position.Z,
        };
        config.Save();

        Plugin.Log.Information(
            $"[MasterBaiter] Learned the cosmic travel NPC: {nearest.Name.TextValue} " +
            $"({nearest.ObjectKind}, id {nearest.BaseId}) in territory {config.CosmicGate.Territory} " +
            $"at ({nearest.Position.X:0.0}, {nearest.Position.Y:0.0}, {nearest.Position.Z:0.0}).");
    }

    public void Tick()
    {
        Learn();

        if (_step == Step.Idle)
            return;

        var now = Environment.TickCount64;
        if (now < _nextAt)
            return;

        switch (_step)
        {
            case Step.ToGate:
                if (travel.Running)
                    return;

                // Die Reise oeffnet kein Ladenfenster; sie gilt als geglueckt,
                // wenn das Auswahlfenster oder die Planetenauswahl da ist.
                if (CosmicPlanet.IsOpen)
                {
                    _step = Step.Planet;
                    return;
                }

                if (TopicSelect.IsOpen)
                {
                    _step = Step.Dialog;
                    return;
                }

                _step = Step.Interact;
                _deadline = now + InteractTimeoutMs;
                return;

            case Step.Interact:
                if (TalkDialog.IsOpen)
                {
                    Plugin.Log.Information($"[MasterBaiter] {TalkDialog.Describe()}");
                    TalkDialog.Advance();
                    _nextAt = Pacing.Next(550);
                    _deadline = now + InteractTimeoutMs;
                    return;
                }

                if (CosmicPlanet.IsOpen)
                {
                    _step = Step.Planet;
                    return;
                }

                if (TopicSelect.IsOpen)
                {
                    _step = Step.Dialog;
                    return;
                }

                if (now > _deadline)
                {
                    Stop("The travel NPC did not respond.");
                    return;
                }

                _nextAt = Pacing.Next(StepDelayMs);
                return;

            case Step.Dialog:
            {
                if (TalkDialog.IsOpen)
                {
                    TalkDialog.Advance();
                    _nextAt = Pacing.Next(550);
                    return;
                }

                if (CosmicPlanet.IsOpen)
                {
                    _step = Step.Planet;
                    return;
                }

                if (!TopicSelect.IsOpen)
                {
                    _step = Step.Interact;
                    return;
                }

                var entries = TopicSelect.Entries();
                var choice = entries.FindIndex(e => e.Contains(GateKeyword, StringComparison.OrdinalIgnoreCase));
                if (choice < 0)
                {
                    Stop("No travel option in the dialog: " + string.Join(" | ", entries));
                    return;
                }

                Plugin.Log.Information($"[MasterBaiter] Choosing \"{entries[choice]}\".");
                TopicSelect.Select(choice);
                _nextAt = Pacing.Next(StepDelayMs);
                _step = Step.Planet;
                _deadline = now + InteractTimeoutMs;
                return;
            }

            case Step.Planet:
            {
                if (TalkDialog.IsOpen)
                {
                    TalkDialog.Advance();
                    _nextAt = Pacing.Next(550);
                    _deadline = now + WarpTimeoutMs;
                    return;
                }

                // "Blast Off" ist ein gewoehnlicher Ja/Nein-Dialog. Das ist
                // zugleich das Erfolgszeichen: Er erscheint nur, wenn die
                // Planetenwahl gesessen hat.
                if (YesNoDialog.IsOpen)
                {
                    Plugin.Log.Information("[MasterBaiter] Confirming the ride.");
                    YesNoDialog.Confirm();
                    _step = Step.Warping;
                    _nextAt = Pacing.Next(1000);
                    _deadline = now + WarpTimeoutMs;
                    return;
                }

                if (!CosmicPlanet.IsOpen)
                {
                    if (_fromTerritory != 0 && Plugin.ClientState.TerritoryType != _fromTerritory)
                    {
                        Arrived();
                        return;
                    }

                    // Fenster weg, kein Dialog: Der letzte Wert hat abgebrochen.
                    // Also erneut ansprechen und die naechste Stufe versuchen.
                    if (_planetAttempt > 0)
                    {
                        Plugin.Log.Information(
                            "[MasterBaiter] That value closed the window instead of selecting. Trying again.");
                        _step = Step.Interact;
                        _sawPlanetWindow = false;
                        _nextAt = Pacing.Next(1000);
                        _deadline = now + InteractTimeoutMs;
                        return;
                    }

                    if (now > _deadline)
                    {
                        Stop("The planet select window did not open.");
                        return;
                    }

                    _nextAt = Pacing.Next(StepDelayMs);
                    return;
                }

                if (!_sawPlanetWindow)
                {
                    _sawPlanetWindow = true;
                    _fromTerritory = Plugin.ClientState.TerritoryType;
                    Plugin.Log.Information(
                        $"[MasterBaiter] Planet window open in territory {_fromTerritory}: " +
                        $"{string.Join(", ", CosmicPlanet.Names())}");
                }

                // Ohne die Option nichts anfassen: Das Fenster bleibt offen,
                // der Spieler waehlt und bestaetigt selbst.
                if (!config.AutoSelectPlanet)
                {
                    Status = $"Pick {config.CosmicPlanet} yourself, then Blast Off.";
                    _nextAt = Pacing.Next(StepDelayMs);
                    _deadline = now + WarpTimeoutMs;
                    return;
                }

                var index = CosmicPlanet.IndexOf(config.CosmicPlanet);
                if (index < 0)
                {
                    Stop($"{config.CosmicPlanet} is not on offer: {string.Join(", ", CosmicPlanet.Names())}");
                    return;
                }

                // Erst den Planeten anklicken, beim naechsten Durchgang Blast
                // Off. Getrennt, weil das Fenster zwischen beiden Schritten
                // seine Auswahl uebernehmen muss.
                if (_planetAttempt == 0)
                {
                    CosmicPlanet.SelectRow(index);
                    Plugin.Log.Information($"[MasterBaiter] Clicking {config.CosmicPlanet} (row {index}).");
                    _planetAttempt = 1;
                    _nextAt = Pacing.Next(850);
                    return;
                }

                if (_planetAttempt == 1)
                {
                    CosmicPlanet.BlastOff();
                    Plugin.Log.Information("[MasterBaiter] Blast off.");
                    _planetAttempt = 2;
                    _nextAt = Pacing.Next(1500);
                    return;
                }

                if (now > _deadline)
                {
                    Stop("Nothing happened after blast off. " +
                         "Switch off \"Pick the planet automatically\" and click it yourself.");
                    return;
                }

                _nextAt = Pacing.Next(StepDelayMs);
                return;
            }

            case Step.Warping:
                if (_fromTerritory != 0 && Plugin.ClientState.TerritoryType != _fromTerritory)
                {
                    Arrived();
                    return;
                }

                if (now > _deadline)
                {
                    Stop("The zone did not change.");
                    return;
                }

                _nextAt = Pacing.Next(StepDelayMs);
                return;
        }
    }

    /// <summary>Wartet der Durchlauf gerade auf einen Klick des Spielers?</summary>
    public bool WaitingForClick => _step == Step.Planet && !config.AutoSelectPlanet;

    private void Arrived()
    {
        _step = Step.Idle;
        Status = $"Arrived on {config.CosmicPlanet}.";
        Plugin.Log.Information($"[MasterBaiter] {Status}");

        if (_thenWalkTo is { } vendor)
        {
            _thenWalkTo = null;
            Plugin.Log.Information($"[MasterBaiter] Walking to {vendor.Npc}.");
            travel.Start(vendor);
            return;
        }

        Arrival?.Invoke();
    }

    /// <summary>Wird ausgeloest, sobald das Gebiet gewechselt hat.</summary>
    public event Action? Arrival;
}
