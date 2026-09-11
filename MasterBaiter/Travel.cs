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
    private enum Step { Idle, WaitingToTeleport, Teleporting, WaitingForZone, Aethernet, WaitingForDistrict, Pathfinding, Walking, Landing, Interacting, WaitingForShop, Done, Failed }

    private const float ArrivalRange = 3.5f;   // so nah wollen wir an den NPC

    /// <summary>
    /// Wie oft ein anderer Standpunkt vor demselben NPC versucht wird.
    ///
    /// Der hinterlegte Wegpunkt ist ein Punkt, kein Bereich, und manche taugen
    /// nicht: In Tuliyollal endet der Weg an einer Stelle, von der aus sich der
    /// Haendler nicht ansprechen laesst. Denselben Punkt erneut anzulaufen
    /// aendert daran nichts — es muss ein anderer sein.
    /// </summary>
    private const int ApproachSpots = 6;

    /// <summary>Wie weit die Ausweichpunkte um den NPC herum liegen.</summary>
    private const float ApproachOffset = 2.5f;

    /// <summary>
    /// Ab welcher Naehe zwei Ausweichpunkte als derselbe gelten.
    ///
    /// Etwas unter der Ankunftsreichweite: Wer dort schon steht, hat den Punkt
    /// bereits abgelaufen, und ihn erneut anzusteuern kostet einen Versuch
    /// ohne jede Aenderung.
    /// </summary>
    private const float SameSpotDistance = 2f;

    /// <summary>
    /// Wie oft das Absitzen versucht wird, bevor es trotzdem weitergeht.
    ///
    /// Der Befehl ist gesperrt, solange der Charakter noch in einer Bewegung
    /// steckt; ein zweiter Versuch eine Sekunde spaeter greift dann. Klappt es
    /// gar nicht, ist Ansprechen immer noch besser als Aufgeben — zu Pferd auf
    /// dem Boden geht es ja.
    /// </summary>
    private const int MaxLandAttempts = 4;

    private const int LandTimeoutMs = 15_000;

    /// <summary>
    /// So lange darf der Charakter beim Laufen auf der Stelle stehen, bevor er
    /// als festhaengend gilt.
    ///
    /// vnavmesh rechnet den Weg einmal und laeuft ihn dann ab; ob dabei etwas
    /// im Weg steht, merkt niemand. In Tuliyollal hat der Charakter so
    /// sechsundachtzig Sekunden lang gegen eine Kiste gedrueckt — der Pfad galt
    /// die ganze Zeit als "laeuft noch".
    ///
    /// Von fuenf auf drei Sekunden: Waehrend ein Weg laeuft, ist Stillstand
    /// ohnehin schon der Ausnahmefall, und die fuenf waren an Goplus Stand
    /// jedes Mal voll auszusitzen, bevor der naechste Platz drankam. Kuerzer
    /// wird riskant — ein Flugstart hebt den Charakter fast auf der Stelle ab.
    /// </summary>
    private const int StuckMs = 3000;

    /// <summary>Weniger Bewegung als das gilt als keine.</summary>
    private const float StuckDistance = 0.7f;
    private const int ZoneTimeoutMs = 45_000;

    /// <summary>Abstand zwischen zwei Teleportversuchen.</summary>
    private const int TeleportRetryMs = 2500;

    /// <summary>So lange wird es versucht, bevor der Halt scheitert.</summary>
    private const int TeleportRetryWindowMs = 25_000;
    private const int WalkTimeoutMs = 300_000;
    private const int InteractTimeoutMs = 40_000;
    private const int ZoneGraceMs = 8_000;
    private const float InteractRange = 6f;

    /// <summary>
    /// Weiter als das vom angeforderten Ziel entfernt heisst: Der Weg ist
    /// abgebrochen worden, nicht zu Ende gelaufen.
    ///
    /// Grosszuegig gewaehlt, weil ein Flug in der Luft endet und die Hoehe in
    /// die Entfernung eingeht. Es geht nicht um Genauigkeit, sondern um den
    /// Unterschied zwischen "da" und "ganz woanders".
    /// </summary>
    private const float PathLostDistance = 10f;

    /// <summary>Wie oft derselbe Weg neu gerechnet wird, bevor der Platz schuld ist.</summary>
    private const int MaxRepaths = 3;

    // vnavmesh
    private readonly ICallGateSubscriber<bool> _navReady;
    private readonly ICallGateSubscriber<Vector3, bool, float, bool> _navMoveCloseTo;
    private readonly ICallGateSubscriber<bool> _navPathRunning;
    private readonly ICallGateSubscriber<bool> _navPathfinding;
    private readonly ICallGateSubscriber<object> _navStop;
    private readonly ICallGateSubscriber<Vector3, bool, float, Vector3?> _navPointOnFloor;
    private readonly ICallGateSubscriber<Vector3, float, float, Vector3?> _navNearestReachable;
    private readonly ICallGateSubscriber<Vector3, float, bool, bool> _navOnMesh;
    private readonly ICallGateSubscriber<int> _navWaypoints;

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

    /// <summary>
    /// Die Punkte, die bei diesem Ziel schon angelaufen wurden. Damit ein
    /// zweiter Versuch auch wirklich woanders hinfuehrt.
    /// </summary>
    private readonly List<Vector3> _tried = [];
    private int _talkAttempts;
    private Vector3 _lastPosition;
    private long _movedAt;
    private long _walkStartedAt;
    private long _zoneSettledAt;
    private int _landAttempts;

    /// <summary>Wohin zuletzt geschickt wurde, und wie oft der Weg neu gerechnet wurde.</summary>
    private Vector3 _destination;
    private int _repaths;
    private bool _loggedPath;

    /// <summary>Wird ausgeloest, sobald das Haendlerfenster offen ist.</summary>
    public event Action? Arrived;

    private readonly Configuration _config;

    public Travel(Configuration config)
    {
        _config = config;

        var pi = Plugin.PluginInterface;
        _navReady = pi.GetIpcSubscriber<bool>("vnavmesh.Nav.IsReady");
        _navMoveCloseTo = pi.GetIpcSubscriber<Vector3, bool, float, bool>("vnavmesh.SimpleMove.PathfindAndMoveCloseTo");
        _navPathRunning = pi.GetIpcSubscriber<bool>("vnavmesh.Path.IsRunning");
        _navPathfinding = pi.GetIpcSubscriber<bool>("vnavmesh.SimpleMove.PathfindInProgress");
        _navStop = pi.GetIpcSubscriber<object>("vnavmesh.Path.Stop");
        _navPointOnFloor = pi.GetIpcSubscriber<Vector3, bool, float, Vector3?>("vnavmesh.Query.Mesh.PointOnFloor");
        _navNearestReachable = pi.GetIpcSubscriber<Vector3, float, float, Vector3?>("vnavmesh.Query.Mesh.NearestPointReachable");
        _navOnMesh = pi.GetIpcSubscriber<Vector3, float, bool, bool>("vnavmesh.Query.Mesh.IsPointOnMesh");
        _navWaypoints = pi.GetIpcSubscriber<int>("vnavmesh.Path.NumWaypoints");

        _lsTeleport = pi.GetIpcSubscriber<uint, byte, bool>("Lifestream.Teleport");
        _lsBusy = pi.GetIpcSubscriber<bool>("Lifestream.IsBusy");
        _lsAbort = pi.GetIpcSubscriber<object>("Lifestream.Abort");
        _lsAethernet = pi.GetIpcSubscriber<uint, bool>("Lifestream.AethernetTeleportById");
    }

    public bool Running => _step is not (Step.Idle or Step.Done or Step.Failed);

    /// <summary>Die Objekt-Id des Ziels, damit der Aufrufer weiss, wo er steht.</summary>
    public uint TargetDataId => _target.DataId;

    /// <summary>
    /// Wie weit es noch bis zum Ziel ist, solange eine Reise laeuft und der
    /// Charakter im richtigen Gebiet steht. Sonst null — ueber Gebietsgrenzen
    /// hinweg ist eine Entfernung keine Aussage.
    /// </summary>
    public float? DistanceToTarget
    {
        get
        {
            if (!Running || Plugin.ClientState.TerritoryType != _target.Territory)
                return null;

            var me = Plugin.ObjectTable.LocalPlayer;
            return me == null ? null : Vector3.Distance(me.Position, _target.World);
        }
    }
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

    /// <summary>Bisherige Versuche, den Aethernet-Sprung auszuloesen.</summary>
    private int _aethernetTries;

    /// <summary>Wann der naechste Versuch fruehestens ansteht.</summary>
    private long _aethernetNextTry;

    /// <summary>Abstand zwischen zwei Versuchen.</summary>
    private const int AethernetRetryMs = 2500;

    /// <summary>So oft wird es versucht, bevor der Halt scheitert.</summary>
    private const int AethernetMaxTries = 8;

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
        // Der Teleportpunkt wird erst geprueft, wenn ein Teleport ansteht —
        // innerhalb des Gebiets und auf den Planeten braucht es keinen.
        if (!vendor.HasPosition)
        {
            Fail("Vendor has no usable coordinates.");
            return;
        }

        _target = vendor;
        _approachRetries = 0;
        _repaths = 0;
        _tried.Clear();
        _talkAttempts = 0;
        _zoneSettledAt = 0;
        _doneWhen = null;
        _aethernetTries = 0;
        _aethernetNextTry = 0;

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

        // Ein abgelehnter Teleport ist meist voruebergehend: im Kampf, beim
        // Reiten, waehrend eines Gespraechs oder unmittelbar nach dem Schliessen
        // eines Fensters. Frueher scheiterte der Halt daran sofort — bei einer
        // Route hiess das, einen ganzen Haendler auszulassen.
        //
        // Warum Lifestream ablehnt, sagt die Schnittstelle nicht. Deshalb steht
        // der Zustand des Charakters im Protokoll: Daran ist ablesbar, was
        // gerade blockiert, statt es zu raten.
        if (!TryTeleport(vendor))
        {
            _step = Step.WaitingToTeleport;
            _deadline = Environment.TickCount64 + TeleportRetryWindowMs;
            _nextActionAt = Pacing.Next(TeleportRetryMs);
            Status = $"Waiting to teleport to {vendor.AetheryteName}.";
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
            case Step.WaitingToTeleport:
                if (Environment.TickCount64 > _deadline)
                {
                    Fail($"Lifestream would not teleport to {_target.AetheryteName}. {Blockers()}");
                    return;
                }

                if (Environment.TickCount64 < _nextActionAt)
                    return;

                if (!TryTeleport(_target))
                {
                    _nextActionAt = Pacing.Next(TeleportRetryMs);
                    return;
                }

                Status = $"Teleporting to {_target.AetheryteName}.";
                _step = Step.Teleporting;
                _deadline = Environment.TickCount64 + ZoneTimeoutMs;
                return;

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

                // Lifestream nimmt den Sprung nur an, wenn der Charakter im
                // Aethernet-Netz steht. Direkt nach dem Ausloesen des Teleports
                // ist er das noch nicht — er steht bis zum Ladebildschirm im
                // Ausgangsgebiet, und von dort wird abgelehnt.
                //
                // Auf einen Gebietswechsel zu warten waere falsch: Steht man
                // bereits in der Stadt, findet keiner statt. Deshalb wird es
                // schlicht wiederholt, bis es angenommen wird.
                if (Environment.TickCount64 < _aethernetNextTry)
                    return;

                if (_aethernetTries >= AethernetMaxTries)
                {
                    Fail($"Lifestream would not take the aethernet hop to {_target.Zone} " +
                         $"(id {_target.AethernetId}) after {_aethernetTries} tries.");
                    return;
                }

                _aethernetTries++;
                _aethernetNextTry = Environment.TickCount64 + AethernetRetryMs;

                var accepted = false;
                try
                {
                    accepted = _lsAethernet.InvokeFunc(_target.AethernetId);
                }
                catch (Exception ex)
                {
                    Fail($"Aethernet hop not available: {ex.Message}");
                    return;
                }

                Plugin.Log.Information(
                    $"[MasterBaiter] Aethernet hop to {_target.Zone} via id {_target.AethernetId}, " +
                    $"from territory {Plugin.ClientState.TerritoryType}, try {_aethernetTries}: " +
                    $"{(accepted ? "accepted" : "refused")}.");

                // Der Rueckgabewert sagt nichts: Lifestream meldet "true" und
                // scheitert danach intern mit "Destination could not be found",
                // wenn der Charakter noch nicht im Aethernet-Netz steht. Ob der
                // Sprung wirklich gelungen ist, zeigt allein der Gebietswechsel
                // — darauf wartet der naechste Schritt.
                _ = accepted;

                Status = "Taking the aethernet.";
                _step = Step.WaitingForDistrict;
                _deadline = Environment.TickCount64 + ZoneTimeoutMs;
                break;

            case Step.WaitingForDistrict:
                if (Plugin.ClientState.TerritoryType != _target.Territory)
                {
                    // Nichts passiert? Dann war der Sprung zu frueh. Zurueck in
                    // den vorigen Schritt, der ihn erneut ausloest — inzwischen
                    // duerfte der Teleport angekommen sein.
                    if (Environment.TickCount64 >= _aethernetNextTry
                        && _aethernetTries < AethernetMaxTries)
                        _step = Step.WaitingForZone;

                    return;
                }
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

                // Erst wenn der Charakter wirklich in der Welt steht.
                //
                // Nach einem Teleport meldet das Spiel den Gebietswechsel, bevor
                // es den Charakter absetzt: Seine Position ist dann noch der
                // Nullpunkt der Karte. Ein Weg, der von dort aus gerechnet wird,
                // ist ein anderer als der, den er braucht — im Protokoll stand
                // "Kicking off pathfind from <0. 0. 0>", und der Charakter ist
                // danach zehn Sekunden lang gegen den Aetherkristall gelaufen,
                // bis ihn die Steckenerkennung aufgehalten hat.
                //
                // Der Gebietswechsel ist also nicht das Ankommen. Zweihundert
                // Millisekunden lagen dazwischen.
                if (!Placed())
                {
                    Status = "Waiting for the character to arrive.";
                    return;
                }

                // Steht der NPC schon in der Objektliste, ist seine
                // tatsaechliche Position besser als die hinterlegte: Die ist
                // aufgeschrieben, teils mit geschaetzter Hoehe, und der NPC
                // steht da, wo er steht.
                var live = LiveTargetPosition();
                var destination = live ?? _target.World;

                if (_approachRetries > 0)
                    destination = NextSpot(destination);

                // Den Boden nur suchen, wenn die Hoehe wirklich geraten ist.
                //
                // <c>ApproximateHeight</c> beschreibt den Tabelleneintrag, nicht
                // die Ablesung: Steht der NPC in der Objektliste, ist seine Hoehe
                // gemessen, und sie durch eine Bodensuche zu ersetzen heisst,
                // eine Auskunft gegen eine Schaetzung zu tauschen.
                //
                // In Tuliyollal hat das einen Halt gekostet. Goplu steht auf
                // y -0,97; die Bodensuche mit zehn Einheiten Spielraum fand das
                // Deck darunter auf y -9,75. Der Charakter lief brav dorthin,
                // stand neun Einheiten unter dem Haendler und meldete
                // "12,9 away, too far to interact" — sechsmal, bis der Halt
                // ausfiel. Der Weg war richtig, das Stockwerk nicht.
                if (live == null && _target.ApproximateHeight)
                    destination = OnMesh(destination);

                // Liegt der Punkt ueberhaupt auf begehbarem Netz? Ein Platz
                // hinter einem Verkaufsstand tut es nicht, und ihn anzulaufen
                // kostet einen Versuch, um am Ende dort zu stehen, wo man schon
                // stand.
                if (_approachRetries > 0 && !Reachable(destination))
                {
                    Trace.Say($"Approach spot {_approachRetries + 1} for {_target.Npc} is not on " +
                              "reachable ground, skipping it.");

                    if (TryAnotherSpot("spot not on the mesh"))
                        return;

                    Fail($"No reachable spot near {_target.Npc}.");
                    return;
                }

                _destination = destination;
                _tried.Add(destination);
                Trace.Say($"Walking to {_target.Npc}: " +
                          $"({destination.X:0.0}, {destination.Y:0.0}, {destination.Z:0.0}), " +
                          (live is { } l
                              ? $"live position, height {l.Y:0.0}"
                              : $"stored position, height {(_target.ApproximateHeight ? "guessed" : "known")}") +
                          $", attempt {_approachRetries + 1}.");

                // Fliegen nur, wo es freigeschaltet ist — sonst plant vnavmesh
                // einen Weg durch die Luft, den der Charakter nicht nehmen kann.
                var fly = _config.UseFlight && _config.UseMount && Flight.AllowedHere;

                bool started;
                try { started = _navMoveCloseTo.InvokeFunc(destination, fly, ArrivalRange); }
                catch (Exception ex) { Fail($"vnavmesh not available: {ex.Message}"); return; }

                if (!started)
                {
                    Fail("vnavmesh found no path.");
                    return;
                }

                _step = Step.Walking;
                _loggedPath = false;
                _walkStartedAt = Environment.TickCount64;
                MarkMoved();
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
                {
                    // Die Laenge des Weges, einmal je Lauf. Ein Weg aus zwei
                    // Punkten quer durch eine Stadt ist keiner, und das sieht
                    // man nur an dieser Zahl.
                    if (!_loggedPath)
                    {
                        _loggedPath = true;
                        try { Trace.Say($"Path to {_target.Npc}: {_navWaypoints.InvokeFunc()} waypoint(s)."); }
                        catch { /* aeltere Fassung ohne diese Abfrage */ }
                    }

                    if (!Stuck())
                        return;

                    // Der Weg laeuft, der Charakter nicht. Weiterzuwarten
                    // heisst, gegen dasselbe Hindernis zu druecken, bis die
                    // Frist ablaeuft.
                    try { _navStop.InvokeAction(); } catch { /* dann eben nicht */ }

                    if (TryAnotherSpot("stuck on the way"))
                        return;

                    Fail($"Stuck on the way to {_target.Npc}.");
                    return;
                }

                // Ein beendeter Weg ist kein angekommener Charakter.
                //
                // vnavmesh raeumt seine Wegpunkte auch weg, wenn das
                // Navigationsnetz neu geladen wird — und das passiert direkt
                // nach jedem Gebietswechsel. Von aussen sieht das aus wie
                // "fertig gelaufen": Im Protokoll stand "Walked to Summoning
                // bell in 1,1 s", und der Charakter war vierunddreissig Meter
                // entfernt. Danach wurde ein Ausweichpunkt verbraucht fuer ein
                // Problem, das keiner war.
                var away = DistanceTo(_destination);
                if (away is { } gap && gap > PathLostDistance && _repaths < MaxRepaths)
                {
                    _repaths++;
                    Plugin.Log.Warning(
                        $"[MasterBaiter] The path to {_target.Npc} ended {gap:0.0} away. " +
                        $"Walking it again ({_repaths} of {MaxRepaths}) — the same spot, because " +
                        "the spot was not the problem.");

                    Status = $"Walking to {_target.Npc}.";
                    _step = Step.Pathfinding;
                    _nextActionAt = Pacing.Next(600);
                    return;
                }

                // Die Dauer mitschreiben: Ein Weg, der aus dem Ruder laeuft,
                // sieht im Protokoll sonst genauso aus wie ein kurzer.
                Plugin.Log.Information(
                    $"[MasterBaiter] Walked to {_target.Npc} in " +
                    $"{(Environment.TickCount64 - _walkStartedAt) / 1000.0:0.0} s" +
                    (away is { } d ? $", {d:0.0} from where it was sent." : ".") );

                // Fliegend laesst sich niemand ansprechen. vnavmesh fliegt bis
                // auf Reichweite heran und bleibt dort in der Luft stehen; von
                // dort sah jeder Versuch aus wie ein Haendler, der nicht
                // antwortet — sechsmal "Talked at 3,5 distance", sechsmal kein
                // Fenster, danach fuenf Ausweichpunkte. Der Weg war richtig,
                // nur die Hoehe nicht.
                Status = $"Landing at {_target.Npc}.";
                _step = Step.Landing;
                _landAttempts = 0;
                _nextActionAt = Pacing.Next(300);
                _deadline = Environment.TickCount64 + LandTimeoutMs;
                break;

            case Step.Landing:
                if (Environment.TickCount64 < _nextActionAt)
                    return;

                if (!Mount.Flying || _landAttempts >= MaxLandAttempts)
                {
                    if (_landAttempts >= MaxLandAttempts && Mount.Flying)
                        Plugin.Log.Warning(
                            "[MasterBaiter] Still in the air after " +
                            $"{MaxLandAttempts} attempts to dismount. Talking anyway.");

                    Status = $"Talking to {_target.Npc}.";
                    _step = Step.Interacting;
                    // Nach dem Absitzen faellt der Charakter noch. Wer sofort
                    // anspricht, spricht im Fallen.
                    _nextActionAt = Pacing.Next(_landAttempts > 0 ? 1600 : 600);
                    _deadline = Environment.TickCount64 + InteractTimeoutMs;
                    return;
                }

                _landAttempts++;
                if (Mount.Dismount())
                    Plugin.Log.Information($"[MasterBaiter] Landing at {_target.Npc}.");

                _nextActionAt = Pacing.Next(900);
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
                    if (TryAnotherSpot("out of reach"))
                        return;

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
                    // Zweimal vergeblich angesprochen heisst nicht zwingend,
                    // dass der NPC zu weit weg ist — das Spiel verweigert das
                    // Gespraech auch bei verstelltem Blick. Dann hilft nur ein
                    // anderer Standpunkt, nicht ein weiterer Versuch von hier.
                    if (++_talkAttempts > 2 && TryAnotherSpot("no window opened"))
                        return;

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
            // Eine Rufglocke oeffnet keine Ladentheke, sondern die
            // Gehilfenliste. Ohne diesen Fall gilt die Ankunft als
            // gescheitert, und die Reise spricht die Glocke wieder und wieder
            // an, waehrend das Fenster laengst offen steht.
            if (SummoningBells.IsBell(_target.DataId))
                return RetainerList.IsOpen;

            return MarketBoards.IsBoard(_target.DataId) ? MarketBoard.IsOpen : ShopWindowReader.IsOpen;
        }
    }

    /// <summary>Sucht den Haendler in der Objektliste und spricht ihn an.</summary>
    /// <summary>Merkt sich, dass der Charakter gerade noch vorangekommen ist.</summary>
    private void MarkMoved()
    {
        _lastPosition = Plugin.ObjectTable.LocalPlayer?.Position ?? Vector3.Zero;
        _movedAt = Environment.TickCount64;
    }

    /// <summary>
    /// Steht der Charakter, obwohl er laufen sollte?
    ///
    /// Gemessen wird die Strecke, nicht die Geschwindigkeit: Ein Zwischenbild
    /// im Stillstand ist normal, fuenf Sekunden ohne einen Meter nicht.
    /// </summary>
    private bool Stuck()
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        if (me == null)
            return false;

        if (Vector3.Distance(me.Position, _lastPosition) > StuckDistance)
        {
            MarkMoved();
            return false;
        }

        return Environment.TickCount64 - _movedAt > StuckMs;
    }

    /// <summary>
    /// Sucht sich einen anderen Standpunkt vor demselben NPC.
    ///
    /// Gibt false zurueck, wenn alle durchprobiert sind — dann ist es kein
    /// Standortproblem mehr und der Aufrufer soll aufgeben.
    /// </summary>
    private bool TryAnotherSpot(string why)
    {
        if (_approachRetries >= ApproachSpots)
            return false;

        _approachRetries++;
        _talkAttempts = 0;

        Plugin.Log.Information(
            $"[MasterBaiter] {_target.Npc} {why}, trying approach {_approachRetries} of {ApproachSpots}.");
        Status = _approachRetries == 1
            ? $"Getting closer to {_target.Npc}."
            : $"Trying another spot at {_target.Npc}.";

        _step = Step.Pathfinding;
        _deadline = Environment.TickCount64 + WalkTimeoutMs;
        return true;
    }

    /// <summary>
    /// Ein Punkt auf einem Kreis um das Ziel. Der erste Versuch laeuft den NPC
    /// unmittelbar an, jeder weitere stellt sich woanders hin.
    /// </summary>
    /// <summary>
    /// Der naechste Ausweichpunkt, der nicht schon einmal angelaufen wurde.
    ///
    /// Die blosse Winkelrechnung genuegte nicht: Weil sich der Mittelpunkt
    /// zwischen den Versuchen aendert — mal die gemessene Position des NPC, mal
    /// die gespeicherte —, kamen bei Goplu dreimal hintereinander genau
    /// dieselben Koordinaten heraus. Der Charakter stand bereits dort, vnavmesh
    /// meldete nach einer Sekunde "angekommen", und die letzten drei von sechs
    /// Versuchen waren aufgebraucht, ohne dass je ein neuer Platz probiert
    /// wurde.
    ///
    /// Ein Wiederholungsversuch, der dasselbe tut, ist keiner.
    /// </summary>
    private Vector3 NextSpot(Vector3 centre)
    {
        for (var attempt = _approachRetries; attempt <= ApproachSpots; attempt++)
        {
            var spot = OffsetSpot(centre, attempt);
            if (_tried.All(t => Vector3.Distance(t, spot) > SameSpotDistance))
                return spot;
        }

        // Alle belegt: dann eben der vorgesehene. Besser ein wiederholter
        // Versuch als gar keiner.
        return OffsetSpot(centre, _approachRetries);
    }

    /// <summary>
    /// Steht der Charakter in der Welt, oder ist er noch unterwegs dorthin?
    ///
    /// Der Nullpunkt ist das Zeichen fuer "noch nicht abgesetzt". Eine echte
    /// Position genau dort gibt es in keinem Gebiet dieses Spiels, und selbst
    /// wenn: eine Sekunde spaeter zu laufen kostet weniger als ein Weg vom
    /// falschen Anfang.
    /// </summary>
    private static bool Placed()
    {
        if (Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas]
            || Plugin.Condition[Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas51])
            return false;

        var me = Plugin.ObjectTable.LocalPlayer;
        return me != null && me.Position.LengthSquared() > 0.01f;
    }

    /// <summary>
    /// Wie weit der Charakter von einem Punkt entfernt ist, oder null, wenn
    /// sich das gerade nicht sagen laesst.
    /// </summary>
    private static float? DistanceTo(Vector3 point)
    {
        var me = Plugin.ObjectTable.LocalPlayer;
        return me == null || point == Vector3.Zero ? null : Vector3.Distance(me.Position, point);
    }

    /// <summary>
    /// Liegt dieser Punkt auf begehbarem, erreichbarem Netz?
    ///
    /// Unbekannt zaehlt als ja: Ein falsches Nein liesse einen Ausweichpunkt
    /// aus, der getaugt haette, und vnavmesh sagt ohnehin selbst Bescheid,
    /// wenn es keinen Weg findet.
    /// </summary>
    private bool Reachable(Vector3 point)
    {
        try { return _navOnMesh.InvokeFunc(point, 5f, false); }
        catch { return true; }
    }

    /// <summary>
    /// Legt einen geschaetzten Punkt auf das Navigationsnetz.
    ///
    /// <c>NearestPointReachable</c> sucht in einem begrenzten Kaestchen um den
    /// Punkt herum. Die Bodensuche, die hier frueher stand, tut etwas anderes:
    /// Sie nimmt den hoechsten Boden <b>unterhalb</b> des Punktes und sucht
    /// dafuer ueber die gesamte Saeule — vnavmesh setzt die senkrechte
    /// Ausdehnung intern auf 2048. In Tuliyollal fand sie damit das Deck
    /// achteinhalb Meter tiefer, und der Charakter stand unter dem Haendler.
    ///
    /// Der Zahlenwert, den wir dort uebergaben, war nie eine Hoehentoleranz,
    /// sondern die Ausdehnung in der Ebene. Die Bodensuche bleibt als
    /// Rueckfall, falls im Kaestchen nichts liegt — dann ist irgendein Boden
    /// besser als keiner.
    /// </summary>
    private Vector3 OnMesh(Vector3 point)
    {
        try
        {
            if (_navNearestReachable.InvokeFunc(point, 5f, 5f) is { } near)
                return near;
        }
        catch { /* aeltere Fassung ohne diese Abfrage */ }

        try
        {
            if (_navPointOnFloor.InvokeFunc(point, false, 10f) is { } floor)
            {
                Trace.Say($"No reachable ground within 5 of {_target.Npc}; " +
                          $"fell back to the floor search, which landed {point.Y - floor.Y:0.0} lower.");
                return floor;
            }
        }
        catch { /* dann eben ohne */ }

        return point;
    }

    private static Vector3 OffsetSpot(Vector3 centre, int attempt)
    {
        var angle = MathF.Tau * (attempt - 1) / ApproachSpots;
        return centre with
        {
            X = centre.X + (MathF.Cos(angle) * ApproachOffset),
            Z = centre.Z + (MathF.Sin(angle) * ApproachOffset),
        };
    }

    /// <summary>Wo der Ziel-NPC gerade wirklich steht, falls er geladen ist.</summary>
    private Vector3? LiveTargetPosition()
    {
        foreach (var obj in Plugin.ObjectTable)
        {
            if (obj.ObjectKind is not (Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventNpc
                                       or Dalamud.Game.ClientState.Objects.Enums.ObjectKind.EventObj))
                continue;

            var matches = _target.DataId != 0
                ? obj.BaseId == _target.DataId
                : string.Equals(obj.Name.TextValue, _target.Npc, StringComparison.OrdinalIgnoreCase);

            if (matches)
                return obj.Position;
        }

        return null;
    }

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

    /// <summary>
    /// Loest den Teleport aus. Gibt false zurueck, wenn Lifestream ablehnt —
    /// und schreibt dann auf, was am Charakter gerade dagegen sprechen koennte.
    /// </summary>
    private bool TryTeleport(VendorIndex.Vendor vendor)
    {
        // Lifestream lehnt ab, solange es noch mit etwas anderem beschaeftigt
        // ist — und genau das ist der Grund, warum der erste Versuch jedes Mal
        // scheiterte: Er kam unmittelbar nach dem Schliessen des Ladenfensters
        // oder dem Ende der vorigen Fahrt. Die Zustandsflags des Charakters
        // zeigen davon nichts, deshalb stand dort "nothing obvious".
        try
        {
            if (_lsBusy.InvokeFunc())
            {
                // Auf Information, nicht Debug: Sonst laesst sich nicht
                // unterscheiden, ob die Pruefung gegriffen hat oder Lifestream
                // aus einem anderen Grund ablehnt.
                Plugin.Log.Information("[MasterBaiter] Lifestream is still busy; waiting.");
                return false;
            }
        }
        catch
        {
            // Antwortet es nicht, wird der Teleport es zeigen.
        }

        try
        {
            if (_lsTeleport.InvokeFunc(vendor.AetheryteId, 0))
                return true;
        }
        catch (Exception ex)
        {
            Fail($"Lifestream not available: {ex.Message}");
            return false;
        }

        Plugin.Log.Information(
            $"[MasterBaiter] Lifestream refused the teleport to {vendor.AetheryteName}. {Blockers()}");
        return false;
    }

    /// <summary>Was am Charakter gerade einen Teleport verhindern koennte.</summary>
    private static string Blockers()
    {
        var conditions = Plugin.Condition;
        var flags = new[]
        {
            Dalamud.Game.ClientState.Conditions.ConditionFlag.InCombat,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.Casting,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.Mounted,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.Occupied33,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInEvent,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.OccupiedInQuestEvent,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.BetweenAreas,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.BoundByDuty,
            Dalamud.Game.ClientState.Conditions.ConditionFlag.Jumping,
        };

        var active = flags.Where(f => conditions[f]).Select(f => f.ToString()).ToList();
        return active.Count == 0
            ? "Nothing obvious is blocking it."
            : $"Active: {string.Join(", ", active)}.";
    }

    private void Arrive()
    {
        _step = Step.Done;
        Status = $"Shop open at {_target.Npc}.";
        Plugin.Log.Information($"[MasterBaiter] Arrived at {_target.Npc}, its window is open.");
        Arrived?.Invoke();
    }

    private void Fail(string reason)
    {
        _step = Step.Failed;
        Status = reason;
        Plugin.Log.Warning($"[MasterBaiter] Travel failed: {reason}");
    }
}
