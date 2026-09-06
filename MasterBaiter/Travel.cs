using System.Numerics;
using Dalamud.Plugin.Ipc;

namespace MasterBaiter;

/// <summary>
/// Reist zu einem Haendler: Lifestream teleportiert in die Zone, vnavmesh laeuft
/// den Rest. Beide Plugins sind optional; fehlt eines, faellt der Knopf aus.
///
/// Ablauf:
///   Teleport   nur wenn wir in der falschen Zone stehen
///   Ankommen   warten, bis Lifestream fertig und die Zone gewechselt ist
///   Aufsetzen  Zielpunkt auf das Navigationsnetz legen (Hoehe ist oft unbekannt)
///   Laufen     vnavmesh uebernimmt, wir warten auf das Ende
/// </summary>
internal sealed class Travel
{
    private enum Step { Idle, Teleporting, WaitingForZone, Aethernet, WaitingForDistrict, Pathfinding, Walking, Interacting, WaitingForShop, Done, Failed }

    private const float ArrivalRange = 3.5f;   // so nah wollen wir an den NPC
    private const int ZoneTimeoutMs = 45_000;
    private const int WalkTimeoutMs = 300_000;
    private const int InteractTimeoutMs = 40_000;
    private const int ZoneGraceMs = 8_000;
    private const float InteractRange = 6f;

    // vnavmesh
    private readonly ICallGateSubscriber<bool> _navReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _navMoveCloseTo;
    private readonly ICallGateSubscriber<bool> _navPathRunning;
    private readonly ICallGateSubscriber<bool> _navPathfinding;
    private readonly ICallGateSubscriber<object> _navStop;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> _navPointOnFloor;

    // Lifestream
    private readonly ICallGateSubscriber<uint, byte, bool> _lsTeleport;
    private readonly ICallGateSubscriber<bool> _lsBusy;
    private readonly ICallGateSubscriber<object> _lsAbort;
    private readonly ICallGateSubscriber<uint, bool> _lsAethernet;

    private Step _step = Step.Idle;
    private VendorIndex.Vendor _target;
    private long _deadline;
    private long _nextActionAt;
    private int _approachRetries;
    private long _zoneSettledAt;

    /// <summary>Wird ausgeloest, sobald das Haendlerfenster offen ist.</summary>
    public event Action? Arrived;

    public Travel()
    {
        var pi = Plugin.PluginInterface;
        _navReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _navMoveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _navPathRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _navPathfinding = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        _navStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        _navPointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");

        _lsTeleport = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        _lsBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        _lsAbort = pi.GetIpcSubscriber<object>("Lifestream.Abort");
        _lsAethernet = pi.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById");
    }

    public bool Running => _step is not (Step.Idle or Step.Done or Step.Failed);
    public string Status { get; private set; } = string.Empty;
    public VendorIndex.Vendor Target => _target;

    /// <summary>
    /// Welche der vorausgesetzten Plugins antworten nicht? Leer heisst, dass
    /// Reisen moeglich ist.
    /// </summary>
    public List<string> MissingHelpers()
    {
        var missing = new List<string>();

        try { _navReady.InvokeFunc(); }
        catch { missing.Add("vnavmesh"); }

        try { _lsBusy.InvokeFunc(); }
        catch { missing.Add("Lifestream"); }

        return missing;
    }

    public bool Available => MissingHelpers().Count == 0;

    public bool NavmeshReady
    {
        get
        {
            try { return _navReady.InvokeFunc(); }
            catch { return false; }
        }
    }

    /// <summary>
    /// Eigene Erfolgsbedingung fuer dieses Ziel. Null heisst: das uebliche
    /// Ladenfenster.
    /// </summary>
    private Func<bool>? _doneWhen;

    /// <summary>
    /// Wie <see cref="Start(VendorIndex.Vendor)"/>, aber mit eigener
    /// Erfolgsbedingung. Sie wird nach dem Start gesetzt, weil Start sie
    /// zuruecksetzt — jedes gewoehnliche Ziel soll wieder das Ladenfenster
    /// erwarten.
    /// </summary>
    public void Start(VendorIndex.Vendor vendor, Func<bool>? doneWhen)
    {
        Start(vendor);
        _doneWhen = doneWhen;
    }

    public void Start(VendorIndex.Vendor vendor)
    {
        if (!vendor.Navigable)
        {
            Fail("Vendor has no usable coordinates.");
            return;
        }

        _target = vendor;
        _approachRetries = 0;
        _zoneSettledAt = 0;
        _doneWhen = null;

        if (Plugin.ClientState.TerritoryType == vendor.Territory)
        {
            Status = $"Walking to {vendor.Npc}.";
            _step = Step.Pathfinding;
            _deadline = Environment.TickCount64 + WalkTimeoutMs;
            return;
        }

        if (vendor.AetheryteId == 0)
        {
            Fail($"No aetheryte known for {vendor.Zone}.");
            return;
        }

        // Nicht jeder Aetheryt aus den Sheets ist ein Teleportziel.
        if (!Teleportable.Check(vendor.AetheryteId, out var why))
        {
            Fail($"Cannot teleport to {vendor.AetheryteName} for {vendor.Zone}: {why}.");
            return;
        }

        try
        {
            if (!_lsTeleport.InvokeFunc(vendor.AetheryteId, 0))
            {
                Fail("Lifestream refused the teleport.");
                return;
            }
        }
        catch (Exception ex)
        {
            Fail($"Lifestream not available: {ex.Message}");
            return;
        }

        Status = $"Teleporting to {vendor.AetheryteName}.";
        _step = Step.Teleporting;
        _deadline = Environment.TickCount64 + ZoneTimeoutMs;
    }

    public void Stop(string reason)
    {
        try { _navStop.InvokeAction(); } catch { /* Plugin nicht da */ }
        try { if (_lsBusy.InvokeFunc()) _lsAbort.InvokeAction(); } catch { /* dito */ }
        _step = Step.Idle;
        Status = reason;
    }

    public void Tick()
    {
        if (!Running)
            return;

        if (Environment.TickCount64 > _deadline)
        {
            Stop($"Travel timed out in step {_step} (territory {Plugin.ClientState.TerritoryType}, " +
                 $"expected {_target.Territory} for {_target.Zone}).");
            return;
        }

        switch (_step)
        {
            case Step.Teleporting:
                bool busy;
                try { busy = _lsBusy.InvokeFunc(); }
                catch { Fail("Lifestream stopped responding."); return; }
                if (!busy)
                {
                    _step = Step.WaitingForZone;
                    _deadline = Environment.TickCount64 + ZoneTimeoutMs;
                }
                break;

            case Step.WaitingForZone:
                // Schon am Ziel? Dann direkt laufen.
                if (Plugin.ClientState.TerritoryType == _target.Territory)
                {
                    Status = $"Walking to {_target.Npc}.";
                    _step = Step.Pathfinding;
                    _deadline = Environment.TickCount64 + WalkTimeoutMs;
                    return;
                }

                // Manche Ziele — Stadtviertel, The Firmament — haben keinen
                // eigenen Aetheryten. Dorthin geht es nur per Aethernet-Kristall.
                if (_target.AethernetId == 0)
                {
                    // Die erwartete Gebietsnummer stammt teils aus dem Kartennamen
                    // und kann danebenliegen, etwa bei instanziierten Gebieten.
                    // Nach einer Weile wird es trotzdem versucht: Liegt das Ziel
                    // nicht auf diesem Navigationsnetz, scheitert vnavmesh sauber.
                    if (_zoneSettledAt == 0 && Plugin.ClientState.TerritoryType != 0)
                        _zoneSettledAt = Environment.TickCount64 + ZoneGraceMs;

                    if (_zoneSettledAt != 0 && Environment.TickCount64 > _zoneSettledAt)
                    {
                        Plugin.Log.Warning(
                            $"[MasterBaiter] Territory is {Plugin.ClientState.TerritoryType}, expected " +
                            $"{_target.Territory} for {_target.Zone}. Trying to walk anyway.");
                        Status = $"Walking to {_target.Npc}.";
                        _step = Step.Pathfinding;
                        _deadline = Environment.TickCount64 + WalkTimeoutMs;
                    }

                    return;
                }

                if (Plugin.ClientState.TerritoryType == 0)
                    return;

                try
                {
                    if (!_lsAethernet.InvokeFunc(_target.AethernetId))
                    {
                        Fail("Lifestream refused the aethernet hop.");
                        return;
                    }
                }
                catch (Exception ex)
                {
                    Fail($"Aethernet hop not available: {ex.Message}");
                    return;
                }

                Status = "Taking the aethernet.";
                _step = Step.WaitingForDistrict;
                _deadline = Environment.TickCount64 + ZoneTimeoutMs;
                break;

            case Step.WaitingForDistrict:
                if (Plugin.ClientState.TerritoryType != _target.Territory)
                    return;
                Status = $"Walking to {_target.Npc}.";
                _step = Step.Pathfinding;
                _deadline = Environment.TickCount64 + WalkTimeoutMs;
                break;

            case Step.Pathfinding:
                if (!NavmeshReady)
                {
                    Status = "Waiting for the navmesh to finish loading.";
                    return;
                }

                var destination = _target.World;
                if (_target.ApproximateHeight)
                {
                    // Die Datenbank kennt keine Hoehe. vnavmesh sucht den Boden.
                    try
                    {
                        var onFloor = _navPointOnFloor.InvokeFunc(destination, false, 10f);
                        if (onFloor is { } p)
                            destination = p;
                    }
                    catch { /* dann eben ohne */ }
                }

                bool started;
                try { started = _navMoveCloseTo.InvokeFunc(destination, false, ArrivalRange); }
                catch (Exception ex) { Fail($"vnavmesh not available: {ex.Message}"); return; }

                if (!started)
                {
                    Fail("vnavmesh found no path.");
                    return;
                }

                _step = Step.Walking;
                // vnavmesh rechnet den Pfad nebenlaeufig. Ohne diese Schonfrist
                // gilt der Weg als beendet, bevor er ueberhaupt begonnen hat.
                _nextActionAt = Pacing.Next(1100);
                break;

            case Step.Walking:
                if (Environment.TickCount64 < _nextActionAt)
                    return;

                bool running;
                try
                {
                    // Solange noch gerechnet wird, ist der Weg nicht zu Ende.
                    running = _navPathRunning.InvokeFunc();
                    if (!running)
                    {
                        try { running = _navPathfinding.InvokeFunc(); }
                        catch { /* aeltere Fassung ohne diese Abfrage */ }
                    }
                }
                catch { Fail("vnavmesh stopped responding."); return; }

                if (running)
                    return;
                Status = $"Talking to {_target.Npc}.";
                _step = Step.Interacting;
                _nextActionAt = Pacing.Next(600);
                _deadline = Environment.TickCount64 + InteractTimeoutMs;
                break;

            case Step.Interacting:
                if (Environment.TickCount64 < _nextActionAt)
                    return;

                if (TargetWindowOpen)
                {
                    Arrive();
                    return;
                }

                if (!Interact())
                {
                    // Einmal nachlaufen, bevor aufgegeben wird. Der Pfad endet
                    // gelegentlich ein Stueck vor dem Ziel.
                    if (_approachRetries++ < 1)
                    {
                        Plugin.Log.Information($"[MasterBaiter] {_target.Npc} out of reach, walking closer.");
                        Status = $"Getting closer to {_target.Npc}.";
                        _step = Step.Pathfinding;
                        _deadline = Environment.TickCount64 + WalkTimeoutMs;
                        return;
                    }

                    Fail($"{_target.Npc} is not within reach.");
                    return;
                }

                _step = Step.WaitingForShop;
                _nextActionAt = Pacing.Next(2600);
                break;

            case Step.WaitingForShop:
                if (TargetWindowOpen)
                {
                    Arrive();
                    return;
                }

                // Viele Haendler schieben ein Auswahlfenster dazwischen.
                if (TopicSelect.IsOpen)
                {
                    if (Environment.TickCount64 < _nextActionAt)
                        return;

                    var entries = TopicSelect.Entries();
                    if (entries.Count == 0)
                    {
                        Plugin.Log.Warning("[MasterBaiter] Dialog is open but has no readable entries.");
                        _nextActionAt = Pacing.Next(1100);
                        return;
                    }

                    var choice = TopicSelect.BestEntry(entries, out var scores);
                    if (choice < 0)
                    {
                        Fail("No purchase option in the dialog: " + string.Join(" | ", entries));
                        return;
                    }

                    Plugin.Log.Information(
                        $"[MasterBaiter] Dialog at {_target.Npc}: {string.Join(" | ", entries)} — " +
                        $"choosing \"{entries[choice]}\" (scores: {scores}).");
                    TopicSelect.Select(choice);
                    _nextActionAt = Pacing.Next(1100);
                    return;
                }

                // Ein Sprechkasten will nur weitergeklickt werden. Erneutes
                // Ansprechen setzt ihn zurueck, und der NPC bleibt unerreichbar.
                if (TalkDialog.IsOpen)
                {
                    if (Environment.TickCount64 < _nextActionAt)
                        return;
                    Plugin.Log.Debug($"[MasterBaiter] Advancing talk — {TalkDialog.Describe()}");
                    TalkDialog.Advance();
                    _nextActionAt = Pacing.Next(450);
                    _deadline = Environment.TickCount64 + InteractTimeoutMs;
                    return;
                }

                // Solange irgendein Fenster des NPCs offen ist, nicht erneut
                // ansprechen — das wuerde es nur schliessen und neu oeffnen.
                if (YesNoDialog.IsOpen)
                    return;

                // Sonst nochmal ansprechen, bis die Frist ablaeuft.
                if (Environment.TickCount64 >= _nextActionAt)
                {
                    Plugin.Log.Information($"[MasterBaiter] No window opened at {_target.Npc}, talking again.");
                    _step = Step.Interacting;
                }
                break;
        }
    }

    /// <summary>
    /// Ist das Fenster offen, auf das dieses Ziel wartet?
    ///
    /// Nicht jedes Ziel oeffnet einen Laden: Am Marktbrett kommt die Suchmaske,
    /// am Fahrzeug der Cosmic Exploration die Planetenauswahl. Ohne diese
    /// Unterscheidung gilt ein geglueckter Besuch als gescheitert, und das Ziel
    /// wird endlos neu angesprochen — was das Fenster jedes Mal schliesst.
    /// </summary>
    private bool TargetWindowOpen
    {
        get
        {
            if (_doneWhen != null)
                return _doneWhen();
            return MarketBoards.IsBoard(_target.DataId) ? MarketBoard.IsOpen : ShopWindowReader.IsOpen;
        }
    }

    /// <summary>Sucht den Haendler in der Objektliste und spricht ihn an.</summary>
    private unsafe bool Interact()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null)
            return false;

        Dalamud.Game.ClientState.Objects.Types.IGameObject? best = null;
        var bestDistance = float.MaxValue;

        foreach (var obj in Plugin.ObjectTable)
        {
            // Haendler sind EventNpc, Marktbretter dagegen EventObj. Wer nur
            // nach NPCs sucht, steht vor dem Brett und findet nichts.
            if (obj.ObjectKind is not (Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                                       or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj))
                continue;

            // Ueber die Kennung, wenn wir sie haben, sonst ueber den Namen.
            var matches = _target.DataId != 0
                ? obj.BaseId == _target.DataId
                : string.Equals(obj.Name.TextValue, _target.Npc, StringComparison.OrdinalIgnoreCase);
            if (!matches)
                continue;

            var d = Vector3.Distance(me.Position, obj.Position);
            if (d < bestDistance)
            {
                bestDistance = d;
                best = obj;
            }
        }

        if (best == null || bestDistance > InteractRange)
        {
            Plugin.Log.Warning(best == null
                ? $"[MasterBaiter] {_target.Npc} not found in the object table."
                : $"[MasterBaiter] {_target.Npc} is {bestDistance:0.0} away, too far to interact.");
            return false;
        }

        var targets = FFXIVClientStructs.FFXIV.Client.Game.Control.TargetSystem.Instance();
        if (targets == null)
            return false;

        var native = (FFXIVClientStructs.FFXIV.Client.Game.Object.GameObject*)best.Address;
        targets->InteractWithObject(native, false);
        Plugin.Log.Information($"[MasterBaiter] Talked to {_target.Npc} at {bestDistance:0.0} distance.");
        return true;
    }

    private void Arrive()
    {
        _step = Step.Done;
        Status = $"Shop open at {_target.Npc}.";
        Plugin.Log.Information($"[MasterBaiter] Shop opened at {_target.Npc}.");
        Arrived?.Invoke();
    }

    private void Fail(string reason)
    {
        _step = Step.Failed;
        Status = reason;
        Plugin.Log.Warning($"[MasterBaiter] Travel failed: {reason}");
    }
}
